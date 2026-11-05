using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;
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

        return new ScreenSnapshot(bounds, CreateScreenImage(bitmap));
    }

    internal static BitmapSource CreateScreenImage(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            // CopyFromScreen's fourth byte is not meaningful alpha. Bgr32 keeps
            // the desktop opaque, just like the previous HBITMAP conversion.
            var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null, data.Scan0,
                checked(data.Stride * bitmap.Height), data.Stride);
            source.Freeze();
            return source;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
