using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingSnap.App.Controls;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class ThemeChromeRegressionTests
{
    [Fact]
    public void ExistingCapturePinToolbarTutorialAndQrTipFollowThemeWithoutChangingImage() => Run(() =>
    {
        var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources["UiFont"] = new FontFamily("Segoe UI");
        application.Resources["DataFont"] = new FontFamily("Consolas");
        using var themes = new ThemeService(application.Resources, Dispatcher.CurrentDispatcher, false);
        themes.Apply("Dark");
        var settings = new AppSettingsService(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "QingSnap-theme-chrome", Guid.NewGuid().ToString("N")));
        using var clipboard = new ClipboardService();
        using var ocr = new OcrService(settings);
        var pixels = new byte[800 * 600 * 4];
        Array.Fill(pixels, (byte)255);
        var source = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgra32, null, pixels, 800 * 4);
        source.Freeze();
        var capture = new CaptureOverlayWindow(new ScreenSnapshot(new System.Drawing.Rectangle(0, 0, 800, 600), source));
        var pin = new StickyImageWindow(source, "synthetic-theme", clipboard, ocr, new AppSettings(),
            new System.Drawing.Rectangle(-10000, -10000, 800, 600)) { ShowActivated = false };
        var tutorial = new FirstRunTutorialWindow();
        ToolTip? qrTip = null;
        try
        {
            Layout((FrameworkElement)capture.Content, 800, 600);
            Call(capture, "SetSelectionFromPixels", new System.Drawing.Rectangle(50, 50, 500, 300));
            pin.Show();
            var toolbarField = pin.GetType().GetField("_pinAnnotationToolbar", BindingFlags.NonPublic | BindingFlags.Instance)!;
            toolbarField.SetValue(pin, new Popup());
            Call(pin, "BeginPinAnnotations");
            ((Popup)toolbarField.GetValue(pin)!).IsOpen = false;
            toolbarField.SetValue(pin, null);
            Call(pin, "EnsurePinAnnotationToolbar");
            var popup = (Popup)pin.GetType().GetField("_pinAnnotationToolbar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pin)!;
            var pinChrome = Assert.IsType<Border>(popup.Child);
            // A never-opened Popup is not a presentation root and cannot receive
            // Application resource invalidations. Exercise the real (invisible) popup.
            pinChrome.Opacity = 0;
            popup.IsOpen = true;
            var qr = new QrCodeHotspotMarker(new QrCodeResult("https://example.com/", "QR_CODE", 50, 50));
            ((Grid)pin.FindName("ExpandedPinContent")).Children.Add(qr);
            qrTip = Assert.IsType<ToolTip>(qr.ToolTip);
            qrTip.PlacementTarget = qr;
            qrTip.Opacity = 0;
            qrTip.IsOpen = true;
            var qrChrome = Assert.IsType<Border>(qrTip.Content);
            var before = Pixel(pin.CreatePinOutput());
            foreach (var mode in new[] { "Light", "Dark", "Light", "Dark" })
            {
                themes.Apply(mode);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Layout((FrameworkElement)capture.Content, 800, 600);
                Assert.Equal(themes.GetColor("ThemeOverlayPanelBrush"), Color(((Border)capture.FindName("CaptureToolbar")).Background));
                Assert.Equal(themes.GetColor("ThemeTextBrush"), Color(tutorial.Foreground));
                Assert.Equal(themes.GetColor("ThemeOverlayPanelBrush"), Color(pinChrome.Background));
                Assert.Equal(themes.GetColor("ThemeOverlayPanelBrush"), Color(qrChrome.Background));
                Assert.Equal(before, Pixel(pin.CreatePinOutput()));
                Assert.Equal(new System.Drawing.Rectangle(50, 50, 500, 300),
                    capture.GetType().GetMethod("GetLocalRegion", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(capture, null));
            }
            qrTip.IsOpen = false;
        }
        finally { if (qrTip is not null) qrTip.IsOpen = false; capture.Close(); pin.Close(); tutorial.Close(); themes.Apply("Dark"); }
    });

    private static byte[] Pixel(BitmapSource bitmap)
    {
        var result = new byte[4];
        bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), result, 4, 0);
        return result;
    }

    private static System.Windows.Media.Color Color(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static void Layout(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void Call(object target, string name, params object[] args) => target.GetType()
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args);

    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "Theme UI test did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
