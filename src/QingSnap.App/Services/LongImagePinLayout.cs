using System.Drawing;

namespace QingSnap.App.Services;

internal static class LongImagePinLayout
{
    public static bool NeedsReader(int width, int height, Rectangle? captureRegion) =>
        (captureRegion is { Width: > 0, Height: > 0 } region &&
         width == region.Width && height > region.Height + 1) ||
        height / (double)Math.Max(1, width) >= 2.15;

    // Capture bounds are physical pixels; WPF viewport dimensions are DIPs.
    public static (double Width, double Height) Viewport(Rectangle bounds, double dpiX, double dpiY) =>
        (Math.Max(1, bounds.Width / dpiX - 2), Math.Max(1, bounds.Height / dpiY - 2));
}
