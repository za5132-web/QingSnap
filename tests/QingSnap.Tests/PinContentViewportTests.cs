using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
public sealed class PinContentViewportTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void BothAxisAnchorsPreserveImagePointAcrossDpi(double dpi)
    {
        var cursor = 200 / dpi;
        var viewport = 600 / dpi;
        var size = 1200D;
        var scale = 1 / dpi;
        var offset = 300 / dpi;
        var point = PinContentViewportMath.ImageCoordinate(cursor, offset, size * scale, viewport, scale);
        var next = PinContentViewportMath.AnchoredOffset(point, scale * 1.7, cursor, viewport, size);
        var restored = PinContentViewportMath.ImageCoordinate(cursor, next, size * scale * 1.7, viewport, scale * 1.7);
        Assert.Equal(point, restored, 8);
    }

    [Theory]
    [InlineData(800, 600, 400, 300, 0.5)]
    [InlineData(800, 600, 500, 300, 0.5)]
    [InlineData(800, 16000, 400, 300, 0.01875)]
    [InlineData(16000, 20, 400, 300, 0.025)]
    public void FitAndCenteredPaddingNeverDistortOrLoseThinImages(double w, double h, double vw, double vh, double expected)
    {
        var scale = PinContentViewportMath.Fit(w, h, vw, vh);
        Assert.Equal(expected, scale, 8);
        Assert.InRange(w * scale, 0, vw);
        Assert.InRange(h * scale, 0, vh);
        var center = PinContentViewportMath.ImageCoordinate(vw / 2, 0, w * scale, vw, scale);
        Assert.Equal(w / 2, center, 7);
        Assert.Equal(0, PinContentViewportMath.AnchoredOffset(-100, scale, 100, vw, w));
    }

    [Fact]
    public void OrdinaryReaderKeepsNativeBoundsAndBitmapAndRestoresNormalWheel() => Run(false, window =>
    {
        var handle = new WindowInteropHelper(window).Handle;
        NativeMethods.GetWindowRect(handle, out var before);
        var original = window.CreatePinOutput();
        Call(window, "EnterFixedViewport");
        Assert.True(Get<bool>(window, "_isLongReaderMode"));
        Assert.True(((MenuItem)window.FindName("FixedViewportMenuItem")).IsChecked);
        Call(window, "SetReaderScale", 2D, false);
        Call(window, "CompletePinMotion", true);
        NativeMethods.GetWindowRect(handle, out var after);
        AssertBounds(before, after);
        Assert.Same(original, window.CreatePinOutput());
        Assert.Null(Get<object?>(window, "_zoomPreview"));
        Assert.Equal(Transform.Identity.Value, ((FrameworkElement)window.FindName("LongImageSurface")).RenderTransform.Value);
        Call(window, "ExitFixedViewport");
        Assert.False(Get<bool>(window, "_isLongReaderMode"));
        Assert.False(((MenuItem)window.FindName("FixedViewportMenuItem")).IsChecked);
        Call(window, "QueuePinZoom", 120);
        Call(window, "CompletePinMotion", true);
        Assert.True(Get<double>(window, "_scale") > 1);
    });

    [Fact]
    public void RepeatedContentZoomKeepsBothOffsetsAndDetailToggle() => Run(false, window =>
    {
        Call(window, "EnterFixedViewport");
        Call(window, "SetReaderScale", 2D, false);
        Call(window, "CompletePinMotion", true);
        var scroller = (ScrollViewer)window.FindName("LongImageScroller");
        scroller.ScrollToHorizontalOffset(170);
        scroller.ScrollToVerticalOffset(140);
        window.UpdateLayout();
        var x = scroller.HorizontalOffset;
        var y = scroller.VerticalOffset;
        Call(window, "ToggleContentDetail");
        Call(window, "CompletePinMotion", true);
        Assert.InRange(scroller.ScrollableWidth, 0, 1);
        Assert.InRange(scroller.ScrollableHeight, 0, 1);
        Call(window, "ToggleContentDetail");
        window.UpdateLayout();
        Assert.Equal(2D, Get<double>(window, "_readerScale"), 7);
        Assert.Equal(x, scroller.HorizontalOffset, 5);
        Assert.Equal(y, scroller.VerticalOffset, 5);
        for (var i = 0; i < 20; i++) Call(window, "SetReaderScale", i % 2 == 0 ? 3D : 2D, false);
        Call(window, "CompletePinMotion", true);
        Assert.Equal(2D, Get<double>(window, "_readerScale"), 7);
        Assert.Null(Get<object?>(window, "_pinMotionFrame"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ViewportAnnotationExportAndUndoRemainInImagePixels(bool longImage) => Run(longImage, window =>
    {
        if (!longImage) Call(window, "EnterFixedViewport");
        Call(window, "SetReaderScale", 2D, false);
        Call(window, "CompletePinMotion", true);
        Set(window, "_pinAnnotationToolbar", new Popup());
        Call(window, "BeginPinAnnotations");
        var controller = Get<CaptureAnnotationController>(window, "_pinAnnotations");
        controller.ActiveTool = CaptureAnnotationTool.Rectangle;
        controller.Begin(new Point(100, 100));
        controller.End(new Point(250, 200));
        var output = window.CreatePinOutput();
        Assert.Equal(800, output.PixelWidth);
        Assert.Equal(longImage ? 1600 : 600, output.PixelHeight);
        Assert.Same(((Viewbox)window.FindName("LongPinAnnotationHost")).Child,
            Get<Canvas>(window, "_pinAnnotationLayer"));
        controller.Undo();
        Assert.False(controller.HasAnnotations);
        controller.Redo();
        Assert.True(controller.HasAnnotations);
        Call(window, "FinishPinAnnotations");
        Assert.True(Get<bool>(window, "_isLongReaderMode"));
    });

    [Fact]
    public void LongReaderKeepsExistingOverviewAndFullFitDoesNotResizeWindow() => Run(true, window =>
    {
        var handle = new WindowInteropHelper(window).Handle;
        NativeMethods.GetWindowRect(handle, out var before);
        Call(window, "OnContentFitClick", window, new RoutedEventArgs());
        Call(window, "CompletePinMotion", true);
        NativeMethods.GetWindowRect(handle, out var after);
        AssertBounds(before, after);
        Assert.InRange(((ScrollViewer)window.FindName("LongImageScroller")).ScrollableHeight, 0, 1);
        Call(window, "EnterOverviewMode");
        Assert.False(Get<bool>(window, "_isLongReaderMode"));
        Call(window, "EnterFixedViewport");
        Assert.True(Get<bool>(window, "_isLongReaderMode"));
    });

    [Fact]
    public void OneToOneContentCanBeSmallerThanViewportAndRemainCentered() => Run(false, window =>
    {
        Call(window, "SetScaleImmediate", 2D, true);
        Call(window, "EnterFixedViewport");
        Call(window, "OnContentActualClick", window, new RoutedEventArgs());
        Call(window, "CompletePinMotion", true);
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var image = (Image)window.FindName("LongPinnedImage");
        Assert.Equal(800D, image.Width * dpi.DpiScaleX, 5);
        Assert.Equal(600D, image.Height * dpi.DpiScaleY, 5);
        var scroller = (ScrollViewer)window.FindName("LongImageScroller");
        var center = image.TranslatePoint(new Point(image.ActualWidth / 2, image.ActualHeight / 2), scroller);
        Assert.Equal(scroller.ViewportWidth / 2, center.X, 4);
        Assert.Equal(scroller.ViewportHeight / 2, center.Y, 4);
    });

    [Fact]
    public void AnimatedFramesTransformAllLayersWithoutResizingNativeWindow() => Run(false, window =>
    {
        Call(window, "EnterFixedViewport");
        var handle = new WindowInteropHelper(window).Handle;
        NativeMethods.GetWindowRect(handle, out var before);
        var surface = (FrameworkElement)window.FindName("LongImageSurface");
        var originalWidth = surface.Width;
        var image = (Image)window.FindName("LongPinnedImage");
        var source = image.Source;
        Call(window, "SetReaderScale", 3D, false);
        if (Get<object?>(window, "_pinMotionFrame") is Action<double> frame)
        {
            foreach (var progress in new[] { 0.1, 0.3, 0.6, 0.8 })
            {
                frame(progress);
                Assert.Equal(originalWidth, surface.Width);
                Assert.Same(source, image.Source);
                Assert.False(surface.RenderTransform.Value.IsIdentity);
                NativeMethods.GetWindowRect(handle, out var current);
                AssertBounds(before, current);
            }
        }
        Call(window, "CompletePinMotion", true);
        window.UpdateLayout();
        var scroller = (ScrollViewer)window.FindName("LongImageScroller");
        var center = image.TranslatePoint(new Point(image.ActualWidth / 2, image.ActualHeight / 2), scroller);
        Assert.Equal(scroller.ViewportWidth / 2, center.X, 4);
        Assert.Equal(scroller.ViewportHeight / 2, center.Y, 4);
        Assert.True(surface.RenderTransform.Value.IsIdentity);
    });

    [Fact]
    public void OrdinaryDockRestoresReadingZoomAndBothOffsets() => Run(false, window =>
    {
        Call(window, "EnterFixedViewport");
        Call(window, "SetReaderScale", 2D, false);
        Call(window, "CompletePinMotion", true);
        var scroller = (ScrollViewer)window.FindName("LongImageScroller");
        scroller.ScrollToHorizontalOffset(170);
        scroller.ScrollToVerticalOffset(140);
        window.UpdateLayout();
        var xRatio = scroller.HorizontalOffset / scroller.ScrollableWidth;
        var yRatio = scroller.VerticalOffset / scroller.ScrollableHeight;
        var handle = new WindowInteropHelper(window).Handle;
        NativeMethods.GetWindowRect(handle, out var before);
        Call(window, "CollapseToDock");
        Call(window, "CompletePinMotion", true);
        Call(window, "RestoreFromDock");
        Call(window, "CompletePinMotion", true);
        window.UpdateLayout();
        NativeMethods.GetWindowRect(handle, out var after);
        AssertBounds(before, after);
        Assert.True(Get<bool>(window, "_isLongReaderMode"));
        Assert.Equal(2D, Get<double>(window, "_readerScale"), 7);
        Assert.Equal(xRatio, scroller.HorizontalOffset / scroller.ScrollableWidth, 4);
        Assert.Equal(yRatio, scroller.VerticalOffset / scroller.ScrollableHeight, 4);
    });

    [Fact]
    public void ClosingDuringZoomReleasesMotionWithoutReplacingBitmap() => Run(false, window =>
    {
        Call(window, "EnterFixedViewport");
        Call(window, "SetReaderScale", 3D, false);
        window.Close();
        Assert.Null(Get<object?>(window, "_pinMotionFrame"));
        Assert.False(Get<bool>(window, "_isContentPanning"));
        Assert.Null(Get<object?>(window, "_zoomPreview"));
    });

    private static void AssertBounds(NativeMethods.NativeRectangle a, NativeMethods.NativeRectangle b)
    {
        Assert.Equal(a.Left, b.Left); Assert.Equal(a.Top, b.Top);
        Assert.Equal(a.Width, b.Width); Assert.Equal(a.Height, b.Height);
    }

    private static void Run(bool longImage, Action<StickyImageWindow> action)
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
                    "QingSnap-content-tests", Guid.NewGuid().ToString("N")));
                using var clipboard = new ClipboardService();
                using var ocr = new OcrService(settings);
                var height = longImage ? 1600 : 600;
                var image = BitmapSource.Create(800, height, 96, 96, PixelFormats.Bgra32,
                    null, new byte[800 * height * 4], 800 * 4);
                window = new StickyImageWindow(image, "synthetic-viewport", clipboard, ocr, new AppSettings(),
                    new System.Drawing.Rectangle(-900, 100, 800, 600));
                new WindowInteropHelper(window).EnsureHandle();
                Call(window, "OnLoaded", window, new RoutedEventArgs());
                window.Measure(new Size(window.Width, window.Height));
                window.Arrange(new Rect(0, 0, window.Width, window.Height));
                window.UpdateLayout();
                action(window);
            }
            catch (Exception exception) { failure = exception; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Viewport test exceeded 30 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static void Call(object target, string name, params object[] args) => target.GetType()
        .GetMethods(Flags | BindingFlags.DeclaredOnly).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args);
}
