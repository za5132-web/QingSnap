using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingSnap.App.Infrastructure;
using QingSnap.App.Services;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace QingSnap.App.Views;

/// <summary>
/// A short-lived, fixed-HWND carrier for pin zooms. Present only changes retained
/// visuals; the owning pin is never hidden, resized, shown or closed by this class.
/// All coordinates accepted by this class are physical desktop pixels.
/// </summary>
internal sealed class PinZoomPreviewWindow : Window, IDisposable
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private readonly DrawingRectangle _carrierBounds;
    private readonly double _borderPhysicalX;
    private readonly double _borderPhysicalY;
    private readonly Canvas _canvas;
    private readonly Border _frame;
    private readonly WpfRectangle _rectangle;
    private readonly VisualBrush _sourceBrush;
    private HwndSource? _nativeSource;
    private BitmapSource? _frozenBitmap;
    private bool _resourcesReleased;
    private bool _initiallyCloaked;
    private bool _usesOpacityFallback;
    private bool _hasPresentation;
    private double _centerX;
    private double _centerY;
    private double _contentWidthPhysical;
    private double _contentHeightPhysical;

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativeMethods.NativePoint Reserved;
        public NativeMethods.NativePoint MaximumSize;
        public NativeMethods.NativePoint MaximumPosition;
        public NativeMethods.NativePoint MinimumTrackSize;
        public NativeMethods.NativePoint MaximumTrackSize;
    }

    public PinZoomPreviewWindow(Window owner, FrameworkElement source, DrawingRectangle carrierBounds,
        double borderPhysicalX = 1, double borderPhysicalY = 1)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(source);
        owner.VerifyAccess();
        source.VerifyAccess();
        ValidateSurface(carrierBounds.Width, carrierBounds.Height);
        if (!double.IsFinite(borderPhysicalX) || !double.IsFinite(borderPhysicalY) ||
            borderPhysicalX < 0 || borderPhysicalY < 0 ||
            borderPhysicalX * 2 >= carrierBounds.Width || borderPhysicalY * 2 >= carrierBounds.Height)
            throw new ArgumentOutOfRangeException(nameof(borderPhysicalX));

        _carrierBounds = carrierBounds;
        _borderPhysicalX = borderPhysicalX;
        _borderPhysicalY = borderPhysicalY;
        // Do not make this a native owned window: DWM propagates an owner's
        // cloak to owned windows, which would hide the preview together with
        // the original pin. The coordinator owns and disposes our lifetime.
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = System.Windows.Media.Brushes.Transparent;
        UseLayoutRounding = false;
        SnapsToDevicePixels = false;
        Title = "QingSnap 贴图缩放预览";
        Width = carrierBounds.Width;
        Height = carrierBounds.Height;

        _sourceBrush = new VisualBrush(source)
        {
            AutoLayoutContent = false,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, Math.Max(1, source.ActualWidth), Math.Max(1, source.ActualHeight)),
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top
        };
        _rectangle = new WpfRectangle
        {
            Fill = _sourceBrush,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false
        };
        RenderOptions.SetBitmapScalingMode(_rectangle, BitmapScalingMode.Linear);
        _frame = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            Child = _rectangle,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false
        };
        _frame.SetResourceReference(Border.BorderBrushProperty, "ThemeAccentBrush");
        _frame.SetResourceReference(Border.BackgroundProperty, "ThemeImageBackdropBrush");
        _canvas = new Canvas
        {
            ClipToBounds = true,
            Background = null,
            UseLayoutRounding = false,
            SnapsToDevicePixels = false
        };
        _canvas.Children.Add(_frame);
        Content = _canvas;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnFirstContentRendered;
        DpiChanged += OnPreviewDpiChanged;
        PreviewMouseWheel += OnPreviewWheel;
        PreviewMouseDown += OnPreviewPointerDown;
        Closed += OnClosed;
        UpdateCanvasMetrics();
    }

    public bool IsReady { get; private set; }
    public event EventHandler? Ready;

    /// <summary>Key state is the integer bit field of WPF ModifierKeys.</summary>
    public event Action<int, int, DrawingPoint>? WheelRequested;
    public event Action<MouseButton, DrawingPoint>? PointerPressed;

    /// <summary>Update an outer physical rectangle, including the physical border.</summary>
    public void UpdateVisibleBounds(DrawingRectangle outerBounds) => Present(
        outerBounds.Left + outerBounds.Width / 2D, outerBounds.Top + outerBounds.Height / 2D,
        Math.Max(1, outerBounds.Width - _borderPhysicalX * 2),
        Math.Max(1, outerBounds.Height - _borderPhysicalY * 2));

    /// <summary>Keep fractional position and scale; no HWND operation or bitmap allocation.</summary>
    public void Present(double centerX, double centerY, double contentWidthPx, double contentHeightPx)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_resourcesReleased, this);
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY) ||
            !double.IsFinite(contentWidthPx) || !double.IsFinite(contentHeightPx) ||
            contentWidthPx <= 0 || contentHeightPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentWidthPx));
        ValidateSurface(contentWidthPx + _borderPhysicalX * 2, contentHeightPx + _borderPhysicalY * 2);
        _centerX = centerX;
        _centerY = centerY;
        _contentWidthPhysical = contentWidthPx;
        _contentHeightPhysical = contentHeightPx;
        _hasPresentation = true;
        ApplyPresentation();
    }

    /// <summary>
    /// Snapshot the current rectangle once before the owner changes its source
    /// layout during handoff. Resolution/area never exceed this carrier's budget.
    /// Subsequent Present calls may still position/scale the frozen content.
    /// </summary>
    public void FreezeContent()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_resourcesReleased, this);
        if (_frozenBitmap is not null || !_hasPresentation) return;
        try
        {
            var ratio = Math.Min(1, Math.Min(_carrierBounds.Width / _contentWidthPhysical,
                _carrierBounds.Height / _contentHeightPhysical));
            var width = _contentWidthPhysical * ratio;
            var height = _contentHeightPhysical * ratio;
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(width));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(height));
            ValidateSurface(pixelWidth, pixelHeight);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
                context.DrawRectangle(_rectangle.Fill, null, new Rect(0, 0, width, height));
            var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            bitmap.Freeze();
            var brush = new ImageBrush(bitmap)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            brush.Freeze();
            _frozenBitmap = bitmap;
            _rectangle.Fill = brush;
            _sourceBrush.Visual = null;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinZoomPreview", exception, "固定贴图缩放预览内容失败。");
            throw;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        var handle = new WindowInteropHelper(this).Handle;
        _nativeSource = HwndSource.FromHwnd(handle);
        _nativeSource?.AddHook(OnNativeMessage);
        _initiallyCloaked = NativeMethods.SetWindowCloaked(handle, true);
        if (!_initiallyCloaked) { _usesOpacityFallback = true; Opacity = 0; }
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle,
            new nint(style | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow));
        if (!NativeMethods.SetWindowPos(handle, nint.Zero, _carrierBounds.Left, _carrierBounds.Top,
                _carrierBounds.Width, _carrierBounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoOwnerZOrder))
        {
            var exception = new Win32Exception(Marshal.GetLastWin32Error(), "无法放置贴图缩放预览窗口。");
            DiagnosticLog.Error("PinZoomPreview", exception, "初始化预览承载窗口失败。");
            throw exception;
        }
        UpdateCanvasMetrics();
        if (_hasPresentation) ApplyPresentation();
    }

    private nint OnNativeMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return new nint(MaNoActivate);
        }
        if (message == WmGetMinMaxInfo && lParam != nint.Zero)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MaximumTrackSize.X = _carrierBounds.Width;
            info.MaximumTrackSize.Y = _carrierBounds.Height;
            info.MinimumTrackSize.X = 1;
            info.MinimumTrackSize.Y = 1;
            Marshal.StructureToPtr(info, lParam, false);
            // WPF must also cache the adjusted limits for its measure pass.
            handled = false;
        }
        return nint.Zero;
    }

    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        if (_resourcesReleased) return;
        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        if (_initiallyCloaked)
        {
            if (!NativeMethods.SetWindowCloaked(handle, false))
            {
                DiagnosticLog.Warning("PinZoomPreview", "无法显示贴图缩放预览，保留原贴图窗口。");
                Close();
                return;
            }
            _initiallyCloaked = false;
        }
        else if (_usesOpacityFallback) { Opacity = 1; UpdateLayout(); }
        NativeMethods.DwmFlush();
        IsReady = true;
        Ready?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCanvasMetrics()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _canvas.Width = _carrierBounds.Width / dpi.DpiScaleX;
        _canvas.Height = _carrierBounds.Height / dpi.DpiScaleY;
        _frame.BorderThickness = new Thickness(_borderPhysicalX / dpi.DpiScaleX,
            _borderPhysicalY / dpi.DpiScaleY, _borderPhysicalX / dpi.DpiScaleX, _borderPhysicalY / dpi.DpiScaleY);
    }

    private void ApplyPresentation()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _rectangle.Width = _contentWidthPhysical / dpi.DpiScaleX;
        _rectangle.Height = _contentHeightPhysical / dpi.DpiScaleY;
        _frame.Width = (_contentWidthPhysical + _borderPhysicalX * 2) / dpi.DpiScaleX;
        _frame.Height = (_contentHeightPhysical + _borderPhysicalY * 2) / dpi.DpiScaleY;
        Canvas.SetLeft(_frame, (_centerX - _contentWidthPhysical / 2 - _borderPhysicalX - _carrierBounds.Left) / dpi.DpiScaleX);
        Canvas.SetTop(_frame, (_centerY - _contentHeightPhysical / 2 - _borderPhysicalY - _carrierBounds.Top) / dpi.DpiScaleY);
    }

    private void OnPreviewDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        UpdateCanvasMetrics();
        if (_hasPresentation) ApplyPresentation();
    }

    private DrawingPoint PointerPosition(System.Windows.Input.MouseEventArgs e)
    {
        if (NativeMethods.GetCursorPos(out var point)) return new DrawingPoint(point.X, point.Y);
        var screen = PointToScreen(e.GetPosition(this));
        return new DrawingPoint((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
    }

    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        WheelRequested?.Invoke(e.Delta, (int)Keyboard.Modifiers, PointerPosition(e));
    }

    private void OnPreviewPointerDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PointerPressed?.Invoke(e.ChangedButton, PointerPosition(e));
    }

    private static void ValidateSurface(double width, double height)
    {
        if (width <= 0 || height <= 0 || width > PinSurfaceBudget.MaximumDimension ||
            height > PinSurfaceBudget.MaximumDimension || Math.Ceiling(width) * Math.Ceiling(height) > PinSurfaceBudget.DefaultMaximumPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "贴图缩放预览超出安全表面预算。");
    }

    private void OnClosed(object? sender, EventArgs e) => ReleaseResources();

    private void ReleaseResources()
    {
        if (_resourcesReleased) return;
        _resourcesReleased = true;
        IsReady = false;
        SourceInitialized -= OnSourceInitialized;
        ContentRendered -= OnFirstContentRendered;
        DpiChanged -= OnPreviewDpiChanged;
        PreviewMouseWheel -= OnPreviewWheel;
        PreviewMouseDown -= OnPreviewPointerDown;
        Closed -= OnClosed;
        if (_nativeSource is { IsDisposed: false }) _nativeSource.RemoveHook(OnNativeMessage);
        _nativeSource = null;
        _rectangle.Fill = null;
        _sourceBrush.Visual = null;
        _frozenBitmap = null;
        _frame.Child = null;
        _canvas.Children.Clear();
        Content = null;
        Ready = null;
        WheelRequested = null;
        PointerPressed = null;
    }

    public void Dispose()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Dispose, DispatcherPriority.Normal);
            return;
        }
        if (_resourcesReleased) return;
        Close();
        ReleaseResources();
    }
}
