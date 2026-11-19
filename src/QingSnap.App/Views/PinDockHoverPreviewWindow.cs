using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Infrastructure;
using QingSnap.App.Services;
using Image = System.Windows.Controls.Image;

namespace QingSnap.App.Views;

// Pure presentation: never steals focus or pointer hits from the permanent dock.
internal sealed class PinDockHoverPreviewWindow : Window
{
    private readonly Image _image;
    private readonly NativeMethods.NativeRectangle _initialBounds;
    private HwndSource? _source;
    private double _previewOpacity;
    internal bool IsReady { get; private set; }
    internal event EventHandler? Ready;

    internal PinDockHoverPreviewWindow(ImageSource image, NativeMethods.NativeRectangle initialBounds, double opacity)
    {
        _initialBounds = initialBounds;
        _previewOpacity = opacity;
        Title = "QingSnap 收纳悬停预览";
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        IsHitTestVisible = false;
        Background = System.Windows.Media.Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Math.Max(1, initialBounds.Width);
        Height = Math.Max(1, initialBounds.Height);
        Opacity = 0; // First frame appears only after native placement/layout.
        _image = new Image { Source = image, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);
        var border = new Border { Child = _image, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BorderBrushProperty, "ThemeAccentBrush");
        Content = border;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnFirstContentRendered;
        Closed += OnClosed;
    }

    internal void SetPreviewOpacity(double value)
    {
        _previewOpacity = Math.Clamp(value, 0.2, 1);
        if (IsReady) Opacity = _previewOpacity;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnMessage);
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle,
            new nint(style | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow | NativeMethods.WsExTransparent));
        if (!NativeMethods.SetWindowPos(handle, nint.Zero, _initialBounds.Left, _initialBounds.Top,
                _initialBounds.Width, _initialBounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoOwnerZOrder))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法放置收纳预览。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativeMethods.NativePoint Reserved, MaximumSize, MaximumPosition, MinimumTrackSize, MaximumTrackSize;
    }

    private nint OnMessage(nint handle, int message, nint wp, nint lp, ref bool handled)
    {
        if (message == NativeMethods.WmNcHitTest) { handled = true; return new nint(-1); }
        if (message == 0x0021) { handled = true; return new nint(3); }
        if (message == 0x0024 && lp != 0)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lp);
            info.MaximumTrackSize = new() { X = PinSurfaceBudget.MaximumDimension, Y = PinSurfaceBudget.MaximumDimension };
            info.MinimumTrackSize = new() { X = 1, Y = 1 };
            Marshal.StructureToPtr(info, lp, false);
        }
        return nint.Zero;
    }

    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        IsReady = true;
        Opacity = _previewOpacity;
        Ready?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        ContentRendered -= OnFirstContentRendered;
        Closed -= OnClosed;
        if (_source is { IsDisposed: false }) _source.RemoveHook(OnMessage);
        _source = null;
        _image.Source = null;
        Content = null;
        Ready = null;
        IsReady = false;
    }
}
