using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace QingSnap.App.Views;

public partial class LongCaptureOverlayWindow : Window
{
    private const double RailThickness = 2;

    private readonly DrawingRectangle _captureRegion;
    private readonly DrawingRectangle _screenBounds;
    private HwndSource? _windowSource;

    public LongCaptureOverlayWindow(DrawingRectangle captureRegion, LongCaptureMode mode)
    {
        _captureRegion = captureRegion;
        _screenBounds = FormsScreen.FromRectangle(captureRegion).Bounds;
        InitializeComponent();

        if (mode == LongCaptureMode.Manual)
        {
            ModeText.Text = "MANUAL LONG CAPTURE";
            StateText.Text = "选区保持显示 · 滚动页面后截取下一屏";
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        extendedStyle |= NativeMethods.WsExTransparent |
                         NativeMethods.WsExNoActivate |
                         NativeMethods.WsExToolWindow;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new nint(extendedStyle));

        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowProcedure);
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            _screenBounds.X,
            _screenBounds.Y,
            _screenBounds.Width,
            _screenBounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateVisuals();

    private void OnClosed(object? sender, EventArgs e)
    {
        _windowSource?.RemoveHook(WindowProcedure);
        _windowSource = null;
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmNcHitTest)
        {
            handled = true;
            return NativeMethods.HtTransparent;
        }

        return nint.Zero;
    }

    private void UpdateVisuals()
    {
        var scaleX = Surface.ActualWidth / Math.Max(1, _screenBounds.Width);
        var scaleY = Surface.ActualHeight / Math.Max(1, _screenBounds.Height);
        var left = (_captureRegion.Left - _screenBounds.Left) * scaleX;
        var top = (_captureRegion.Top - _screenBounds.Top) * scaleY;
        var right = (_captureRegion.Right - _screenBounds.Left) * scaleX;
        var bottom = (_captureRegion.Bottom - _screenBounds.Top) * scaleY;

        SetRectangle(TopShade, 0, 0, Surface.ActualWidth, top);
        SetRectangle(BottomShade, 0, bottom, Surface.ActualWidth, Surface.ActualHeight - bottom);
        SetRectangle(LeftShade, 0, top, left, bottom - top);
        SetRectangle(RightShade, right, top, Surface.ActualWidth - right, bottom - top);

        SetRectangle(TopRail, left, Math.Max(0, top - RailThickness), right - left, RailThickness);
        SetRectangle(BottomRail, left, bottom, right - left, RailThickness);
        SetRectangle(LeftRail, Math.Max(0, left - RailThickness), top, RailThickness, bottom - top);
        SetRectangle(RightRail, right, top, RailThickness, bottom - top);
        PositionBadge(left, top, right, bottom);
    }

    private void PositionBadge(double left, double top, double right, double bottom)
    {
        StateBadge.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = StateBadge.DesiredSize.Width;
        var height = StateBadge.DesiredSize.Height;
        const double gap = 10;

        if (bottom + gap + height <= Surface.ActualHeight)
        {
            SetPosition(StateBadge, Math.Clamp(left, 8, Math.Max(8, Surface.ActualWidth - width - 8)), bottom + gap);
            return;
        }

        if (top - gap - height >= 0)
        {
            SetPosition(StateBadge, Math.Clamp(left, 8, Math.Max(8, Surface.ActualWidth - width - 8)), top - gap - height);
            return;
        }

        if (right + gap + width <= Surface.ActualWidth)
        {
            SetPosition(StateBadge, right + gap, Math.Clamp(top, 8, Math.Max(8, Surface.ActualHeight - height - 8)));
            return;
        }

        if (left - gap - width >= 0)
        {
            SetPosition(StateBadge, left - gap - width, Math.Clamp(top, 8, Math.Max(8, Surface.ActualHeight - height - 8)));
            return;
        }

        StateBadge.Visibility = Visibility.Collapsed;
    }

    private static void SetRectangle(ShapeRectangle rectangle, double left, double top, double width, double height)
    {
        SetPosition(rectangle, left, top);
        rectangle.Width = Math.Max(0, width);
        rectangle.Height = Math.Max(0, height);
    }

    private static void SetPosition(FrameworkElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
    }
}
