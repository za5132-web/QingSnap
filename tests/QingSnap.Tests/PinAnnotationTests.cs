using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class PinAnnotationTests
{
    [Fact]
    public void ActualToolbarBuildsWithDarkResourcesAndFitsCompactBounds() => Run(false, window =>
    {
        Call(window, "BeginPinAnnotations");
        Get<Popup>(window, "_pinAnnotationToolbar").IsOpen = false;
        Set(window, "_pinAnnotationToolbar", null);
        Call(window, "EnsurePinAnnotationToolbar");
        var border = Assert.IsType<Border>(Get<Popup>(window, "_pinAnnotationToolbar").Child);
        border.Measure(new Size(1000, 1000));
        border.Arrange(new Rect(new Point(), border.DesiredSize));
        Assert.InRange(border.ActualWidth, 420, 450);
        Assert.InRange(border.ActualHeight, 70, 200);
        var image = new RenderTargetBitmap((int)Math.Ceiling(border.ActualWidth),
            (int)Math.Ceiling(border.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(border);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = System.IO.File.Create(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "QingSnap-pin-annotation-toolbar-preview.png"));
        encoder.Save(stream);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnnotationExportUsesWholeImageAndSurvivesUndoRedoAndViewSwitch(bool longImage) => Run(longImage, window =>
    {
        var before = window.CreatePinOutput();
        Call(window, "BeginPinAnnotations");
        var controller = Get<CaptureAnnotationController>(window, "_pinAnnotations");
        controller.ActiveTool = CaptureAnnotationTool.Rectangle;
        var top = longImage ? 1200 : 100;
        controller.Begin(new Point(100, top));
        controller.End(new Point(300, top + 100));
        var annotated = window.CreatePinOutput();
        Assert.Equal(800, annotated.PixelWidth);
        Assert.Equal(longImage ? 1600 : 600, annotated.PixelHeight);
        Assert.NotEqual(Pixel(before, 200, top), Pixel(annotated, 200, top));
        controller.Undo();
        Assert.Equal(Pixel(before, 200, top), Pixel(window.CreatePinOutput(), 200, top));
        controller.Redo();
        Call(window, "FinishPinAnnotations");
        if (longImage)
        {
            Call(window, "EnterOverviewMode");
            Assert.NotNull(((Viewbox)window.FindName("PinAnnotationHost")).Child);
            Call(window, "EnterReaderMode", false);
            Assert.NotNull(((Viewbox)window.FindName("LongPinAnnotationHost")).Child);
        }
        Call(window, "BeginPinAnnotations");
        Assert.True(controller.HasAnnotations);
        Assert.Equal(Pixel(annotated, 200, top), Pixel(window.CreatePinOutput(), 200, top));
        controller.Clear();
        controller.Undo();
        Assert.True(controller.HasAnnotations);
    });

    [Fact]
    public void FirstTextIsCommittedBeforeExport() => Run(false, window =>
    {
        Call(window, "BeginPinAnnotations");
        var controller = Get<CaptureAnnotationController>(window, "_pinAnnotations");
        controller.ActiveTool = CaptureAnnotationTool.Text;
        controller.Begin(new Point(30, 40));
        var layer = Get<Canvas>(window, "_pinAnnotationLayer");
        var editor = Assert.Single(layer.Children.OfType<TextBox>());
        editor.Text = "轻截 标注测试";
        window.CreatePinOutput();
        Assert.True(controller.HasAnnotations);
        Assert.Empty(layer.Children.OfType<TextBox>());
    });

    [Fact]
    public void EditingInvalidatesOldOcrAndThumbnailRemainsSmall() => Run(true, window =>
    {
        Call(window, "BeginPinAnnotations");
        var controller = Get<CaptureAnnotationController>(window, "_pinAnnotations");
        controller.ActiveTool = CaptureAnnotationTool.Mosaic;
        controller.Begin(new Point(100, 100));
        controller.End(new Point(200, 200));
        Assert.Null(Get<object?>(window, "_ocrResult"));
        Assert.Null(Get<object?>(window, "_ocrPreloadTask"));
        Call(window, "FinishPinAnnotations");
        var thumbnail = window.CreatePinOutput(thumbnail: true);
        Assert.InRange(thumbnail.PixelWidth, 1, 180);
        Assert.InRange(thumbnail.PixelHeight, 1, 130);
        Assert.False(((Viewbox)window.FindName("LongPinAnnotationHost")).IsHitTestVisible);
    });

    [Fact]
    public void StaleOcrCannotReappearAfterEditing() => Run(false, window =>
    {
        var pending = new TaskCompletionSource<OcrRecognitionResult>();
        Set(window, "_ocrPreloadTask", pending.Task);
        var observation = (Task)Invoke(window, "ObserveOcrPreloadAsync", pending.Task)!;
        Call(window, "BeginPinAnnotations");
        pending.SetResult(new OcrRecognitionResult("secret", "test", "test", 0, 800, 600, 800, 600, []));
        // Allow the captured WPF continuation to execute without real OCR or clipboard access.
        Pump();
        Assert.True(observation.IsCompleted);
        Assert.Null(Get<object?>(window, "_ocrResult"));
    });

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
                    "QingSnap-pin-annotation-tests", Guid.NewGuid().ToString("N")));
                using var clipboard = new ClipboardService();
                using var ocr = new OcrService(settings);
                var height = longImage ? 1600 : 600;
                var pixels = new byte[800 * height * 4];
                Array.Fill(pixels, (byte)255);
                var image = BitmapSource.Create(800, height, 96, 96, PixelFormats.Bgra32, null, pixels, 800 * 4);
                window = new StickyImageWindow(image, "synthetic", clipboard, ocr, new AppSettings(),
                    new System.Drawing.Rectangle(-900, 100, 800, 600));
                new WindowInteropHelper(window).EnsureHandle();
                Call(window, "OnLoaded", window, new RoutedEventArgs());
                // Tests exercise editing without opening a real floating toolbar.
                Set(window, "_pinAnnotationToolbar", new Popup());
                action(window);
            }
            catch (Exception exception) { failure = exception; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static uint Pixel(BitmapSource image, int x, int y)
    {
        var bytes = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return BitConverter.ToUInt32(bytes);
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] args) => target.GetType()
        .GetMethods(Flags | BindingFlags.DeclaredOnly).Single(method => method.Name == name && method.GetParameters().Length == args.Length)
        .Invoke(target, args);
    private static void Call(object target, string name, params object[] args) => Invoke(target, name, args);
}
