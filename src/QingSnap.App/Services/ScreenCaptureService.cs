using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsCursor = System.Windows.Forms.Cursor;
using FormsScreen = System.Windows.Forms.Screen;

namespace QingSnap.App.Services;

public sealed class ScreenCaptureService
{
    public ScreenSnapshot CaptureScreenContainingCursor()
    {
        var screen = FormsScreen.FromPoint(FormsCursor.Position);
        return CaptureBounds(screen.Bounds);
    }

    public ScreenSnapshot CaptureScreenContainingRegion(DrawingRectangle region)
    {
        if (!IsRegionVisible(region))
        {
            throw new InvalidOperationException("上一次截图范围已不在当前屏幕布局中，请重新选择区域。");
        }

        var screen = FormsScreen.FromRectangle(region);
        if (!screen.Bounds.Contains(region))
        {
            throw new InvalidOperationException("上一次截图范围跨越了当前屏幕边界，请重新选择区域。");
        }

        return CaptureBounds(screen.Bounds);
    }

    public ScreenSnapshot CaptureRegion(DrawingRectangle region)
    {
        if (!IsRegionVisible(region))
        {
            throw new InvalidOperationException("上一次截图范围已不在当前屏幕布局中，请重新选择区域。");
        }

        return CaptureBounds(region);
    }

    public bool IsRegionVisible(DrawingRectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return false;
        }

        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        return virtualScreen.Contains(region);
    }

    private static ScreenSnapshot CaptureBounds(DrawingRectangle bounds)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Left,
                bounds.Top,
                0,
                0,
                bounds.Size,
                CopyPixelOperation.SourceCopy);
        }

        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return new ScreenSnapshot(bounds, source);
        }
        finally
        {
            NativeMethods.DeleteObject(handle);
        }
    }
}
