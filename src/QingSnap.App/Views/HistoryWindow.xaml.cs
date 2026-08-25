using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QingSnap.App.Models;
using QingSnap.App.Services;
using WpfButton = System.Windows.Controls.Button;

namespace QingSnap.App.Views;

public partial class HistoryWindow : Window
{
    private const int MaximumLoadedItems = 500;

    private readonly CaptureHistoryService _historyService;
    private readonly HistoryOcrIndexingService _historyOcrIndexer;
    private readonly ClipboardService _clipboardService;
    private readonly Action<string> _pinImage;
    private readonly Action<string> _recognizeImage;
    private IReadOnlyList<HistoryItem> _loadedItems = [];
    private HistorySnapshot? _snapshot;
    private CancellationTokenSource? _refreshCancellation;
    private bool _isReady;
    private bool _isLoadingHistory;
    private HistoryOcrIndexProgress? _indexProgress;
    private readonly Dictionary<string, string> _pendingIndexUpdates =
        new(StringComparer.OrdinalIgnoreCase);

    public HistoryWindow(
        CaptureHistoryService historyService,
        HistoryOcrIndexingService historyOcrIndexer,
        ClipboardService clipboardService,
        Action<string> pinImage,
        Action<string> recognizeImage)
    {
        _historyService = historyService;
        _historyOcrIndexer = historyOcrIndexer;
        _clipboardService = clipboardService;
        _pinImage = pinImage;
        _recognizeImage = recognizeImage;
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
        _historyOcrIndexer.ProgressChanged += OnIndexProgressChanged;
        Closed += (_, _) =>
        {
            _refreshCancellation?.Cancel();
            _historyOcrIndexer.ProgressChanged -= OnIndexProgressChanged;
        };
    }

    public ObservableCollection<HistoryItem> VisibleItems { get; } = [];

    public void RefreshHistory() => _ = LoadHistoryAsync();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isReady = true;
        RefreshHistory();
        _historyOcrIndexer.ScheduleBackfill();
    }

    private async Task LoadHistoryAsync()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var cancellationToken = _refreshCancellation.Token;

        LoadingPanel.Visibility = Visibility.Visible;
        StatusText.Text = "正在读取截图记录…";
        _isLoadingHistory = true;

        try
        {
            var snapshot = await Task.Run(
                () => _historyService.LoadSnapshot(MaximumLoadedItems, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _snapshot = snapshot;
            _loadedItems = snapshot.Items;
            _isLoadingHistory = false;
            if (_pendingIndexUpdates.Count > 0)
            {
                _loadedItems = _loadedItems
                    .Select(item => _pendingIndexUpdates.TryGetValue(item.FilePath, out var text)
                        ? item with { SearchText = text }
                        : item)
                    .ToArray();
                _pendingIndexUpdates.Clear();
            }
            HeaderStatsText.Text = snapshot.TotalCount > MaximumLoadedItems
                ? $"共 {snapshot.TotalCount:N0} 张 · {FormatBytes(snapshot.TotalBytes)} · 已载入最近 {MaximumLoadedItems:N0} 张"
                : $"共 {snapshot.TotalCount:N0} 张 · {FormatBytes(snapshot.TotalBytes)}";
            ApplyFilters();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"读取失败：{exception.Message}";
            EmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            _isLoadingHistory = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ApplyFilters()
    {
        if (!_isReady)
        {
            return;
        }

        var query = SearchBox.Text.Trim();
        var today = DateTime.Today;
        var dateFilterIndex = DateFilter.SelectedIndex;
        var filteredItems = _loadedItems.Where(item =>
        {
            var matchesQuery = string.IsNullOrEmpty(query) ||
                               item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               item.DimensionsText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
            var matchesDate = dateFilterIndex switch
            {
                1 => item.CreatedAt.Date == today,
                2 => item.CreatedAt >= today.AddDays(-6),
                3 => item.PixelHeight / (double)Math.Max(1, item.PixelWidth) >= 2.15,
                4 => item.IsFavorite,
                _ => true
            };
            return matchesQuery && matchesDate;
        });

        VisibleItems.Clear();
        foreach (var item in filteredItems)
        {
            VisibleItems.Add(item);
        }

        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyState.Visibility = VisibleItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusText.Text = _snapshot is null
            ? "就绪"
            : $"当前显示 {VisibleItems.Count:N0} / {_snapshot.TotalCount:N0} 张 · 双击预览图可打开原图";
        ApplyIndexProgressStatus();
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
            if (progress.CompletedFilePath is not null)
            {
                if (_isLoadingHistory)
                {
                    _pendingIndexUpdates[progress.CompletedFilePath] = progress.RecognizedText ?? string.Empty;
                    ApplyIndexProgressStatus();
                    return;
                }

                _loadedItems = _loadedItems
                    .Select(item => string.Equals(
                        item.FilePath,
                        progress.CompletedFilePath,
                        StringComparison.OrdinalIgnoreCase)
                        ? item with { SearchText = progress.RecognizedText ?? string.Empty }
                        : item)
                    .ToArray();
                ApplyFilters();
            }
            else
            {
                ApplyIndexProgressStatus();
            }
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

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilters();

    private void OnDateFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilters();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshHistory();

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

    private void OnDeleteItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: HistoryItem item })
        {
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"把这张截图移到回收站？\n\n{item.FileName}",
            "删除截图",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _historyService.DeleteToRecycleBin(item.FilePath);
            StatusText.Text = "截图已移到回收站。";
            RefreshHistory();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"删除失败：{exception.Message}";
        }
    }

    private void OnHistoryItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is Border { DataContext: HistoryItem item })
        {
            OpenItem(item);
            e.Handled = true;
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

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
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
