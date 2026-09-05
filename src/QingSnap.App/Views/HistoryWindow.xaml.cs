using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using QingSnap.App.Controls;
using QingSnap.App.Models;
using QingSnap.App.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfCursors = System.Windows.Input.Cursors;
using WpfImage = System.Windows.Controls.Image;

namespace QingSnap.App.Views;

public partial class HistoryWindow : Window
{
    private const int PageSize = 80;
    private const double PrefetchRemainingItems = 20;

    private readonly CaptureHistoryService _historyService;
    private readonly HistoryOcrIndexingService _historyOcrIndexer;
    private readonly ClipboardService _clipboardService;
    private readonly Action<string> _pinImage;
    private readonly Action<string> _recognizeImage;
    private readonly QrCodeService _qrCodeService;
    private CancellationTokenSource? _refreshCancellation;
    private readonly CancellationTokenSource _windowCancellation = new();
    private CancellationTokenSource? _qrCodeCancellation;
    private readonly ThumbnailLruCache _thumbnailCache = new();
    private readonly DispatcherTimer _searchDebounceTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };
    private int _queryVersion;
    private int _nextOffset;
    private int _totalMatched;
    private long _totalBytes;
    private bool _hasMore;
    private bool _isReady;
    private bool _isLoadingHistory;
    private bool _isUpdatingTagFilter;
    private bool _isBatchOperation;
    private bool _isGridView;
    private int _gridColumnCount;
    private bool _isSynchronizingSelection;
    private readonly HistorySelectionState _selection = new();
    private HistoryOcrIndexProgress? _indexProgress;

    public HistoryWindow(
        CaptureHistoryService historyService,
        HistoryOcrIndexingService historyOcrIndexer,
        ClipboardService clipboardService,
        Action<string> pinImage,
        Action<string> recognizeImage,
        QrCodeService qrCodeService)
    {
        _historyService = historyService;
        _historyOcrIndexer = historyOcrIndexer;
        _clipboardService = clipboardService;
        _pinImage = pinImage;
        _recognizeImage = recognizeImage;
        _qrCodeService = qrCodeService;
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
        SizeChanged += OnHistoryWindowSizeChanged;
        _searchDebounceTimer.Tick += OnSearchDebounceTick;
        _historyOcrIndexer.ProgressChanged += OnIndexProgressChanged;
        _historyService.MetadataIndexRefreshed += OnMetadataIndexRefreshed;
        Closed += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _windowCancellation.Cancel();
            _windowCancellation.Dispose();
            _thumbnailCache.Dispose();
            _qrCodeCancellation?.Cancel();
            _qrCodeCancellation?.Dispose();
            _historyOcrIndexer.ProgressChanged -= OnIndexProgressChanged;
            _historyService.MetadataIndexRefreshed -= OnMetadataIndexRefreshed;
            ResourceDiagnostics.Sample("HistoryClosed");
        };
    }

    public ObservableCollection<HistoryItem> VisibleItems { get; } = [];

    public ObservableCollection<HistoryGridRow> GridRows { get; } = [];

    public void RefreshHistory() => _ = ReloadHistoryAsync(refreshTags: true);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResourceDiagnostics.Sample("HistoryOpened", ("Thumb", _thumbnailCache.Count));
        _isReady = true;
        RefreshHistory();
        _historyOcrIndexer.ScheduleBackfill();
    }

    private void OnMetadataIndexRefreshed(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isReady && IsVisible)
            {
                _ = ReloadHistoryAsync(refreshTags: true);
            }
        });
    }

    private async Task ReloadHistoryAsync(bool refreshTags = false)
    {
        CancellationToken cancellationToken = default;
        try
        {
            _refreshCancellation?.Cancel();
            _refreshCancellation?.Dispose();
            _refreshCancellation = new CancellationTokenSource();
            cancellationToken = _refreshCancellation.Token;
            var requestVersion = ++_queryVersion;
            _isLoadingHistory = false;

            LoadingPanel.Visibility = Visibility.Visible;
            StatusText.Text = "正在读取截图记录…";
            _selection.Clear();
            VisibleItems.Clear();
            GridRows.Clear();
            _totalMatched = 0;
            _totalBytes = 0;
            _nextOffset = 0;
            _hasMore = true;
            RestoreSelectionToControls();
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (refreshTags)
            {
                await RefreshTagFilterAsync(cancellationToken);
            }

            await LoadNextPageAsync(requestVersion, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("History", exception, "Failed to load history snapshot.");
            if (IsLoaded)
            {
                StatusText.Text = $"读取失败：{exception.Message}";
                EmptyState.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (IsLoaded && !cancellationToken.IsCancellationRequested)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    private HistoryQuery CreateQuery(int offset) => new(
        offset,
        PageSize,
        SearchBox.Text?.Trim() ?? string.Empty,
        (HistoryFilterKind)Math.Clamp(DateFilter.SelectedIndex, 0, 4),
        (TagFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
        IncludeStatistics: offset == 0);

    private async Task LoadNextPageAsync(int requestVersion, CancellationToken cancellationToken)
    {
        if (!_isReady || _isLoadingHistory || !_hasMore || requestVersion != _queryVersion)
        {
            return;
        }

        _isLoadingHistory = true;
        try
        {
            var page = await _historyService.QueryHistoryAsync(CreateQuery(_nextOffset), cancellationToken);
            if (cancellationToken.IsCancellationRequested || requestVersion != _queryVersion)
            {
                return;
            }

            var existingIds = VisibleItems.Select(item => item.MetadataId).ToHashSet();
            foreach (var item in page.Items)
            {
                if (existingIds.Add(item.MetadataId))
                {
                    VisibleItems.Add(item);
                }
            }

            if (page.TotalCount >= 0)
            {
                _totalMatched = page.TotalCount;
                _totalBytes = page.TotalBytes;
            }
            _nextOffset += page.Items.Count;
            _hasMore = page.HasMore && page.Items.Count > 0;
            HeaderStatsText.Text = $"共 {_totalMatched:N0} 张 · {FormatBytes(_totalBytes)}";
            RebuildGridRows(force: true);
            RestoreSelectionToControls();
            EmptyState.Visibility = VisibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = _hasMore
                ? $"已载入 {VisibleItems.Count:N0} / {_totalMatched:N0} 张 · 向下滚动继续加载"
                : $"当前显示 {VisibleItems.Count:N0} / {_totalMatched:N0} 张 · 双击预览图可打开原图";
            ApplyIndexProgressStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("History", exception, "历史分页查询失败。");
            StatusText.Text = $"加载下一页失败：{exception.Message} · 继续滚动可重试";
            _hasMore = true;
        }
        finally
        {
            if (requestVersion == _queryVersion)
            {
                _isLoadingHistory = false;
            }
        }
    }

    private void OnIndexProgressChanged(object? sender, HistoryOcrIndexProgress progress)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_isReady)
            {
                return;
            }

            _indexProgress = progress;
            if (progress.PendingCount == 0 &&
                progress.CompletedFilePath is not null &&
                !string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                ScheduleQueryReload();
            }

            ApplyIndexProgressStatus();
        });
    }

    private void ApplyIndexProgressStatus()
    {
        if (_indexProgress is { IsOcrAvailable: false })
        {
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                StatusText.Text = "OCR 组件未安装；当前只能搜索已经建立过文字索引的截图";
            }

            return;
        }

        if (_indexProgress is { PendingCount: > 0 } progress)
        {
            StatusText.Text = $"正在后台识字找图 · 剩余 {progress.PendingCount:N0} 张 · 已完成 {progress.IndexedCount:N0} 张";
        }
        else if (_indexProgress is { IndexedCount: > 0 } completed)
        {
            StatusText.Text = $"文字索引已更新 · 本次完成 {completed.IndexedCount:N0} 张";
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScheduleQueryReload();
    }

    private void OnDateFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isReady)
        {
            _ = ReloadHistoryAsync();
        }
    }

    private void OnTagFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isReady && !_isUpdatingTagFilter)
        {
            _ = ReloadHistoryAsync();
        }
    }

    private void ScheduleQueryReload()
    {
        if (!_isReady)
        {
            return;
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        _ = ReloadHistoryAsync();
    }

    private async void OnHistoryScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_hasMore || _isLoadingHistory || e.ExtentHeight <= 0)
        {
            return;
        }

        if (e.ExtentHeight - e.VerticalOffset - e.ViewportHeight <= PrefetchRemainingItems)
        {
            var cancellation = _refreshCancellation;
            if (cancellation is not null)
            {
                await LoadNextPageAsync(_queryVersion, cancellation.Token);
            }
        }
    }

    private async void OnThumbnailLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfImage image || !TryResolveHistoryItem(image.DataContext, out var item))
        {
            return;
        }

        image.Source = null;
        try
        {
            var thumbnail = await _thumbnailCache.GetAsync(
                item.FilePath,
                ThumbnailLruCache.DefaultDecodePixelWidth,
                _windowCancellation.Token);
            if (!TryResolveHistoryItem(image.DataContext, out var current) || current.MetadataId != item.MetadataId)
            {
                return;
            }

            image.Source = thumbnail;
            if (thumbnail is null && !File.Exists(item.FilePath))
            {
                await _historyService.RemoveMissingMetadataAsync(item.FilePath, _windowCancellation.Token);
                ScheduleQueryReload();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnThumbnailUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is WpfImage image)
        {
            image.Source = null;
        }
    }

    private void OnThumbnailDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is WpfImage image && image.IsLoaded)
        {
            OnThumbnailLoaded(image, new RoutedEventArgs());
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _thumbnailCache.Clear();
        RefreshHistory();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => _historyService.OpenHistoryDirectory();

    private void OnOpenItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: HistoryItem item })
        {
            OpenItem(item);
        }
    }

    private async void OnCopyItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: HistoryItem item })
        {
            return;
        }

        try
        {
            await _clipboardService.CopyImageAsync(_historyService.LoadFullImage(item.FilePath));
            StatusText.Text = $"已复制：{item.FileName}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"复制失败：{exception.Message}";
        }
    }

    private void OnPinItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: HistoryItem item })
        {
            return;
        }

        _pinImage(item.FilePath);
        StatusText.Text = $"已贴到桌面：{item.FileName}";
    }

    private void OnFavoriteItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: HistoryItem item })
        {
            return;
        }

        var isFavorite = _historyService.ToggleFavorite(item.FilePath);
        StatusText.Text = isFavorite ? $"已收藏：{item.FileName}" : $"已取消收藏：{item.FileName}";
        RefreshHistory();
    }

    private void OnOcrItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: HistoryItem item })
        {
            return;
        }

        _recognizeImage(item.FilePath);
        StatusText.Text = $"正在识别：{item.FileName}";
    }

    private async void OnDeleteItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: HistoryItem item })
        {
            return;
        }

        await DeleteItemsAsync([item]);
    }

    private void OnHistoryItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || !TryResolveHistoryItem(border.DataContext, out var item))
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            OpenItem(item);
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        _selection.Select(
            item.MetadataId,
            VisibleItems.Select(value => value.MetadataId).ToArray(),
            toggle: modifiers.HasFlag(ModifierKeys.Control),
            range: modifiers.HasFlag(ModifierKeys.Shift));
        RestoreSelectionToControls();
        e.Handled = true;
    }

    private void OnHistoryAreaMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || _selection.Count == 0)
        {
            return;
        }

        _selection.Clear();
        RestoreSelectionToControls();
    }

    private void OnHistoryItemMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || !TryResolveHistoryItem(border.DataContext, out var item))
        {
            return;
        }

        if (!_selection.Contains(item.MetadataId))
        {
            _selection.SelectOnly(item.MetadataId);
            RestoreSelectionToControls();
        }

        var selectedItems = GetSelectedVisibleItems();

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("HistoryContextMenuStyle"),
            PlacementTarget = border,
            Placement = PlacementMode.MousePoint
        };
        var suffix = selectedItems.Count > 1 ? $"（{selectedItems.Count} 张）" : string.Empty;
        var qrCodeItem = CreateTagMenuItem("识别二维码", item, isEnabled: true);
        qrCodeItem.Click += async (_, _) => await RecognizeHistoryQrCodeAsync(border, item);
        var addItem = CreateTagMenuItem($"添加标签{suffix}…", item, isEnabled: true);
        addItem.Click += async (_, _) => await EditTagsAsync(selectedItems, HistoryTagEditMode.Add);
        var removeItem = CreateTagMenuItem(
            $"移除标签{suffix}…",
            item,
            selectedItems.Any(value => value.HasTags));
        removeItem.Click += async (_, _) => await EditTagsAsync(selectedItems, HistoryTagEditMode.Remove);
        menu.Items.Add(qrCodeItem);
        menu.Items.Add(addItem);
        menu.Items.Add(removeItem);
        border.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private async Task RecognizeHistoryQrCodeAsync(Border card, HistoryItem item)
    {
        _qrCodeCancellation?.Cancel();
        _qrCodeCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _qrCodeCancellation = cancellation;
        StatusText.Text = $"正在识别二维码：{item.FileName}";

        try
        {
            var image = _historyService.LoadFullImage(item.FilePath);
            var results = await _qrCodeService.RecognizeAsync(image, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_qrCodeCancellation, cancellation))
            {
                return;
            }

            if (!TryResolveHistoryItem(card.DataContext, out var currentItem) ||
                currentItem.MetadataId != item.MetadataId)
            {
                return;
            }

            if (results.Count == 0)
            {
                StatusText.Text = $"未检测到二维码：{item.FileName}";
                return;
            }

            var preview = FindVisualDescendant<WpfImage>(card);
            var adornerLayer = preview is null ? null : AdornerLayer.GetAdornerLayer(preview);
            if (preview is null || adornerLayer is null)
            {
                StatusText.Text = "二维码已识别，但当前缩略图暂时无法显示热点，请滚动后重试。";
                return;
            }

            foreach (var existing in adornerLayer.GetAdorners(preview) ?? [])
            {
                if (existing is QrCodeHotspotAdorner)
                {
                    adornerLayer.Remove(existing);
                }
            }

            var adorner = new QrCodeHotspotAdorner(
                preview,
                results,
                image.PixelWidth,
                image.PixelHeight);
            adorner.Layer.ResultInvoked += OnHistoryQrCodeHotspotInvoked;
            adornerLayer.Add(adorner);
            StatusText.Text = $"已找到 {results.Count:N0} 个二维码 · 悬停查看，单击使用";
            DiagnosticLog.Info(
                "QrCode",
                $"历史缩略图二维码热点已显示：{results.Count:N0} 个结果，文件 {item.FileName}。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("QrCode", exception, $"历史截图二维码识别失败：{item.FileName}");
            StatusText.Text = "二维码识别失败，请换一张清晰图片重试。";
        }
        finally
        {
            if (ReferenceEquals(_qrCodeCancellation, cancellation))
            {
                _qrCodeCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async void OnHistoryQrCodeHotspotInvoked(QrCodeResult result)
    {
        try
        {
            StatusText.Text = await QrCodeInteractionService.InvokeAsync(result, _clipboardService);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("QrCode", exception, "执行历史截图二维码热点操作失败。");
            StatusText.Text = result.IsUrl ? "无法打开此链接。" : "复制二维码内容失败。";
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private MenuItem CreateTagMenuItem(string header, HistoryItem item, bool isEnabled) => new()
    {
        Header = header,
        Tag = item,
        IsEnabled = isEnabled,
        Style = (Style)FindResource("HistoryMenuItemStyle")
    };

    private async Task EditTagsAsync(IReadOnlyList<HistoryItem> items, HistoryTagEditMode mode)
    {
        if (items.Count == 0 || _isBatchOperation)
        {
            return;
        }

        try
        {
            var availableTags = await _historyService.LoadAllTagsAsync();
            var currentTags = mode == HistoryTagEditMode.Add
                ? IntersectTags(items)
                : items.SelectMany(item => item.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var dialog = new HistoryTagWindow(mode, availableTags, currentTags)
            {
                Owner = this
            };
            dialog.ShowDialog();
            if (!dialog.Accepted)
            {
                return;
            }

            if (mode == HistoryTagEditMode.Add)
            {
                await RunBatchOperationAsync(
                    $"正在为 {items.Count:N0} 张截图添加标签…",
                    async () => await Task.WhenAll(items.Select(item =>
                        _historyService.AddTagsAsync(item.FilePath, dialog.SelectedTags))),
                    $"已为 {items.Count:N0} 张截图添加标签：{string.Join("、", dialog.SelectedTags)}");
            }
            else
            {
                await RunBatchOperationAsync(
                    $"正在从 {items.Count:N0} 张截图移除标签…",
                    async () => await Task.WhenAll(
                        items.SelectMany(item => dialog.SelectedTags.Select(tag =>
                            _historyService.RemoveTagAsync(item.FilePath, tag)))),
                    $"已从 {items.Count:N0} 张截图移除标签：{string.Join("、", dialog.SelectedTags)}");
            }

            RefreshHistory();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("HistoryTags", exception, "更新截图标签失败。");
            StatusText.Text = $"标签操作失败：{exception.Message}";
        }
    }

    private static IReadOnlyList<string> IntersectTags(IReadOnlyList<HistoryItem> items)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var intersection = new HashSet<string>(items[0].Tags ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Skip(1))
        {
            intersection.IntersectWith(item.Tags ?? []);
        }

        return intersection.OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private IReadOnlyList<HistoryItem> GetSelectedVisibleItems() => VisibleItems
        .Where(item => _selection.Contains(item.MetadataId))
        .ToArray();

    private void RestoreSelectionToControls()
    {
        _isSynchronizingSelection = true;
        try
        {
            SyncSelection(HistoryItemsControl);
            UpdateGridSelection();
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        var selectedCount = GetSelectedVisibleItems().Count;
        BatchSelectionText.Text = $"已选 {selectedCount:N0} 张";
        BatchActionPanel.Visibility = selectedCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SyncSelection(WpfListBox listBox)
    {
        listBox.SelectedItems.Clear();
        foreach (var item in VisibleItems.Where(item => _selection.Contains(item.MetadataId)))
        {
            listBox.SelectedItems.Add(item);
        }
    }

    private static bool TryResolveHistoryItem(object? dataContext, out HistoryItem item)
    {
        if (dataContext is HistoryItem directItem)
        {
            item = directItem;
            return true;
        }

        if (dataContext is HistoryGridCard gridCard)
        {
            item = gridCard.Item;
            return true;
        }

        item = null!;
        return false;
    }

    private int CalculateGridColumnCount()
    {
        var availableWidth = Math.Max(660D, ActualWidth - 40D);
        return Math.Max(1, (int)Math.Floor(availableWidth / 222D));
    }

    private void RebuildGridRows(bool force = false)
    {
        var columnCount = CalculateGridColumnCount();
        if (!force && columnCount == _gridColumnCount)
        {
            return;
        }

        _gridColumnCount = columnCount;
        GridRows.Clear();
        for (var index = 0; index < VisibleItems.Count; index += columnCount)
        {
            var cards = VisibleItems
                .Skip(index)
                .Take(columnCount)
                .Select(item => new HistoryGridCard(item, _selection.Contains(item.MetadataId)))
                .ToArray();
            GridRows.Add(new HistoryGridRow(cards));
        }
    }

    private void UpdateGridSelection()
    {
        foreach (var card in GridRows.SelectMany(row => row.Items))
        {
            card.IsSelected = _selection.Contains(card.Item.MetadataId);
        }
    }

    private void OnHistoryWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isReady)
        {
            return;
        }

        var columnCount = CalculateGridColumnCount();
        if (columnCount == _gridColumnCount)
        {
            return;
        }

        RebuildGridRows(force: true);
        UpdateGridSelection();
    }

    private void OnHistoryListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingSelection || sender is not WpfListBox listBox)
        {
            return;
        }

        _selection.SelectAll(listBox.SelectedItems
            .OfType<HistoryItem>()
            .Select(item => item.MetadataId));
        RestoreSelectionToControls();
    }

    private void OnListViewClick(object sender, RoutedEventArgs e) => SetHistoryViewMode(gridView: false);

    private void OnGridViewClick(object sender, RoutedEventArgs e) => SetHistoryViewMode(gridView: true);

    private void SetHistoryViewMode(bool gridView)
    {
        _isGridView = gridView;
        ListViewButton.IsChecked = !gridView;
        GridViewButton.IsChecked = gridView;
        HistoryItemsControl.Visibility = gridView ? Visibility.Collapsed : Visibility.Visible;
        HistoryGridControl.Visibility = gridView ? Visibility.Visible : Visibility.Collapsed;
        RestoreSelectionToControls();
        StatusText.Text = gridView ? "已切换到图标模式。" : "已切换到列表模式。";
    }

    private async void OnBatchFavoriteClick(object sender, RoutedEventArgs e) =>
        await SetSelectedFavoriteAsync(isFavorite: true);

    private async void OnBatchUnfavoriteClick(object sender, RoutedEventArgs e) =>
        await SetSelectedFavoriteAsync(isFavorite: false);

    private async Task SetSelectedFavoriteAsync(bool isFavorite)
    {
        var items = GetSelectedVisibleItems();
        if (items.Count == 0)
        {
            return;
        }

        var completed = await RunBatchOperationAsync(
            isFavorite
                ? $"正在收藏 {items.Count:N0} 张截图…"
                : $"正在取消收藏 {items.Count:N0} 张截图…",
            () => Task.Run(() => _historyService.SetFavoriteState(
                items.Select(item => item.FilePath),
                isFavorite)),
            isFavorite
                ? $"已收藏 {items.Count:N0} 张截图。"
                : $"已取消收藏 {items.Count:N0} 张截图。");
        if (completed)
        {
            RefreshHistory();
        }
    }

    private async void OnBatchAddTagsClick(object sender, RoutedEventArgs e) =>
        await EditTagsAsync(GetSelectedVisibleItems(), HistoryTagEditMode.Add);

    private async void OnBatchRemoveTagsClick(object sender, RoutedEventArgs e) =>
        await EditTagsAsync(GetSelectedVisibleItems(), HistoryTagEditMode.Remove);

    private async void OnBatchCopyPathsClick(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedVisibleItems();
        if (items.Count == 0 || _isBatchOperation)
        {
            return;
        }

        try
        {
            await _clipboardService.CopyTextAsync(string.Join(Environment.NewLine, items.Select(item => item.FilePath)));
            StatusText.Text = $"已复制 {items.Count:N0} 条文件路径。";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("HistoryBatch", exception, "批量复制截图路径失败。");
            StatusText.Text = $"复制路径失败：{exception.Message}";
        }
    }

    private async void OnBatchDeleteClick(object sender, RoutedEventArgs e) =>
        await DeleteItemsAsync(GetSelectedVisibleItems());

    private async Task DeleteItemsAsync(IReadOnlyList<HistoryItem> items)
    {
        if (items.Count == 0 || _isBatchOperation)
        {
            return;
        }

        var message = items.Count == 1
            ? $"把这张截图移到回收站？\n\n{items[0].FileName}"
            : $"把选中的 {items.Count:N0} 张截图移到回收站？\n\n只确认这一次，图片仍可从 Windows 回收站恢复。";
        var confirmation = System.Windows.MessageBox.Show(
            this,
            message,
            "删除截图",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _isBatchOperation = true;
        BatchActionPanel.IsEnabled = false;
        Mouse.OverrideCursor = WpfCursors.Wait;
        StatusText.Text = $"正在把 {items.Count:N0} 张截图移到回收站…";
        var deletedIds = new List<long>();
        var failures = new List<(string FileName, Exception Error)>();
        try
        {
            await Task.Run(() =>
            {
                for (var index = 0; index < items.Count; index++)
                {
                    var item = items[index];
                    try
                    {
                        _historyService.DeleteToRecycleBin(item.FilePath);
                        _thumbnailCache.Remove(item.FilePath);
                        deletedIds.Add(item.MetadataId);
                    }
                    catch (Exception exception)
                    {
                        failures.Add((item.FileName, exception));
                        DiagnosticLog.Error("HistoryBatch", exception, $"批量删除截图失败：{item.FileName}");
                    }

                    if ((index + 1) % 10 == 0 || index + 1 == items.Count)
                    {
                        var completed = index + 1;
                        _ = Dispatcher.BeginInvoke(() =>
                            StatusText.Text = $"正在移到回收站 · {completed:N0} / {items.Count:N0}");
                    }
                }
            });

            _selection.Remove(deletedIds);
            StatusText.Text = failures.Count == 0
                ? $"已把 {deletedIds.Count:N0} 张截图移到回收站。"
                : $"已删除 {deletedIds.Count:N0} 张，另有 {failures.Count:N0} 张失败。";
            RefreshHistory();
        }
        finally
        {
            _isBatchOperation = false;
            BatchActionPanel.IsEnabled = true;
            Mouse.OverrideCursor = null;
            RestoreSelectionToControls();
        }
    }

    private async Task<bool> RunBatchOperationAsync(
        string progressText,
        Func<Task> operation,
        string completionText)
    {
        if (_isBatchOperation)
        {
            return false;
        }

        _isBatchOperation = true;
        BatchActionPanel.IsEnabled = false;
        Mouse.OverrideCursor = WpfCursors.Wait;
        StatusText.Text = progressText;
        try
        {
            await operation();
            StatusText.Text = completionText;
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("HistoryBatch", exception, progressText);
            StatusText.Text = $"批量操作失败：{exception.Message}";
            return false;
        }
        finally
        {
            _isBatchOperation = false;
            BatchActionPanel.IsEnabled = true;
            Mouse.OverrideCursor = null;
        }
    }

    private async Task RefreshTagFilterAsync(CancellationToken cancellationToken)
    {
        var previousTag = (TagFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        IReadOnlyList<string> tags;
        try
        {
            tags = await _historyService.LoadAllTagsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DiagnosticLog.Error("HistoryTags", exception, "读取历史标签筛选项失败。");
            return;
        }

        _isUpdatingTagFilter = true;
        try
        {
            TagFilter.Items.Clear();
            TagFilter.Items.Add(new ComboBoxItem { Content = "全部标签", Tag = string.Empty });
            foreach (var tag in tags)
            {
                TagFilter.Items.Add(new ComboBoxItem { Content = tag, Tag = tag });
            }

            TagFilter.SelectedItem = TagFilter.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), previousTag, StringComparison.OrdinalIgnoreCase))
                ?? TagFilter.Items[0];
        }
        finally
        {
            _isUpdatingTagFilter = false;
        }
    }

    private void OpenItem(HistoryItem item)
    {
        try
        {
            _historyService.OpenFile(item.FilePath);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"打开失败：{exception.Message}";
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.A &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            Keyboard.FocusedElement is not WpfTextBox)
        {
            _selection.SelectAll(VisibleItems.Select(item => item.MetadataId));
            RestoreSelectionToControls();
            StatusText.Text = $"已选择当前已加载的 {GetSelectedVisibleItems().Count:N0} 张截图。";
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete && GetSelectedVisibleItems().Count > 0)
        {
            await DeleteItemsAsync(GetSelectedVisibleItems());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / (1024D * 1024D * 1024D):0.0} GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024D * 1024D):0.0} MB";
        }

        return $"{bytes / 1024D:0.0} KB";
    }
}

public sealed record HistoryGridRow(IReadOnlyList<HistoryGridCard> Items);

public sealed class HistoryGridCard : INotifyPropertyChanged
{
    private bool _isSelected;

    public HistoryGridCard(HistoryItem item, bool isSelected)
    {
        Item = item;
        _isSelected = isSelected;
    }

    public HistoryItem Item { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
