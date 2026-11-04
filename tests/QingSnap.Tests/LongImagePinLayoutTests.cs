using Rectangle = System.Drawing.Rectangle;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class LongImagePinLayoutTests
{
    [Fact]
    public void ReaderStartsAtCaptureBoundsAndRestoresAfterOverview()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            StickyImageWindow? window = null;
            try
            {
                if (Application.Current is null)
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources["UiFont"] = new FontFamily("Segoe UI");
                    app.Resources["DataFont"] = new FontFamily("Consolas");
                }

                var settings = new AppSettingsService(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "QingSnap-reader-test", Guid.NewGuid().ToString("N")));
                using var clipboard = new ClipboardService();
                using var ocr = new OcrService(settings);
                var image = BitmapSource.Create(1200, 1600, 96, 96, PixelFormats.Bgra32,
                    null, new byte[1200 * 1600 * 4], 1200 * 4);
                window = new StickyImageWindow(image, "synthetic", clipboard, ocr, new AppSettings(),
                    new Rectangle(-800, 100, 1200, 600));
                var handle = new WindowInteropHelper(window).EnsureHandle();
                Call(window, "OnLoaded", window, new RoutedEventArgs());
                Assert.True(NativeMethods.GetWindowRect(handle, out var before));
                Assert.Equal(-801, before.Left);
                Assert.Equal(99, before.Top);
                Assert.Equal(1202, before.Width);
                Assert.Equal(602, before.Height);
                Call(window, "EnterOverviewMode");
                Call(window, "EnterReaderMode", false);
                Assert.True(NativeMethods.GetWindowRect(handle, out var after));
                Assert.Equal(before.Left, after.Left);
                Assert.Equal(before.Top, after.Top);
                Assert.Equal(before.Width, after.Width);
                Assert.Equal(before.Height, after.Height);
                // A repeated toggle must resize again, even if overview scale is unchanged.
                Call(window, "EnterOverviewMode");
                Assert.True(NativeMethods.GetWindowRect(handle, out var overview));
                Assert.True(overview.Width != before.Width || overview.Height != before.Height);
                Call(window, "EnterReaderMode", false);
                Assert.True(NativeMethods.GetWindowRect(handle, out var restored));
                Assert.Equal(before.Width, restored.Width);
                Assert.Equal(before.Height, restored.Height);
                // Restore animation passes through very small heights; the reader rail
                // must remain valid before the full window has expanded.
                var rail = (FrameworkElement)window.FindName("ScrollRail");
                rail.Measure(new System.Windows.Size(6, 20));
                rail.Arrange(new Rect(0, 0, 6, 20));
                Call(window, "UpdateScrollProgress");
                // Activate at the end of restoration requires a shown HWND. Keep
                // this synthetic test window transparent while pumping its timer.
                window.Show();
                window.Opacity = 0;
                Call(window, "CollapseToDock");
                Call(window, "RestoreFromDock");
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(600)
                };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Assert.True(NativeMethods.GetWindowRect(handle, out var expanded));
                Assert.Equal(before.Width, expanded.Width);
                Assert.Equal(before.Height, expanded.Height);
                // Exercise the animated edge-drop path as well as keyboard stashing.
                Call(window, "CollapseToDockAtEdge", expanded, expanded,
                    new NativeMethods.NativePoint(1, 200), new Rectangle(0, 0, 1920, 1080), true);
                PumpAnimation();
                Call(window, "RestoreFromDock");
                PumpAnimation();
                Assert.True(NativeMethods.GetWindowRect(handle, out var edgeRestored));
                Assert.Equal(expanded.Left, edgeRestored.Left);
                Assert.Equal(expanded.Top, edgeRestored.Top);
                Assert.Equal(before.Width, edgeRestored.Width);
                Assert.Equal(before.Height, edgeRestored.Height);
            }
            catch (Exception exception) { failure = exception; }
            finally { window?.Close(); }
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

    private static void PumpAnimation()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void ShortWideScrollingCaptureStillUsesReader() =>
        Assert.True(LongImagePinLayout.NeedsReader(1200, 1000, new Rectangle(-1600, 90, 1200, 600)));

    [Fact]
    public void OrdinaryCaptureKeepsExistingInteraction() =>
        Assert.False(LongImagePinLayout.NeedsReader(1200, 600, new Rectangle(-1600, 90, 1200, 600)));

    [Fact]
    public void ExternalTallImageKeepsReaderFallback() =>
        Assert.True(LongImagePinLayout.NeedsReader(800, 5000, null));

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void ViewportPreservesPhysicalBoundsAtEveryDpi(double dpi)
    {
        var bounds = new Rectangle(-1800, -400, 1002, 602);
        var viewport = LongImagePinLayout.Viewport(bounds, dpi, dpi);
        Assert.Equal(bounds.Width, (viewport.Width + 2) * dpi, 6);
        Assert.Equal(bounds.Height, (viewport.Height + 2) * dpi, 6);
    }
}
