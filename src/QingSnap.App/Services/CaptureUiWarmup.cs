using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Views;
using Size = System.Windows.Size;

namespace QingSnap.App.Services;

internal static class CaptureUiWarmup
{
    public static void Run()
    {
        CaptureOverlayWindow? window = null;
        try
        {
            // Synthetic only: no desktop capture, HWND, OCR, history or clipboard.
            var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr32, null,
                new byte[] { 255, 255, 255, 0 }, 4);
            pixel.Freeze();
            window = new CaptureOverlayWindow(new ScreenSnapshot(new System.Drawing.Rectangle(0, 0, 1, 1), pixel));
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(64, 64));
            content.Arrange(new Rect(0, 0, 64, 64));
            content.UpdateLayout();
            var target = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
            target.Render(content);
        }
        catch (Exception exception) { DiagnosticLog.Error("CaptureWarmup", exception, "截图界面预热失败，正常截图仍可使用。"); }
        finally { window?.Close(); }
    }
}
