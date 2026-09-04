using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace QingSnap.App.Views;

public partial class StickyImageWindow : Window
{
    private const double MinimumScale = 0.01;
    private const double MaximumScale = 4;
    private const double ZoomStep = 1.1;
    private const double LongImageAspectThreshold = 2.15;

    private readonly BitmapSource _image;
    private readonly ClipboardService _clipboardService;
    private readonly OcrService _ocrService;
    private readonly DispatcherTimer _feedbackTimer;
    private readonly DispatcherTimer _collapsedDockHideTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(180)
    };
    private readonly double _imageWidthDip;
    private readonly double _imageHeightDip;
    private readonly DrawingRectangle? _initialRegion;
    private readonly DrawingPoint? _initialPosition;
    private readonly bool _isLongImage;
    private readonly bool _usesCloseButton;
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
    private bool _isInitiallyCloaked;
    private bool _usesOpacityFallback;
    private bool _isCollapsed;
    private NativeMethods.NativeRectangle? _expandedBounds;
    private bool _isDraggingCollapsedDock;
    private bool _collapsedDockDragMoved;
    private NativeMethods.NativePoint _collapsedDockDragStart;
    private NativeMethods.NativeRectangle _collapsedDockWindowStart;
    private bool _collapsedDockUsesLeftEdge;
    private bool _isCollapsedDockRevealed;
    private DrawingRectangle _collapsedDockWorkArea;
    private NativeMethods.NativeRectangle? _collapsedDockRestingBounds;
    private DispatcherTimer? _dockTransitionTimer;
    private bool _isDockTransitioning;

    public StickyImageWindow(
        BitmapSource image,
        string sourceName,
        ClipboardService clipboardService,
        OcrService ocrService,
        AppSettings settings,
        DrawingRectangle? initialRegion = null,
        DrawingPoint? initialPosition = null,
        Task<OcrRecognitionResult>? prefetchedOcr = null)
    {
        if (image.CanFreeze && !image.IsFrozen)
        {
            image.Freeze();
        }

        _image = image;
        _clipboardService = clipboardService;
        _ocrService = ocrService;
        _usesCloseButton = string.Equals(
            settings.CloseInteraction,
            "Button",
            StringComparison.OrdinalIgnoreCase);
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
        CollapsedThumbnail.Source = image;
        CollapsedThumbnail.Stretch = Stretch.UniformToFill;
        PinCloseHotspot.Visibility = _usesCloseButton ? Visibility.Visible : Visibility.Collapsed;
        CloseMenuItem.InputGestureText = "Esc";

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
        _collapsedDockHideTimer.Tick += (_, _) =>
        {
            _collapsedDockHideTimer.Stop();
            if (_isCollapsed && !_isDraggingCollapsedDock && !CollapsedDock.IsMouseOver)
            {
                SetCollapsedDockRevealed(false);
            }
        };
        RenderOptions.SetBitmapScalingMode(PinnedImage, BitmapScalingMode.Linear);
        RenderOptions.SetBitmapScalingMode(LongPinnedImage, BitmapScalingMode.Linear);

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        ContentRendered += OnFirstContentRendered;
        Closed += (_, _) =>
        {
            _feedbackTimer.Stop();
            _collapsedDockHideTimer.Stop();
            _dockTransitionTimer?.Stop();
            _ocrCancellation?.Cancel();
            _ocrCancellation?.Dispose();
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ImmAssociateContextEx(handle, nint.Zero, 0);
        _isInitiallyCloaked = handle != nint.Zero && NativeMethods.SetWindowCloaked(handle, true);
        if (!_isInitiallyCloaked)
        {
            _usesOpacityFallback = true;
            Opacity = 0;
        }
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
            return;
        }

        _fitScale = _initialRegion is { Width: > 0, Height: > 0 } region
            ? Math.Min(
                region.Width / (double)Math.Max(1, _image.PixelWidth),
                region.Height / (double)Math.Max(1, _image.PixelHeight))
            : _fitScale;
        _fitScale = Math.Clamp(_fitScale, MinimumScale, MaximumScale);
        SetScale(_fitScale, false);

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
                NativeMethods.SwpNoActivate);
        }
        else if (_initialPosition is { } initialPosition)
        {
            PlaceAtPoint(initialPosition, workArea);
        }
        else
        {
            PlaceAtCenter(workArea);
        }

        UpdateTextOverlayVisibility();
    }

    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        UpdateLayout();
        NativeMethods.DwmFlush();
        if (_isInitiallyCloaked)
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowCloaked(handle, false);
            _isInitiallyCloaked = false;
            NativeMethods.DwmFlush();
        }
        else if (_usesOpacityFallback)
        {
            Opacity = 1;
            UpdateLayout();
            NativeMethods.DwmFlush();
        }

        Activate();
        Focus();
        if (_usesCloseButton)
        {
            ShowPinCloseButton(true);
        }

        FirstFramePresented?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? FirstFramePresented;

    private void OnImageMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isDockTransitioning)
        {
            e.Handled = true;
            return;
        }

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
            SetScaleImmediate(useActualSize ? 1 : _fitScale);
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var beforeDrag = default(NativeMethods.NativeRectangle);
            var hadBounds = handle != nint.Zero &&
                            NativeMethods.GetWindowRect(handle, out beforeDrag);
            DragMove();
            if (hadBounds)
            {
                TryCollapseFromEdgeDrop(beforeDrag);
            }

            e.Handled = true;
        }
    }

    private void ScheduleOcrPreload()
    {
        if (!_ocrService.IsOcrAvailable)
        {
            return;
        }

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

    private async void CopySelectedText()
    {
        var selectedText = BuildSelectedText();
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            ShowFeedback("请先选择文字");
            return;
        }

        try
        {
            await _clipboardService.CopyTextAsync(selectedText);
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

        return OcrTextSelectionBuilder.Build(_ocrResult, _selectedWordIndices);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isCollapsed || _isDockTransitioning)
        {
            e.Handled = true;
            return;
        }

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

        var wheelSteps = Math.Clamp(e.Delta / 120D, -4, 4);
        SetScaleImmediate(_scale * Math.Pow(ZoomStep, wheelSteps));
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = ResolveShortcutKey(e);
        if (key == Key.M && Keyboard.Modifiers == ModifierKeys.None)
        {
            ToggleCollapsed();
            e.Handled = true;
            return;
        }

        if (key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
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

        if (key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _ocrWords.Count > 0)
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

        if (key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (!_isLongReaderMode)
        {
            return;
        }

        switch (key)
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

    private static Key ResolveShortcutKey(System.Windows.Input.KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key
    };

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

        SetScaleImmediate(_fitScale);
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

        SetScaleImmediate(1);
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

    private void OnCollapseClick(object sender, RoutedEventArgs e) => ToggleCollapsed();

    private void OnCollapsedDockMouseEnter(object sender, WpfMouseEventArgs e)
    {
        if (_isCollapsed && !_isDraggingCollapsedDock && !_isDockTransitioning)
        {
            _collapsedDockHideTimer.Stop();
            SetCollapsedDockRevealed(true);
        }
    }

    private void OnCollapsedDockMouseLeave(object sender, WpfMouseEventArgs e)
    {
        if (_isCollapsed && !_isDraggingCollapsedDock && !_isDockTransitioning)
        {
            _collapsedDockHideTimer.Stop();
            _collapsedDockHideTimer.Start();
        }
    }

    private void OnCollapsedDockMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_isDockTransitioning || e.ChangedButton != MouseButton.Left || handle == nint.Zero ||
            !NativeMethods.GetCursorPos(out _collapsedDockDragStart) ||
            !NativeMethods.GetWindowRect(handle, out _collapsedDockWindowStart))
        {
            return;
        }

        _isDraggingCollapsedDock = true;
        _collapsedDockDragMoved = false;
        _collapsedDockHideTimer.Stop();
        CollapsedDock.CaptureMouse();
        e.Handled = true;
    }

    private void OnCollapsedDockMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (!_isDraggingCollapsedDock)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndCollapsedDockDrag(false);
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var offsetX = cursor.X - _collapsedDockDragStart.X;
        var offsetY = cursor.Y - _collapsedDockDragStart.Y;
        _collapsedDockDragMoved |= Math.Abs(offsetX) > 3 || Math.Abs(offsetY) > 3;
        if (_collapsedDockDragMoved)
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(
                handle,
                nint.Zero,
                _collapsedDockWindowStart.Left + offsetX,
                _collapsedDockWindowStart.Top + offsetY,
                _collapsedDockWindowStart.Width,
                _collapsedDockWindowStart.Height,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpNoOwnerZOrder);
        }

        e.Handled = true;
    }

    private void OnCollapsedDockMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_isDraggingCollapsedDock)
        {
            return;
        }

        EndCollapsedDockDrag(!_collapsedDockDragMoved);
        e.Handled = true;
    }

    private void EndCollapsedDockDrag(bool restore)
    {
        _isDraggingCollapsedDock = false;
        CollapsedDock.ReleaseMouseCapture();
        if (restore)
        {
            RestoreFromDock();
        }
        else if (_isCollapsed)
        {
            SnapCollapsedDockToNearestEdge();
        }
    }

    private void SnapCollapsedDockToNearestEdge()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var bounds))
        {
            return;
        }

        var workArea = System.Windows.Forms.Screen.FromRectangle(
            new DrawingRectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height)).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var useLeftEdge = bounds.Left + bounds.Width / 2 < workArea.Left + workArea.Width / 2;
        var layout = PinDockLayoutCalculator.Calculate(
            workArea,
            bounds.Width,
            bounds.Height,
            bounds.Top,
            useLeftEdge,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
        _collapsedDockWorkArea = workArea;
        _collapsedDockUsesLeftEdge = useLeftEdge;
        _isCollapsedDockRevealed = false;
        UpdateCollapsedDockBadgePosition();
        var left = layout.RestingBounds.Left;
        var top = layout.RestingBounds.Top;
        NativeMethods.SetWindowPos(
            handle,
            nint.Zero,
            left,
            top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoOwnerZOrder);
        _collapsedDockRestingBounds = new NativeMethods.NativeRectangle
        {
            Left = left,
            Top = top,
            Right = left + bounds.Width,
            Bottom = top + bounds.Height
        };
    }

    private void SetCollapsedDockRevealed(bool reveal)
    {
        if (!_isCollapsed || _isCollapsedDockRevealed == reveal)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var bounds))
        {
            return;
        }

        var workArea = _collapsedDockWorkArea.Width > 0
            ? _collapsedDockWorkArea
            : System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        var layout = PinDockLayoutCalculator.Calculate(
            workArea,
            bounds.Width,
            bounds.Height,
            bounds.Top,
            _collapsedDockUsesLeftEdge,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
        var left = reveal ? layout.RevealedBounds.Left : layout.RestingBounds.Left;
        _isCollapsedDockRevealed = reveal;
        NativeMethods.SetWindowPos(
            handle,
            nint.Zero,
            left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoOwnerZOrder);
    }

    private void UpdateCollapsedDockBadgePosition()
    {
        CollapsedPinBadge.HorizontalAlignment = _collapsedDockUsesLeftEdge
            ? System.Windows.HorizontalAlignment.Right
            : System.Windows.HorizontalAlignment.Left;
        CollapsedPinBadge.Margin = _collapsedDockUsesLeftEdge
            ? new Thickness(0, 2, 2, 0)
            : new Thickness(2, 2, 0, 0);
    }

    private void TryCollapseFromEdgeDrop(NativeMethods.NativeRectangle beforeDrag)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_isCollapsed || handle == nint.Zero ||
            !NativeMethods.GetWindowRect(handle, out var afterDrag) ||
            !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var minimumTravel = Math.Max(18, (int)Math.Round(24 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY)));
        if (Math.Abs(afterDrag.Left - beforeDrag.Left) < minimumTravel &&
            Math.Abs(afterDrag.Top - beforeDrag.Top) < minimumTravel)
        {
            return;
        }

        var workArea = System.Windows.Forms.Screen.FromPoint(
            new DrawingPoint(cursor.X, cursor.Y)).WorkingArea;
        var edgeThreshold = Math.Max(14, (int)Math.Round(20 * dpi.DpiScaleX));
        var useLeftEdge = cursor.X <= workArea.Left + edgeThreshold;
        var useRightEdge = cursor.X >= workArea.Right - 1 - edgeThreshold;
        if (!useLeftEdge && !useRightEdge)
        {
            return;
        }

        CollapseToDockAtEdge(beforeDrag, afterDrag, cursor, workArea, useLeftEdge);
    }

    private void CollapseToDockAtEdge(
        NativeMethods.NativeRectangle restoreBounds,
        NativeMethods.NativeRectangle animationFrom,
        NativeMethods.NativePoint cursor,
        DrawingRectangle workArea,
        bool useLeftEdge)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(46, (int)Math.Round(58 * dpi.DpiScaleX));
        var height = Math.Max(36, (int)Math.Round(44 * dpi.DpiScaleY));
        var layout = PinDockLayoutCalculator.Calculate(
            workArea,
            width,
            height,
            cursor.Y - height / 2,
            useLeftEdge,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
        var top = layout.RestingBounds.Top;
        var restingLeft = layout.RestingBounds.Left;
        var revealedLeft = layout.RevealedBounds.Left;

        _expandedBounds = restoreBounds;
        _collapsedDockWorkArea = workArea;
        _collapsedDockUsesLeftEdge = useLeftEdge;
        _isCollapsedDockRevealed = true;
        _collapsedDockRestingBounds = new NativeMethods.NativeRectangle
        {
            Left = restingLeft,
            Top = top,
            Right = restingLeft + width,
            Bottom = top + height
        };
        UpdateCollapsedDockBadgePosition();
        PrepareCollapsedVisualState();
        UpdateLayout();

        var revealedBounds = new NativeMethods.NativeRectangle
        {
            Left = revealedLeft,
            Top = top,
            Right = revealedLeft + width,
            Bottom = top + height
        };
        AnimateWindowBounds(handle, animationFrom, revealedBounds, () =>
        {
            if (_isCollapsed && !CollapsedDock.IsMouseOver)
            {
                _collapsedDockHideTimer.Stop();
                _collapsedDockHideTimer.Start();
            }
        });
    }

    private void PrepareCollapsedVisualState()
    {
        _isCollapsed = true;
        _collapsedDockHideTimer.Stop();
        _feedbackTimer.Stop();
        FeedbackBadge.Visibility = Visibility.Collapsed;
        PinCloseButton.BeginAnimation(OpacityProperty, null);
        PinCloseButton.Opacity = 0;
        ExpandedPinContent.Visibility = Visibility.Collapsed;
        CollapsedDock.Visibility = Visibility.Visible;
        CollapseMenuItem.Header = "恢复贴图";
        Frame.Background = System.Windows.Media.Brushes.Transparent;
        Frame.BorderThickness = new Thickness(0);
        Frame.CornerRadius = new CornerRadius(0);
    }

    private void AnimateWindowBounds(
        nint handle,
        NativeMethods.NativeRectangle from,
        NativeMethods.NativeRectangle to,
        Action? completed = null)
    {
        _dockTransitionTimer?.Stop();
        _isDockTransitioning = true;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        const double durationMilliseconds = 190;
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _dockTransitionTimer = timer;
        timer.Tick += (_, _) =>
        {
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var progress = Math.Clamp(elapsed / durationMilliseconds, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            var left = (int)Math.Round(from.Left + (to.Left - from.Left) * eased);
            var top = (int)Math.Round(from.Top + (to.Top - from.Top) * eased);
            var width = Math.Max(1, (int)Math.Round(from.Width + (to.Width - from.Width) * eased));
            var height = Math.Max(1, (int)Math.Round(from.Height + (to.Height - from.Height) * eased));
            NativeMethods.SetWindowPos(
                handle,
                nint.Zero,
                left,
                top,
                width,
                height,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpNoOwnerZOrder);

            if (progress < 1)
            {
                return;
            }

            timer.Stop();
            if (ReferenceEquals(_dockTransitionTimer, timer))
            {
                _dockTransitionTimer = null;
            }

            _isDockTransitioning = false;
            NativeMethods.DwmFlush();
            completed?.Invoke();
        };
        timer.Start();
    }

    private void ToggleCollapsed()
    {
        if (_isDockTransitioning)
        {
            return;
        }

        if (_isCollapsed)
        {
            RestoreFromDock();
        }
        else
        {
            CollapseToDock();
        }
    }

    private void CollapseToDock()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_isCollapsed || handle == nint.Zero ||
            !NativeMethods.GetWindowRect(handle, out var currentBounds))
        {
            return;
        }

        _expandedBounds = currentBounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        DrawingRectangle workArea;
        int width;
        int height;
        int left;
        int top;
        bool useLeftEdge;
        if (_collapsedDockRestingBounds is { } restingBounds &&
            restingBounds.Width > 0 && restingBounds.Height > 0 &&
            _collapsedDockWorkArea.Width > 0)
        {
            workArea = _collapsedDockWorkArea;
            width = restingBounds.Width;
            height = restingBounds.Height;
            useLeftEdge = _collapsedDockUsesLeftEdge;
            left = restingBounds.Left;
            var layout = PinDockLayoutCalculator.Calculate(
                workArea,
                width,
                height,
                restingBounds.Top,
                useLeftEdge,
                dpi.DpiScaleX,
                dpi.DpiScaleY);
            left = layout.RestingBounds.Left;
            top = layout.RestingBounds.Top;
        }
        else
        {
            workArea = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            width = Math.Max(46, (int)Math.Round(58 * dpi.DpiScaleX));
            height = Math.Max(36, (int)Math.Round(44 * dpi.DpiScaleY));
            useLeftEdge = currentBounds.Left + currentBounds.Width / 2 <
                          workArea.Left + workArea.Width / 2;
            var layout = PinDockLayoutCalculator.Calculate(
                workArea,
                width,
                height,
                currentBounds.Top,
                useLeftEdge,
                dpi.DpiScaleX,
                dpi.DpiScaleY);
            left = layout.RestingBounds.Left;
            top = layout.RestingBounds.Top;
        }

        _collapsedDockWorkArea = workArea;
        _collapsedDockUsesLeftEdge = useLeftEdge;
        _isCollapsedDockRevealed = false;
        UpdateCollapsedDockBadgePosition();

        var cloaked = NativeMethods.SetWindowCloaked(handle, true);
        try
        {
            _isCollapsed = true;
            _collapsedDockHideTimer.Stop();
            _feedbackTimer.Stop();
            FeedbackBadge.Visibility = Visibility.Collapsed;
            PinCloseButton.BeginAnimation(OpacityProperty, null);
            PinCloseButton.Opacity = 0;
            ExpandedPinContent.Visibility = Visibility.Collapsed;
            CollapsedDock.Visibility = Visibility.Visible;
            CollapseMenuItem.Header = "恢复贴图";
            Frame.Background = System.Windows.Media.Brushes.Transparent;
            Frame.BorderThickness = new Thickness(0);
            Frame.CornerRadius = new CornerRadius(0);
            NativeMethods.SetWindowPos(
                handle,
                nint.Zero,
                left,
                top,
                width,
                height,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpNoOwnerZOrder);
            _collapsedDockRestingBounds = new NativeMethods.NativeRectangle
            {
                Left = left,
                Top = top,
                Right = left + width,
                Bottom = top + height
            };
            UpdateLayout();
            NativeMethods.DwmFlush();
        }
        finally
        {
            if (cloaked)
            {
                NativeMethods.SetWindowCloaked(handle, false);
                NativeMethods.DwmFlush();
            }
        }
    }

    private void RestoreFromDock()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_isDockTransitioning || !_isCollapsed || handle == nint.Zero ||
            _expandedBounds is not { } expandedBounds ||
            !NativeMethods.GetWindowRect(handle, out var collapsedBounds))
        {
            return;
        }

        _isCollapsed = false;
        _isCollapsedDockRevealed = false;
        _collapsedDockHideTimer.Stop();
        CollapsedDock.Visibility = Visibility.Collapsed;
        ExpandedPinContent.Visibility = Visibility.Visible;
        CollapseMenuItem.Header = "暂时收起";
        Frame.Background = new SolidColorBrush(MediaColor.FromRgb(7, 11, 14));
        Frame.BorderThickness = new Thickness(1);
        Frame.CornerRadius = new CornerRadius(0);
        UpdateLayout();
        AnimateWindowBounds(handle, collapsedBounds, expandedBounds, () =>
        {
            Activate();
            Focus();
            if (_usesCloseButton)
            {
                ShowPinCloseButton(true);
            }
        });
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPinCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPinCloseHotspotMouseEnter(object sender, WpfMouseEventArgs e) =>
        ShowPinCloseButton(false);

    private void OnPinCloseHotspotMouseLeave(object sender, WpfMouseEventArgs e) =>
        FadePinCloseButton(TimeSpan.Zero);

    private void ShowPinCloseButton(bool autoFade)
    {
        PinCloseButton.BeginAnimation(OpacityProperty, null);
        PinCloseButton.Opacity = 1;
        if (autoFade)
        {
            FadePinCloseButton(TimeSpan.FromMilliseconds(950));
        }
    }

    private void FadePinCloseButton(TimeSpan delay)
    {
        var fade = new DoubleAnimation
        {
            BeginTime = delay,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        PinCloseButton.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }

    private async void CopyImage()
    {
        try
        {
            await _clipboardService.CopyImageAsync(_image);
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
        else if (useInitialPosition)
        {
            PlaceAtCenter(workArea);
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
        SetScaleImmediate(_fitScale);
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

    private void PlaceAtCenter(Rect workArea)
    {
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
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
        var size = StickyImageScaleCalculator.Calculate(
            _imageWidthDip,
            _imageHeightDip,
            scale,
            MinimumScale,
            MaximumScale);
        _scale = size.Scale;

        Width = size.Width;
        Height = size.Height;
        if (preserveCenter && IsLoaded)
        {
            Left = centerX - Width / 2;
            Top = centerY - Height / 2;
        }
    }

    private void SetScaleImmediate(double scale)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var current))
        {
            SetScale(scale, true);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var borderX = Math.Max(1, (int)Math.Round(dpi.DpiScaleX));
        var borderY = Math.Max(1, (int)Math.Round(dpi.DpiScaleY));
        var size = StickyImageScaleCalculator.Calculate(
            _imageWidthDip,
            _imageHeightDip,
            scale,
            MinimumScale,
            MaximumScale,
            dpi.DpiScaleX,
            dpi.DpiScaleY,
            borderX,
            borderY);
        if (Math.Abs(size.Scale - _scale) < 0.00001)
        {
            return;
        }

        var centerX = current.Left + current.Width / 2;
        var centerY = current.Top + current.Height / 2;

        _scale = size.Scale;
        NativeMethods.SetWindowPos(
            handle,
            nint.Zero,
            centerX - size.Width / 2,
            centerY - size.Height / 2,
            size.Width,
            size.Height,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoOwnerZOrder);

        // Some Windows configurations independently cap very large window axes.
        // If that happened, reduce the single scale and resize both axes together
        // instead of allowing Stretch=Fill-style aspect distortion.
        if (NativeMethods.GetWindowRect(handle, out var realized) &&
            (Math.Abs(realized.Width - size.Width) > 1 || Math.Abs(realized.Height - size.Height) > 1))
        {
            var fittedScale = StickyImageScaleCalculator.ScaleThatFits(
                _imageWidthDip,
                _imageHeightDip,
                realized.Width,
                realized.Height,
                dpi.DpiScaleX,
                dpi.DpiScaleY,
                borderX,
                borderY);
            var corrected = StickyImageScaleCalculator.Calculate(
                _imageWidthDip,
                _imageHeightDip,
                Math.Min(_scale, fittedScale),
                MinimumScale,
                MaximumScale,
                dpi.DpiScaleX,
                dpi.DpiScaleY,
                borderX,
                borderY);
            _scale = corrected.Scale;
            NativeMethods.SetWindowPos(
                handle,
                nint.Zero,
                centerX - corrected.Width / 2,
                centerY - corrected.Height / 2,
                corrected.Width,
                corrected.Height,
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpNoZOrder |
                NativeMethods.SwpNoOwnerZOrder);
        }
    }

    private void ShowFeedback(string message)
    {
        FeedbackText.Text = message;
        FeedbackBadge.Visibility = Visibility.Visible;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

}
