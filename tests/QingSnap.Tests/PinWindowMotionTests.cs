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
public sealed class PinWindowMotionTests
{
    [Fact]
    public void RetargetedZoomPreservesAllWheelInputsAnchorAndStopsOnClose()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            StickyImageWindow? window = null;
            try
            {
                if (Application.Current is null)
                {
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    application.Resources["UiFont"] = new FontFamily("Segoe UI");
                    application.Resources["DataFont"] = new FontFamily("Consolas");
                }
                var settings = new AppSettingsService(System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "QingSnap-motion-test", Guid.NewGuid().ToString("N")));
                using var clipboard = new ClipboardService();
                using var ocr = new OcrService(settings);
                var image = BitmapSource.Create(800, 500, 96, 96, PixelFormats.Bgra32,
                    null, new byte[800 * 500 * 4], 800 * 4);
                window = new StickyImageWindow(image, "synthetic-motion", clipboard, ocr,
                    new AppSettings(), new System.Drawing.Rectangle(-700, 100, 800, 500));
                var handle = new WindowInteropHelper(window).EnsureHandle();
                Call(window, "OnLoaded", window, new RoutedEventArgs());
                Assert.True(NativeMethods.GetWindowRect(handle, out var before));
                var scale = Get<double>(window, "_scale");
                Call(window, "QueuePinZoom", 120);
                Call(window, "QueuePinZoom", 120);
                Assert.Equal(scale * 1.21, Get<double>(window, "_zoomTargetScale"), 6);
                Call(window, "CompletePinMotion", true);
                Assert.True(NativeMethods.GetWindowRect(handle, out var after));
                Assert.True(after.Width > before.Width);
                Assert.InRange(Math.Abs(after.Left + after.Width / 2D -
                                        (before.Left + before.Width / 2D)), 0, 0.5);
                Assert.InRange(Math.Abs(after.Top + after.Height / 2D -
                                        (before.Top + before.Height / 2D)), 0, 0.5);
                Assert.Null(Get<object?>(window, "_pinMotionFrame"));
                Assert.Equal("None", Get<object>(window, "_pinMotionKind").ToString());
                var centerX = Get<double>(window, "_zoomCenterX");
                var centerY = Get<double>(window, "_zoomCenterY");
                // Isolated notches after a completed motion must keep the same
                // subpixel anchor, not recapture rounded HWND parity each time.
                for (var index = 0; index < 8; index++)
                {
                    Call(window, "QueuePinZoom", index < 4 ? 120 : -120);
                    Call(window, "CompletePinMotion", true);
                    Assert.Equal(centerX, Get<double>(window, "_zoomCenterX"));
                    Assert.Equal(centerY, Get<double>(window, "_zoomCenterY"));
                }
                // Reproduce independent native sizing (the same stale-DIP-scale
                // condition as screenshot placement on a higher-DPI monitor).
                NativeMethods.SetWindowPos(handle, nint.Zero, before.Left, before.Top, 402, 252,
                    NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoOwnerZOrder);
                Assert.True(NativeMethods.GetWindowRect(handle, out var forced));
                var staleScale = Get<double>(window, "_scale");
                Call(window, "QueuePinZoom", 120);
                Call(window, "CompletePinMotion", true);
                Assert.True(NativeMethods.GetWindowRect(handle, out var firstWheel));
                Assert.InRange(Math.Abs(firstWheel.Width - forced.Width * 1.1), 0, 4);
                Assert.InRange(Math.Abs(firstWheel.Height - forced.Height * 1.1), 0, 4);
                Assert.True(Get<double>(window, "_scale") < staleScale);
                Assert.InRange(Math.Abs(firstWheel.Left + firstWheel.Width / 2D -
                                        (forced.Left + forced.Width / 2D)), 0, 0.5);
                // Explicit size commands interrupt, rather than queue behind, wheel zoom.
                Call(window, "QueuePinZoom", 120);
                Call(window, "OnFitClick", window, new RoutedEventArgs());
                Assert.Null(Get<object?>(window, "_pinMotionFrame"));
                Call(window, "QueuePinZoom", 120);
                Call(window, "OnActualSizeClick", window, new RoutedEventArgs());
                Assert.Null(Get<object?>(window, "_pinMotionFrame"));

                window.Show();
                window.Opacity = 0;
                Call(window, "ToggleCollapsed");
                // Rapid M/restore during a transition is ignored, not another queued motion.
                if (Get<bool>(window, "_isDockTransitioning"))
                {
                    Call(window, "ToggleCollapsed");
                    Call(window, "RestoreFromDock");
                }
                Call(window, "CompletePinMotion", true);
                Assert.True(Get<bool>(window, "_isCollapsed"));
                Assert.False(Get<bool>(window, "_isDockTransitioning"));

                // The immediate/reduced-motion completion path must always release
                // transition state, even when a final frame throws.
                var completed = false;
                Set(window, "_isDockTransitioning", true);
                Call(window, "FinishPinMotionSafely",
                    (Action<double>)(_ => throw new InvalidOperationException("synthetic final-frame failure")),
                    (Action)(() => completed = true), true);
                Assert.True(completed);
                Assert.False(Get<bool>(window, "_isDockTransitioning"));
                Call(window, "RestoreFromDock");
                Call(window, "CompletePinMotion", true);
                Assert.False(Get<bool>(window, "_isCollapsed"));
                Assert.False(Get<bool>(window, "_isDockTransitioning"));

                // Beginning an annotation session finishes pending zoom before hit testing.
                Call(window, "QueuePinZoom", 120);
                Call(window, "BeginPinAnnotations");
                Assert.Null(Get<object?>(window, "_pinMotionFrame"));
                Call(window, "FinishPinAnnotations");
                Call(window, "QueuePinZoom", -120);
                window.Close();
                Assert.Null(Get<object?>(window, "_pinMotionFrame"));
                Assert.Null(Get<object?>(window, "_pinMotionCompleted"));
                Assert.Equal("None", Get<object>(window, "_pinMotionKind").ToString());
            }
            catch (Exception exception) { failure = exception; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static void Call(object target, string name, params object[] arguments) =>
        target.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length)
            .Invoke(target, arguments);
}
