using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingPoint = System.Drawing.Point;
using WpfPoint = System.Windows.Point;
using ShapeRectangle = System.Windows.Shapes.Rectangle;
using MediaColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;

namespace QingSnap.App.Views;

public partial class StickyImageWindow : Window
{
    private const double MinimumScale = 0.01;
    private const double MaximumScale = 4;
    private const double ZoomStep = 1.12;
    private const double LongImageAspectThreshold = 2.15;

    private readonly BitmapSource _image;
    private readonly ClipboardService _clipboardService;
    private readonly OcrService _ocrService;
    private readonly DispatcherTimer _feedbackTimer;
    private readonly DispatcherTimer _zoomQualityTimer;
    private readonly double _imageWidthDip;
    private readonly double _imageHeightDip;
    private readonly DrawingRectangle? _initialRegion;
    private readonly DrawingPoint? _initialPosition;
    private readonly bool _isLongImage;
    private double _fitScale;
    private double _scale;
    private double _readerBaseScale;
    private double _readerScale;
    private double _readerViewportWidth;
    private double _readerViewportHeight;
    private double _readerOffsetRatio;
    private bool _isLongReaderMode;
    private bool _isSelectingText;
    private int _selectionAnchorIndex = -1;
    private CancellationTokenSource? _ocrCancellation;
    private Task<OcrRecognitionResult>? _ocrPreloadTask;
    private bool _isOcrPreloadObserved;
    private OcrRecognitionResult? _ocrResult;
    private IReadOnlyList<OcrTextWord> _ocrWords = [];
    private readonly HashSet<int> _selectedWordIndices = [];

    public StickyImageWindow(
        BitmapSource image,
        string sourceName,
        ClipboardService clipboardService,
        OcrService ocrService,
        DrawingRectangle? initialRegion = null,
        DrawingPoint? initialPosition = null,
        Task<OcrRecognitionResult>? prefetchedOcr = null)
    {
        _image = image;
        _clipboardService = clipboardService;
        _ocrService = ocrService;
        _imageWidthDip = image.PixelWidth * 96D / Math.Max(1, image.DpiX);
        _imageHeightDip = image.PixelHeight * 96D / Math.Max(1, image.DpiY);
        _initialRegion = initialRegion;
        _initialPosition = initialPosition;
        _ocrPreloadTask = prefetchedOcr;
        _isLongImage = _imageHeightDip / Math.Max(1, _imageWidthDip) >= LongImageAspectThreshold;

        InitializeComponent();
        Title = $"QingSnap 贴图 — {Path.GetFileName(sourceName)}";
        PinnedImage.Source = image;
        LongPinnedImage.Source = image;

        if (_isLongImage)
        {
            TopMenuItem.Visibility = Visibility.Visible;
            OverviewMenuItem.Visibility = Visibility.Visible;
            FitMenuItem.Header = "适合阅读宽度";
            ActualSizeMenuItem.Header = "原始宽度  1:1";
        }

        _feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            FeedbackBadge.Visibility = Visibility.Collapsed;
        };
        _zoomQualityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _zoomQualityTimer.Tick += (_, _) =>
        {
            _zoomQualityTimer.Stop();
            RenderOptions.SetBitmapScalingMode(PinnedImage, BitmapScalingMode.HighQuality);
            RenderOptions.SetBitmapScalingMode(LongPinnedImage, BitmapScalingMode.HighQuality);
        };

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _feedbackTimer.Stop();
            _zoomQualityTimer.Stop();
            _ocrCancellation?.Cancel();
            _ocrCancellation?.Dispose();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ScheduleOcrPreload();
        var workArea = SystemParameters.WorkArea;
        _fitScale = Math.Min(
            1,
            Math.Min(
                workArea.Width * 0.72 / Math.Max(1, _imageWidthDip),
                workArea.Height * 0.72 / Math.Max(1, _imageHeightDip)));
        _fitScale = Math.Clamp(_fitScale, MinimumScale, MaximumScale);

        if (_isLongImage)
        {
            InitializeLongReader(workArea);
            Activate();
            Focus();
            return;
        }

        _fitScale = _initialRegion is { Width: > 0, Height: > 0 } region
            ? Math.Min(
                region.Width / (double)Math.Max(1, _image.PixelWidth),
                region.Height / (double)Math.Max(1, _image.PixelHeight))
            : _fitScale;
        _fitScale = Math.Clamp(_fitScale, MinimumScale, MaximumScale);
        SetScale(_fitScale, _initialRegion is null);

        if (_initialRegion is { Width: > 0, Height: > 0 } initialRegion)
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(
                handle,
                NativeMethods.HwndTopmost,
                initialRegion.Left - 1,
                initialRegion.Top - 1,
                initialRegion.Width + 2,
                initialRegion.Height + 2,
                NativeMethods.SwpShowWindow);
        }
        else if (_initialPosition is { } initialPosition)
        {
            PlaceAtPoint(initialPosition, workArea);
        }

        UpdateTextOverlayVisibility();
        Activate();
        Focus();
    }

    private void OnImageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            if (_isLongImage)
            {
                if (_isLongReaderMode)
                {
                    EnterOverviewMode();
                }
                else
                {
                    EnterReaderMode(false);
                }

                e.Handled = true;
                return;
            }

            var useActualSize = Math.Abs(_scale - _fitScale) < 0.01;
            SetScaleSmooth(useActualSize ? 1 : _fitScale, false);
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
            e.Handled = true;
        }
    }

    private void ScheduleOcrPreload()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_ocrResult is not null)
            {
                return;
            }

            if (_ocrPreloadTask is null)
            {
                _ocrCancellation ??= new CancellationTokenSource();
                _ocrPreloadTask = _ocrService.RecognizeAsync(
                    _image,
                    _ocrCancellation.Token,
                    progress: null,
                    includeWordBoxes: true);
            }

            if (!_isOcrPreloadObserved)
            {
                _isOcrPreloadObserved = true;
                _ = ObserveOcrPreloadAsync(_ocrPreloadTask);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private async Task ObserveOcrPreloadAsync(Task<OcrRecognitionResult> preloadTask)
    {
        try
        {
            var result = await preloadTask;
            if (_ocrCancellation?.IsCancellationRequested != true)
            {
                ApplyOcrResult(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (ReferenceEquals(_ocrPreloadTask, preloadTask))
            {
                _ocrPreloadTask = null;
                _isOcrPreloadObserved = false;
            }
        }
    }

    private void ApplyOcrResult(OcrRecognitionResult result)
    {
        _ocrResult = result;
        _ocrWords = result.Lines
            .SelectMany(line => line.Words)
            .OrderBy(word => word.Index)
            .ToArray();
        Dispatcher.BeginInvoke(RenderTextOverlay, DispatcherPriority.Render);
    }

    private void UpdateTextOverlayVisibility()
    {
        TextSelectionOverlay.Visibility = Visibility.Collapsed;
        LongTextSelectionOverlay.Visibility = Visibility.Collapsed;
        ActiveTextOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(RenderTextOverlay, DispatcherPriority.Render);
    }

    private Canvas ActiveTextOverlay => _isLongReaderMode
        ? LongTextSelectionOverlay
        : TextSelectionOverlay;

    private void RenderTextOverlay()
    {
        var overlay = ActiveTextOverlay;
        overlay.Children.Clear();
        if (_ocrResult is null || _selectedWordIndices.Count == 0)
        {
            return;
        }

        if (overlay.ActualWidth <= 0 || overlay.ActualHeight <= 0)
        {
            return;
        }

        var scaleX = overlay.ActualWidth / Math.Max(1, _ocrResult.SourceWidth);
        var scaleY = overlay.ActualHeight / Math.Max(1, _ocrResult.SourceHeight);
        var selectedLines = _ocrWords
            .Where(word => _selectedWordIndices.Contains(word.Index))
            .GroupBy(word => word.LineIndex)
            .OrderBy(group => group.Key);
        foreach (var line in selectedLines)
        {
            var left = line.Min(word => word.Bounds.X) * scaleX;
            var top = line.Min(word => word.Bounds.Y) * scaleY;
            var right = line.Max(word => word.Bounds.Right) * scaleX;
            var bottom = line.Max(word => word.Bounds.Bottom) * scaleY;
            var rectangle = new ShapeRectangle
            {
                Width = Math.Max(2, right - left),
                Height = Math.Max(2, bottom - top),
                RadiusX = 1,
                RadiusY = 1,
                Fill = new SolidColorBrush(MediaColor.FromArgb(104, 72, 171, 255)),
                StrokeThickness = 0,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rectangle, left);
            Canvas.SetTop(rectangle, top);
            overlay.Children.Add(rectangle);
        }
    }

    private void OnTextOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ActiveTextOverlay))
        {
            RenderTextOverlay();
        }
    }

    private void OnTextOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Canvas overlay)
        {
            return;
        }

        var word = HitTestWord(overlay, e.GetPosition(overlay));
        if (word is null)
        {
            _selectedWordIndices.Clear();
            _selectionAnchorIndex = -1;
            RenderTextOverlay();
            overlay.Cursor = WpfCursors.SizeAll;
            OnImageMouseLeftButtonDown(sender, e);
            return;
        }

        if (e.ClickCount >= 3)
        {
            SelectLine(word.LineIndex);
            _selectionAnchorIndex = word.Index;
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            _selectedWordIndices.Clear();
            _selectedWordIndices.Add(word.Index);
            _selectionAnchorIndex = word.Index;
            RenderTextOverlay();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_selectedWordIndices.Add(word.Index))
            {
                _selectedWordIndices.Remove(word.Index);
            }
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _selectionAnchorIndex >= 0)
        {
            SelectWordRange(_selectionAnchorIndex, word.Index);
        }
        else
        {
            _selectedWordIndices.Clear();
            _selectedWordIndices.Add(word.Index);
        }

        _selectionAnchorIndex = word.Index;
        _isSelectingText = true;
        overlay.CaptureMouse();
        RenderTextOverlay();
        e.Handled = true;
    }

    private void OnTextOverlayMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Canvas overlay)
        {
            return;
        }

        var word = HitTestWord(overlay, e.GetPosition(overlay));
        overlay.Cursor = word is null ? WpfCursors.SizeAll : WpfCursors.IBeam;
        if (_isSelectingText && e.LeftButton == MouseButtonState.Pressed &&
            word is not null && _selectionAnchorIndex >= 0)
        {
            SelectWordRange(_selectionAnchorIndex, word.Index);
        }
    }

    private void OnTextOverlayMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Canvas overlay && _isSelectingText)
        {
            _isSelectingText = false;
            overlay.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    private void OnTextOverlayMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Canvas overlay && !_isSelectingText)
        {
            overlay.Cursor = WpfCursors.SizeAll;
        }
    }

    private OcrTextWord? HitTestWord(Canvas overlay, WpfPoint point)
    {
        if (_ocrResult is null || overlay.ActualWidth <= 0 || overlay.ActualHeight <= 0)
        {
            return null;
        }

        var sourcePoint = new WpfPoint(
            point.X * _ocrResult.SourceWidth / overlay.ActualWidth,
            point.Y * _ocrResult.SourceHeight / overlay.ActualHeight);
        var paddingX = 2 * _ocrResult.SourceWidth / overlay.ActualWidth;
        var paddingY = 2 * _ocrResult.SourceHeight / overlay.ActualHeight;
        return _ocrWords.LastOrDefault(word =>
            sourcePoint.X >= word.Bounds.X - paddingX &&
            sourcePoint.X <= word.Bounds.Right + paddingX &&
            sourcePoint.Y >= word.Bounds.Y - paddingY &&
            sourcePoint.Y <= word.Bounds.Bottom + paddingY);
    }

    private void SelectWordRange(int firstIndex, int secondIndex)
    {
        var minimum = Math.Min(firstIndex, secondIndex);
        var maximum = Math.Max(firstIndex, secondIndex);
        _selectedWordIndices.Clear();
        foreach (var word in _ocrWords.Where(word => word.Index >= minimum && word.Index <= maximum))
        {
            _selectedWordIndices.Add(word.Index);
        }

        RenderTextOverlay();
    }

    private void SelectLine(int lineIndex)
    {
        _selectedWordIndices.Clear();
        foreach (var word in _ocrWords.Where(word => word.LineIndex == lineIndex))
        {
            _selectedWordIndices.Add(word.Index);
        }

        RenderTextOverlay();
    }

    private void CopySelectedText()
    {
        var selectedText = BuildSelectedText();
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            ShowFeedback("请先选择文字");
            return;
        }

        try
        {
            _clipboardService.CopyText(selectedText);
            ShowFeedback($"已复制 {selectedText.Length:N0} 个字符");
        }
        catch (Exception exception)
        {
            ShowFeedback(exception is InvalidOperationException
                ? "剪贴板仍被占用 · 选区已保留 · 再按 Ctrl+C"
                : exception.Message);
        }
    }

    private string BuildSelectedText()
    {
        if (_ocrResult is null || _selectedWordIndices.Count == 0)
        {
            return string.Empty;
        }

        var selectedByLine = _ocrWords
            .Where(word => _selectedWordIndices.Contains(word.Index))
            .GroupBy(word => word.LineIndex)
            .OrderBy(group => group.Key);
        var lines = selectedByLine.Select(group =>
        {
            var words = group.OrderBy(word => word.Index).ToArray();
            var text = new System.Text.StringBuilder();
            for (var index = 0; index < words.Length; index++)
            {
                if (index > 0 && ShouldInsertSpace(words[index - 1], words[index]))
                {
                    text.Append(' ');
                }

                text.Append(words[index].Text);
            }

            return text.ToString();
        });
        return string.Join(Environment.NewLine, lines);
    }

    private static bool ShouldInsertSpace(OcrTextWord previous, OcrTextWord current)
    {
        if (string.IsNullOrEmpty(previous.Text) || string.IsNullOrEmpty(current.Text))
        {
            return false;
        }

        var previousCharacter = previous.Text[^1];
        var currentCharacter = current.Text[0];
        if (previousCharacter <= 127 && currentCharacter <= 127 &&
            char.IsLetterOrDigit(previousCharacter) && char.IsLetterOrDigit(currentCharacter))
        {
            return true;
        }

        var gap = current.Bounds.X - previous.Bounds.Right;
        return gap > Math.Max(2, Math.Min(previous.Bounds.Height, current.Bounds.Height) * 0.32);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        RenderOptions.SetBitmapScalingMode(PinnedImage, BitmapScalingMode.LowQuality);
        RenderOptions.SetBitmapScalingMode(LongPinnedImage, BitmapScalingMode.LowQuality);
        _zoomQualityTimer.Stop();
        _zoomQualityTimer.Start();

        if (_isLongReaderMode)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                SetReaderScale(_readerScale * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), true);
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                LongImageScroller.ScrollToHorizontalOffset(
                    LongImageScroller.HorizontalOffset - e.Delta * 0.82);
            }
            else
            {
                LongImageScroller.ScrollToVerticalOffset(
                    LongImageScroller.VerticalOffset - e.Delta * 0.82);
            }

            e.Handled = true;
            return;
        }

        SetScaleSmooth(_scale * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), true);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (_selectedWordIndices.Count > 0)
            {
                CopySelectedText();
            }
            else
            {
                CopyImage();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _ocrWords.Count > 0)
        {
            _selectedWordIndices.Clear();
            foreach (var word in _ocrWords)
            {
                _selectedWordIndices.Add(word.Index);
            }

            RenderTextOverlay();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (!_isLongReaderMode)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Home:
                LongImageScroller.ScrollToTop();
                e.Handled = true;
                break;
            case Key.End:
                LongImageScroller.ScrollToEnd();
                e.Handled = true;
                break;
            case Key.PageUp:
                LongImageScroller.PageUp();
                e.Handled = true;
                break;
            case Key.PageDown:
            case Key.Space:
                LongImageScroller.PageDown();
                e.Handled = true;
                break;
            case Key.Left:
                LongImageScroller.LineLeft();
                e.Handled = true;
                break;
            case Key.Right:
                LongImageScroller.LineRight();
                e.Handled = true;
                break;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyImage();

    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        if (_isLongImage)
        {
            if (!_isLongReaderMode)
            {
                EnterReaderMode(false);
            }

            SetReaderScale(_readerBaseScale, false);
            return;
        }

        SetScaleSmooth(_fitScale, false);
    }

    private void OnActualSizeClick(object sender, RoutedEventArgs e)
    {
        if (_isLongImage)
        {
            if (!_isLongReaderMode)
            {
                EnterReaderMode(false);
            }

            SetReaderScale(1, false);
            return;
        }

        SetScaleSmooth(1, false);
    }

    private void OnTopClick(object sender, RoutedEventArgs e)
    {
        if (!_isLongReaderMode)
        {
            EnterReaderMode(false);
        }

        LongImageScroller.ScrollToTop();
    }

    private void OnOverviewClick(object sender, RoutedEventArgs e) => EnterOverviewMode();

    private void OnReaderModeClick(object sender, RoutedEventArgs e) => EnterReaderMode(false);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void CopyImage()
    {
        try
        {
            _clipboardService.CopyImage(_image);
            ShowFeedback("已复制到剪贴板");
        }
        catch (Exception exception)
        {
            ShowFeedback(exception.Message);
        }
    }

    private void InitializeLongReader(Rect workArea)
    {
        var preferredWidth = _initialRegion is { Width: > 0 } region
            ? region.Width
            : Math.Min(_imageWidthDip, workArea.Width * 0.46);
        _readerViewportWidth = Math.Clamp(
            preferredWidth,
            Math.Min(280, workArea.Width * 0.45),
            workArea.Width * 0.72);

        var preferredHeight = _initialRegion is { Height: > 0 } initialRegion
            ? initialRegion.Height
            : workArea.Height * 0.76;
        _readerViewportHeight = Math.Clamp(
            preferredHeight,
            Math.Min(360, workArea.Height * 0.58),
            workArea.Height * 0.78);
        _readerBaseScale = _readerViewportWidth / Math.Max(1, _imageWidthDip);
        _readerScale = _readerBaseScale;
        EnterReaderMode(true);
    }

    private void EnterReaderMode(bool useInitialPosition)
    {
        if (!_isLongImage)
        {
            return;
        }

        var oldCenterX = Left + ActualWidth / 2;
        var oldCenterY = Top + ActualHeight / 2;
        _isLongReaderMode = true;
        PinnedImage.Visibility = Visibility.Collapsed;
        LongImageScroller.Visibility = Visibility.Visible;
        ScrollRail.Visibility = Visibility.Visible;
        TopMenuItem.Visibility = Visibility.Visible;
        OverviewMenuItem.Visibility = Visibility.Visible;
        ReaderModeMenuItem.Visibility = Visibility.Collapsed;

        Width = _readerViewportWidth + 2;
        Height = _readerViewportHeight + 2;
        ApplyReaderImageSize();
        UpdateTextOverlayVisibility();

        var workArea = SystemParameters.WorkArea;
        if (useInitialPosition && _initialRegion is { Width: > 0, Height: > 0 } region)
        {
            Left = Math.Clamp(region.Left - 1D, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            Top = Math.Clamp(region.Top - 1D, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        }
        else if (useInitialPosition && _initialPosition is { } initialPosition)
        {
            PlaceAtPoint(initialPosition, workArea);
        }
        else if (IsLoaded)
        {
            Left = Math.Clamp(oldCenterX - Width / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            Top = Math.Clamp(oldCenterY - Height / 2, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        }

        Dispatcher.BeginInvoke(() =>
        {
            LongImageScroller.ScrollToVerticalOffset(
                _readerOffsetRatio * LongImageScroller.ScrollableHeight);
            UpdateScrollProgress();
        }, DispatcherPriority.Loaded);

        if (!useInitialPosition)
        {
            ShowFeedback("长图阅读窗 · 滚轮浏览");
        }
    }

    private void EnterOverviewMode()
    {
        if (!_isLongImage || !_isLongReaderMode)
        {
            return;
        }

        _readerOffsetRatio = LongImageScroller.ScrollableHeight <= 0
            ? 0
            : LongImageScroller.VerticalOffset / LongImageScroller.ScrollableHeight;
        _isLongReaderMode = false;
        LongImageScroller.Visibility = Visibility.Collapsed;
        ScrollRail.Visibility = Visibility.Collapsed;
        PinnedImage.Visibility = Visibility.Visible;
        TopMenuItem.Visibility = Visibility.Collapsed;
        OverviewMenuItem.Visibility = Visibility.Collapsed;
        ReaderModeMenuItem.Visibility = Visibility.Visible;
        SetScaleSmooth(_fitScale, false);
        UpdateTextOverlayVisibility();
        ShowFeedback("完整概览 · 双击返回阅读窗");
    }

    private void SetReaderScale(double scale, bool anchorAtCursor)
    {
        if (!_isLongReaderMode)
        {
            return;
        }

        var minimumReaderScale = _readerBaseScale;
        var nextScale = Math.Clamp(scale, minimumReaderScale, MaximumScale);
        var oldExtentHeight = Math.Max(1, LongPinnedImage.ActualHeight);
        var anchorInViewport = anchorAtCursor
            ? Math.Clamp(Mouse.GetPosition(LongImageScroller).Y, 0, LongImageScroller.ViewportHeight)
            : LongImageScroller.ViewportHeight / 2;
        var anchorRatio = (LongImageScroller.VerticalOffset + anchorInViewport) / oldExtentHeight;

        _readerScale = nextScale;
        ApplyReaderImageSize();
        Dispatcher.BeginInvoke(() =>
        {
            var nextOffset = anchorRatio * LongPinnedImage.ActualHeight - anchorInViewport;
            LongImageScroller.ScrollToVerticalOffset(nextOffset);
            UpdateScrollProgress();
        }, DispatcherPriority.Render);
        ShowFeedback($"阅读缩放 {Math.Round(_readerScale * 100)}%");
    }

    private void ApplyReaderImageSize()
    {
        var width = Math.Max(24, _imageWidthDip * _readerScale);
        var height = Math.Max(24, _imageHeightDip * _readerScale);
        LongImageSurface.Width = width;
        LongImageSurface.Height = height;
        LongPinnedImage.Width = width;
        LongPinnedImage.Height = height;
        LongTextSelectionOverlay.Width = width;
        LongTextSelectionOverlay.Height = height;
    }

    private void PlaceAtPoint(DrawingPoint point, Rect workArea)
    {
        Left = Math.Clamp(
            point.X - Width / 2,
            workArea.Left,
            Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(
            point.Y - Height / 2,
            workArea.Top,
            Math.Max(workArea.Top, workArea.Bottom - Height));
    }

    private void OnLongImageScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (_isLongReaderMode && LongImageScroller.ScrollableHeight > 0)
        {
            _readerOffsetRatio = LongImageScroller.VerticalOffset / LongImageScroller.ScrollableHeight;
        }

        UpdateScrollProgress();
    }

    private void OnScrollRailSizeChanged(object sender, SizeChangedEventArgs e) => UpdateScrollProgress();

    private void UpdateScrollProgress()
    {
        if (!_isLongReaderMode || ScrollRail.ActualHeight <= 0)
        {
            return;
        }

        var trackHeight = ScrollRail.ActualHeight;
        var extentHeight = Math.Max(1, LongImageScroller.ExtentHeight);
        var viewportRatio = Math.Clamp(LongImageScroller.ViewportHeight / extentHeight, 0, 1);
        var thumbHeight = Math.Clamp(trackHeight * viewportRatio, 32, trackHeight);
        var travel = Math.Max(0, trackHeight - thumbHeight);
        var offsetRatio = LongImageScroller.ScrollableHeight <= 0
            ? 0
            : LongImageScroller.VerticalOffset / LongImageScroller.ScrollableHeight;

        ScrollThumb.Height = thumbHeight;
        if (ScrollThumb.RenderTransform is TranslateTransform transform)
        {
            transform.Y = travel * offsetRatio;
        }
    }

    private void SetScale(double scale, bool preserveCenter)
    {
        var centerX = Left + ActualWidth / 2;
        var centerY = Top + ActualHeight / 2;
        _scale = Math.Clamp(scale, MinimumScale, MaximumScale);

        Width = Math.Max(24, _imageWidthDip * _scale + 2);
        Height = Math.Max(24, _imageHeightDip * _scale + 2);
        if (preserveCenter && IsLoaded)
        {
            Left = centerX - Width / 2;
            Top = centerY - Height / 2;
        }
    }

    private void SetScaleSmooth(double scale, bool anchorAtCursor)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var current))
        {
            SetScale(scale, true);
            return;
        }

        var nextScale = Math.Clamp(scale, MinimumScale, MaximumScale);
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(24, (int)Math.Round((_imageWidthDip * nextScale + 2) * dpi.DpiScaleX));
        var height = Math.Max(24, (int)Math.Round((_imageHeightDip * nextScale + 2) * dpi.DpiScaleY));

        double anchorX;
        double anchorY;
        double relativeX;
        double relativeY;
        if (anchorAtCursor && NativeMethods.GetCursorPos(out var cursor))
        {
            anchorX = cursor.X;
            anchorY = cursor.Y;
            relativeX = Math.Clamp((cursor.X - current.Left) / (double)Math.Max(1, current.Width), 0, 1);
            relativeY = Math.Clamp((cursor.Y - current.Top) / (double)Math.Max(1, current.Height), 0, 1);
        }
        else
        {
            anchorX = current.Left + current.Width / 2D;
            anchorY = current.Top + current.Height / 2D;
            relativeX = 0.5;
            relativeY = 0.5;
        }

        _scale = nextScale;
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            (int)Math.Round(anchorX - width * relativeX),
            (int)Math.Round(anchorY - height * relativeY),
            width,
            height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void ShowFeedback(string message)
    {
        FeedbackText.Text = message;
        FeedbackBadge.Visibility = Visibility.Visible;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

}
