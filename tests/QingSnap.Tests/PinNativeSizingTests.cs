using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;
using Xunit;
using Xunit.Abstractions;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class PinNativeSizingTests(ITestOutputHelper output)
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public nint Window;
        public nint InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    [Fact]
    public void PinNativeBoundsMayExceedDefaultDesktopTrackSize() => Run(window =>
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var budget = Get<PinSurfaceBudget>(window, "_pinSurfaceBudget");
        var requestedWidth = Math.Min(budget.MaximumWidth, GetSystemMetrics(59) + 384);
        var requestedHeight = Math.Min(budget.MaximumHeight, GetSystemMetrics(60) + 256);
        Assert.True(NativeMethods.SetWindowPos(handle, 0, -200, -100, requestedWidth, requestedHeight,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoOwnerZOrder));
        Assert.True(NativeMethods.GetWindowRect(handle, out var actual));
        Assert.Equal(requestedWidth, actual.Width);
        Assert.Equal(requestedHeight, actual.Height);
        Assert.Equal(-200, actual.Left);
        Assert.Equal(-100, actual.Top);
        output.WriteLine("Requested {0}x{1}; actual {2}x{3}; desktop track {4}x{5}; budget {6}x{7}",
            requestedWidth, requestedHeight, actual.Width, actual.Height,
            GetSystemMetrics(59), GetSystemMetrics(60), budget.MaximumWidth, budget.MaximumHeight);
        foreach (var (width, height) in new[] { (24, 24), (58, 44) })
        {
            Assert.True(NativeMethods.SetWindowPos(handle, 0, -200, -100, width, height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoOwnerZOrder));
            Assert.True(NativeMethods.GetWindowRect(handle, out var tiny));
            Assert.Equal(width, tiny.Width);
            Assert.Equal(height, tiny.Height);
        }
    });

    [Fact]
    public void ThirtySecondsOfMaximumWheelInputDoesNotResizeOrRestartAnimation() => Run(window =>
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        Call(window, "OnLoaded", window, new RoutedEventArgs());
        // Exercise the actual WPF render/Dispatcher path without covering the user's desktop.
        typeof(StickyImageWindow).GetField("_isInitiallyCloaked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, false);
        NativeMethods.SetWindowCloaked(handle, true);
        window.Show();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Call(window, "SetScaleImmediate", 3.9D, true);
        Call(window, "QueuePinZoom", 120);
        var motionVersion = Get<int>(window, "_pinMotionVersion");
        Call(window, "QueuePinZoom", 120);
        Assert.Equal(motionVersion, Get<int>(window, "_pinMotionVersion"));
        Call(window, "CompletePinMotion", true);
        Call(window, "SetScaleImmediate", 4D, true);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.True(NativeMethods.GetWindowRect(handle, out var maximum));
        var dpi = VisualTreeHelper.GetDpi(window);
        var contentFrame = (FrameworkElement)window.FindName("Frame");
        Assert.InRange(Math.Abs(contentFrame.ActualWidth * dpi.DpiScaleX - maximum.Width), 0, 2);
        Assert.InRange(Math.Abs(contentFrame.ActualHeight * dpi.DpiScaleY - maximum.Height), 0, 2);
        var budget = Get<PinSurfaceBudget>(window, "_pinSurfaceBudget");
        Assert.True((long)maximum.Width * maximum.Height <= budget.MaximumPixels);
        if (budget.MaximumWidth > GetSystemMetrics(59) && budget.MaximumHeight > GetSystemMetrics(60))
        {
            Assert.True(maximum.Width > GetSystemMetrics(59));
            Assert.True(maximum.Height > GetSystemMetrics(60));
        }
        var nativeResizes = 0;
        nint CountPositionChanges(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            // Activation/z-order/show notifications also use WINDOWPOSCHANGED;
            // only an actual size request is relevant to this regression.
            if (message == 0x0047 && lParam != nint.Zero &&
                (Marshal.PtrToStructure<WindowPosition>(lParam).Flags & 0x0001) == 0) nativeResizes++;
            return 0;
        }
        var source = HwndSource.FromHwnd(handle)!;
        source.AddHook(CountPositionChanges);
        var before = ResourceDiagnostics.Capture("PinZoomMax30_Start");
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        var ticks = 0;
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) =>
        {
            try
            {
                Call(window, "QueuePinZoom", 120);
                ticks++;
                Assert.Null(Get<object?>(window, "_pinMotionFrame"));
                Assert.True(NativeMethods.GetWindowRect(handle, out var current));
                Assert.Equal(maximum.Width, current.Width);
                Assert.Equal(maximum.Height, current.Height);
                Assert.Equal(maximum.Left, current.Left);
                Assert.Equal(maximum.Top, current.Top);
                if (watch.Elapsed < TimeSpan.FromSeconds(30)) return;
            }
            catch (Exception exception) { failure = exception; }
            timer.Stop();
            frame.Continue = false;
        };
        try
        {
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        finally { timer.Stop(); source.RemoveHook(CountPositionChanges); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(30));
        Assert.True(ticks > 100);
        Assert.Equal(0, nativeResizes);
        var after = ResourceDiagnostics.Capture("PinZoomMax30_End");
        output.WriteLine("{0:0.00}s; {1} maximum-wheel inputs; HWND={2}x{3}; geometry messages={4}; " +
                         "Private delta={5:0.0}MiB; handles={6:+#;-#;0}; GDI={7:+#;-#;0}; USER={8:+#;-#;0}",
            watch.Elapsed.TotalSeconds, ticks, maximum.Width, maximum.Height, nativeResizes,
            (after.PrivateMemoryBytes - before.PrivateMemoryBytes) / (1024D * 1024D),
            after.HandleCount - before.HandleCount, after.GdiObjectCount - before.GdiObjectCount,
            after.UserObjectCount - before.UserObjectCount);

        // Zoom back down, stash/restore, and check that the native hook is removed on close.
        Call(window, "QueuePinZoom", -120);
        Call(window, "CompletePinMotion", true);
        Assert.True(NativeMethods.GetWindowRect(handle, out var smaller));
        Assert.True(smaller.Width < maximum.Width);
        Call(window, "ToggleCollapsed");
        Call(window, "CompletePinMotion", true);
        Call(window, "RestoreFromDock");
        Call(window, "CompletePinMotion", true);
        Assert.True(NativeMethods.GetWindowRect(handle, out var restored));
        Assert.Equal(smaller.Width, restored.Width);
        Assert.Equal(smaller.Height, restored.Height);
        window.Close();
        Assert.Null(Get<object?>(window, "_pinSizingSource"));
    });

    [Theory]
    [InlineData(1200, 801)]
    [InlineData(1300, 121)]
    public void ZoomImageCenterDoesNotOscillateAcrossOddAndEvenCarrierSizes(int width, int height) => Run(window =>
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        Call(window, "OnLoaded", window, new RoutedEventArgs());
        typeof(StickyImageWindow).GetField("_isInitiallyCloaked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, false);
        NativeMethods.SetWindowCloaked(handle, true);
        window.Show();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        const double centerX = -300.5;
        const double centerY = 400.5;
        var image = (FrameworkElement)window.FindName("PinnedImage");
        var previousX = centerX;
        var previousY = centerY;
        var maximumSwing = 0D;
        for (var index = 0; index < 100; index++)
        {
            Call(window, "ApplyScaleAtAnchor", 1 + index * 0.00201, centerX, centerY, true);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(NativeMethods.GetWindowRect(handle, out var bounds));
            var dpi = VisualTreeHelper.GetDpi(window);
            var requestedScale = 1 + index * 0.00201;
            // Test what Image actually draws, not just the center of its carrier.
            // Independent rounded W/H + Uniform used to create plateaus/jumps.
            var render = image.RenderSize;
            var drawScale = Math.Min(render.Width / width, render.Height / height);
            Assert.InRange(Math.Abs(drawScale - requestedScale), 0, 0.000001);
            var local = image.TransformToAncestor(window).Transform(new Point(image.ActualWidth / 2, image.ActualHeight / 2));
            var actualX = bounds.Left + local.X * dpi.DpiScaleX;
            var actualY = bounds.Top + local.Y * dpi.DpiScaleY;
            maximumSwing = Math.Max(maximumSwing, Math.Max(Math.Abs(actualX - previousX), Math.Abs(actualY - previousY)));
            Assert.InRange(Math.Abs(actualX - centerX), 0, 0.000001);
            Assert.InRange(Math.Abs(actualY - centerY), 0, 0.000001);
            previousX = actualX;
            previousY = actualY;
        }
        output.WriteLine("{0}x{1}: 100 monotonic sizes; maximum image-center swing={2:0.000000}px", width, height, maximumSwing);

        // A manual move must move the image too, not keep an old absolute anchor.
        Assert.True(NativeMethods.GetWindowRect(handle, out var beforeDrag));
        NativeMethods.SetWindowPos(handle, 0, beforeDrag.Left + 80, beforeDrag.Top + 40,
            beforeDrag.Width, beforeDrag.Height, NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder);
        Call(window, "QueuePinZoom", 120);
        Assert.InRange(Math.Abs(Get<double>(window, "_zoomCenterX") - (centerX + 80)), 0, 0.000001);
        Assert.InRange(Math.Abs(Get<double>(window, "_zoomCenterY") - (centerY + 40)), 0, 0.000001);
        Call(window, "CompletePinMotion", true);
        Call(window, "ToggleCollapsed");
        Call(window, "CompletePinMotion", true);
        Assert.Null(Get<object?>(window, "_zoomVisualBounds"));
        Assert.Equal(0, ((TranslateTransform)window.FindName("NormalPinZoomOffset")).X);
        Call(window, "RestoreFromDock");
        Call(window, "CompletePinMotion", true);
        Assert.False(Get<bool>(window, "_isCollapsed"));
    }, width, height);

    private static void Run(Action<StickyImageWindow> assertion, int width = 1200, int height = 800)
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
                var settings = new AppSettingsService(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "QingSnap-native-sizing-test", Guid.NewGuid().ToString("N")));
                using var clipboard = new ClipboardService();
                using var ocr = new OcrService(settings);
                var pixels = new byte[width * height * 4];
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    pixels[index] = 220; pixels[index + 1] = 245;
                    pixels[index + 2] = 250; pixels[index + 3] = 255;
                }
                var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                window = new StickyImageWindow(image, "synthetic-native-sizing", clipboard, ocr, new AppSettings(),
                    new System.Drawing.Rectangle(100, 100, width, height));
                assertion(window);
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
    private static void Call(object target, string name, params object[] arguments) => target.GetType()
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length)
        .Invoke(target, arguments);
}
