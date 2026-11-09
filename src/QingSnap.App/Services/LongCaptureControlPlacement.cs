using System.Drawing;

namespace QingSnap.App.Services;

internal static class LongCaptureControlPlacement
{
    // All values are physical screen pixels, not WPF DIPs. Call for each monitor
    // using that monitor's scaled control size; never allow the HUD into the
    // captured pixels, even when the capture spans monitors with different DPI.
    public static Rectangle? FindOutside(
        Rectangle capture,
        Rectangle screenBounds,
        Size desiredSize,
        int gap = 8)
    {
        if (capture.Width <= 0 || capture.Height <= 0 ||
            screenBounds.Width <= 0 || screenBounds.Height <= 0 ||
            desiredSize.Width <= 0 || desiredSize.Height <= 0 ||
            desiredSize.Width > screenBounds.Width || desiredSize.Height > screenBounds.Height)
        {
            return null;
        }

        // Use wider intermediates for edge arithmetic before producing the HWND
        // rectangle. Monitor origins may be negative.
        var spacing = Math.Max(0, gap);
        var screenLeft = (long)screenBounds.Left;
        var screenTop = (long)screenBounds.Top;
        var screenRight = screenLeft + screenBounds.Width;
        var screenBottom = screenTop + screenBounds.Height;
        var captureLeft = (long)capture.Left;
        var captureTop = (long)capture.Top;
        var captureRight = captureLeft + capture.Width;
        var captureBottom = captureTop + capture.Height;
        var width = desiredSize.Width;
        var height = desiredSize.Height;
        var nearX = Math.Clamp(captureLeft, screenLeft, screenRight - width);
        var nearY = Math.Clamp(captureTop, screenTop, screenBottom - height);

        (long X, long Y)[] candidates =
        [
            (nearX, Math.Max(screenTop, captureBottom + spacing)),
            (nearX, Math.Min(screenBottom - height, captureTop - spacing - height)),
            (Math.Max(screenLeft, captureRight + spacing), nearY),
            (Math.Min(screenRight - width, captureLeft - spacing - width), nearY)
        ];

        foreach (var (x, y) in candidates)
        {
            if (x < screenLeft || y < screenTop ||
                x + width > screenRight || y + height > screenBottom ||
                x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue)
            {
                continue;
            }

            var separated = x >= captureRight + spacing ||
                            x + width <= captureLeft - spacing ||
                            y >= captureBottom + spacing ||
                            y + height <= captureTop - spacing;
            if (separated)
            {
                return new Rectangle((int)x, (int)y, width, height);
            }
        }

        // No safe location: the caller must hide the control while capturing,
        // leaving the persistent outside-region overlay and global stop keys.
        return null;
    }
}
