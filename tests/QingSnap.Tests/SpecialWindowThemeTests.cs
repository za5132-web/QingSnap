using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class SpecialWindowThemeTests
{
    [Fact]
    public void CaptureChromeChangesThemeWithoutChangingScreenshotShadeOrColorSwatches() => Run(() =>
    {
        var image = SyntheticImage();
        var window = new CaptureOverlayWindow(new ScreenSnapshot(
            new System.Drawing.Rectangle(-800, 0, 800, 600), image));
        try
        {
            window.Resources.MergedDictionaries.Add(ThemeService.LoadDictionary("Dark"));
            Call(window, "SetAnnotationTool", CaptureAnnotationTool.Rectangle);
            var geometry = (Border)window.FindName("GeometryEditor");
            var tool = (Button)window.FindName("RectangleButton");
            var palette = (StackPanel)window.FindName("AnnotationPalettePanel");
            var swatch = (Button)palette.Children[1];
            var shade = (System.Windows.Shapes.Rectangle)window.FindName("TopShade");
            var shadeColor = ColorOf(shade.Fill);
            var swatchColor = ColorOf(swatch.Background);
            Assert.Equal(ColorOf((Brush)window.FindResource("ThemeOverlayPanelBrush")), ColorOf(geometry.Background));

            window.Resources.MergedDictionaries[0] = ThemeService.LoadDictionary("Light");
            Assert.Equal(ColorOf((Brush)window.FindResource("ThemeOverlayPanelBrush")), ColorOf(geometry.Background));
            Assert.Equal(ColorOf((Brush)window.FindResource("ThemeDangerSurfaceBrush")), ColorOf(tool.Background));
            Assert.Equal(shadeColor, ColorOf(shade.Fill));
            Assert.Equal(swatchColor, ColorOf(swatch.Background));
            Assert.Same(image, ((Image)window.FindName("BackgroundImage")).Source);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PinRestoresCurrentThemeBackdropAndRetainsOriginalImage() => Run(() =>
    {
        var settings = new AppSettingsService(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "QingSnap-theme-pin-test", Guid.NewGuid().ToString("N")));
        using var clipboard = new ClipboardService();
        using var ocr = new OcrService(settings);
        var image = SyntheticImage();
        var window = new StickyImageWindow(image, "synthetic-theme", clipboard, ocr, new AppSettings(),
            new System.Drawing.Rectangle(-700, 100, 800, 600));
        try
        {
            window.Resources.MergedDictionaries.Add(ThemeService.LoadDictionary("Dark"));
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Call(window, "OnLoaded", window, new RoutedEventArgs());
            Assert.True(NativeMethods.GetWindowRect(handle, out var bounds));
            window.Show();
            window.Opacity = 0;
            typeof(StickyImageWindow).GetField("_expandedBounds", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, (NativeMethods.NativeRectangle?)bounds);
            var frame = (Border)window.FindName("Frame");
            Call(window, "PrepareCollapsedVisualState");
            window.Resources.MergedDictionaries[0] = ThemeService.LoadDictionary("Light");
            Call(window, "RestoreFromDock");
            Call(window, "CompletePinMotion", true);
            Assert.Equal(ColorOf((Brush)window.FindResource("ThemeImageBackdropBrush")), ColorOf(frame.Background));
            window.Resources.MergedDictionaries[0] = ThemeService.LoadDictionary("Dark");
            Assert.Equal(ColorOf((Brush)window.FindResource("ThemeImageBackdropBrush")), ColorOf(frame.Background));
            Assert.Same(image, window.CreatePinOutput());
        }
        finally { window.Close(); }
    });

    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static BitmapSource SyntheticImage() => BitmapSource.Create(800, 600, 96, 96,
        PixelFormats.Bgra32, null, new byte[800 * 600 * 4], 800 * 4);

    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                {
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.Resources["UiFont"] = new FontFamily("Segoe UI");
                    application.Resources["DataFont"] = new FontFamily("Consolas");
                }
                action();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Call(object target, string name, params object[] arguments) =>
        target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .Invoke(target, arguments);
}
