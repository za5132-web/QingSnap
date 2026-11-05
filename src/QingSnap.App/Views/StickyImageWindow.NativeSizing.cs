using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using QingSnap.App.Infrastructure;
using QingSnap.App.Services;

namespace QingSnap.App.Views;

public partial class StickyImageWindow
{
    private const int WmGetMinMaxInfo = 0x0024;
    private HwndSource? _pinSizingSource;
    private PinSurfaceBudget _pinSurfaceBudget = PinSurfaceBudget.FromHardware(0, 0, 0);
    private double? _rejectedZoomScaleLimit;
    private double _rejectedZoomDpiX;
    private double _rejectedZoomDpiY;

    [StructLayout(LayoutKind.Sequential)]
    private struct PinMinMaxInfo
    {
        public NativeMethods.NativePoint Reserved;
        public NativeMethods.NativePoint MaximumSize;
        public NativeMethods.NativePoint MaximumPosition;
        public NativeMethods.NativePoint MinimumTrackSize;
        public NativeMethods.NativePoint MaximumTrackSize;
    }

    private void InitializePinNativeSizing(nint handle)
    {
        try
        {
            var hardware = RenderCapability.MaxHardwareTextureSize;
            _pinSurfaceBudget = PinSurfaceBudget.FromHardware(hardware.Width, hardware.Height, RenderCapability.Tier >> 16);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinZoom", exception, "读取贴图渲染尺寸限制失败，使用安全回退预算。");
        }
        _pinSizingSource = HwndSource.FromHwnd(handle);
        _pinSizingSource?.AddHook(OnPinSizingMessage);
    }

    private nint OnPinSizingMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo && lParam != nint.Zero)
        {
            var info = Marshal.PtrToStructure<PinMinMaxInfo>(lParam);
            // DefWindowProc defaults these to the virtual desktop size. Only this
            // pin HWND opts out; maximization coordinates/size remain untouched.
            info.MaximumTrackSize.X = _pinSurfaceBudget.MaximumWidth;
            info.MaximumTrackSize.Y = _pinSurfaceBudget.MaximumHeight;
            // This borderless HWND has no system caption/resize-frame minimum.
            // Preserve explicit WPF minima; zoom's 24px floor stays in its existing
            // scale calculator rather than inheriting a system caption-button width.
            var dpi = VisualTreeHelper.GetDpi(this);
            info.MinimumTrackSize.X = Math.Max(1, (int)Math.Ceiling(MinWidth * dpi.DpiScaleX));
            info.MinimumTrackSize.Y = Math.Max(1, (int)Math.Ceiling(MinHeight * dpi.DpiScaleY));
            Marshal.StructureToPtr(info, lParam, false);
            // Let Window's own message handler consume these adjusted limits too.
            // It caches them for MeasureOverride; marking this handled would grow
            // the HWND but leave its WPF content constrained to the old desktop.
            handled = false;
        }
        return nint.Zero;
    }

    private void ReleasePinNativeSizing()
    {
        if (_pinSizingSource is { IsDisposed: false } source) source.RemoveHook(OnPinSizingMessage);
        _pinSizingSource = null;
    }

    private double MaximumSafeZoomScale(double dpiX, double dpiY, double borderX, double borderY)
    {
        var maximum = _pinSurfaceBudget.MaximumScaleFor(
            _imageWidthDip, _imageHeightDip, dpiX, dpiY, borderX, borderY, MaximumScale);
        if (_rejectedZoomScaleLimit is { } rejected &&
            Math.Abs(dpiX - _rejectedZoomDpiX) < 0.0001 && Math.Abs(dpiY - _rejectedZoomDpiY) < 0.0001)
            maximum = Math.Min(maximum, rejected);
        return maximum;
    }
}
