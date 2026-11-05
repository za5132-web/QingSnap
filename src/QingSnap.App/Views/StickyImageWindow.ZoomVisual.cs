using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using QingSnap.App.Infrastructure;
using Point = System.Windows.Point;

namespace QingSnap.App.Views;

public partial class StickyImageWindow
{
    private NativeMethods.NativeRectangle? _zoomVisualBounds;
    private double _zoomVisualCenterX;
    private double _zoomVisualCenterY;

    private (double X, double Y) GetPinZoomCenter(NativeMethods.NativeRectangle bounds)
    {
        if (_zoomVisualBounds is { } previous &&
            (previous.Width != bounds.Width || previous.Height != bounds.Height))
            return (bounds.Left + bounds.Width / 2D, bounds.Top + bounds.Height / 2D);
        if (NormalPinSurface.ActualWidth <= 0 || NormalPinSurface.ActualHeight <= 0)
            return (bounds.Left + bounds.Width / 2D, bounds.Top + bounds.Height / 2D);
        var dpi = VisualTreeHelper.GetDpi(this);
        var point = NormalPinSurface.TranslatePoint(
            new Point(NormalPinSurface.ActualWidth / 2, NormalPinSurface.ActualHeight / 2), this);
        return (bounds.Left + point.X * dpi.DpiScaleX, bounds.Top + point.Y * dpi.DpiScaleY);
    }

    private void ApplyPinZoomAnchor(NativeMethods.NativeRectangle bounds, double centerX, double centerY)
    {
        if (_isCollapsed || _isLongReaderMode) return;
        _zoomVisualBounds = bounds;
        _zoomVisualCenterX = centerX;
        _zoomVisualCenterY = centerY;
        RefreshPinZoomAnchor(bounds);
    }

    private void SetPinZoomContentSize(double scale)
    {
        if (_isCollapsed || _isLongReaderMode) return;
        // The image must use one continuous scale, not min(round(width)/sourceW,
        // round(height)/sourceH). Thin images otherwise plateau on the shorter
        // axis and then jump many pixels on the longer one.
        NormalPinSurface.Width = _imageWidthDip * scale;
        NormalPinSurface.Height = _imageHeightDip * scale;
    }

    private void RefreshPinZoomAnchor(NativeMethods.NativeRectangle bounds)
    {
        if (NormalPinSurface.ActualWidth <= 0 || NormalPinSurface.ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var center = NormalPinSurface.TranslatePoint(
            new Point(NormalPinSurface.ActualWidth / 2, NormalPinSurface.ActualHeight / 2), this);
        // Subtract the previous translation before deriving the new absolute
        // offset. Never accumulate rounded HWND/PointToScreen coordinates.
        NormalPinZoomOffset.X = (_zoomVisualCenterX - bounds.Left) / dpi.DpiScaleX -
                                (center.X - NormalPinZoomOffset.X);
        NormalPinZoomOffset.Y = (_zoomVisualCenterY - bounds.Top) / dpi.DpiScaleY -
                                (center.Y - NormalPinZoomOffset.Y);
    }

    private void OnNormalPinSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoomVisualBounds is not { } expected || _isCollapsed || _isLongReaderMode) return;
        var handle = new WindowInteropHelper(this).Handle;
        // A manual drag or DPI resize is not another zoom frame. Do not pull
        // that window back toward an old absolute screen coordinate.
        if (handle != nint.Zero && NativeMethods.GetWindowRect(handle, out var current) &&
            current.Left == expected.Left && current.Top == expected.Top &&
            current.Width == expected.Width && current.Height == expected.Height)
            RefreshPinZoomAnchor(current);
    }

    private void ResetPinZoomAnchor()
    {
        _zoomVisualBounds = null;
        NormalPinSurface.Width = double.NaN;
        NormalPinSurface.Height = double.NaN;
        NormalPinZoomOffset.X = 0;
        NormalPinZoomOffset.Y = 0;
    }
}
