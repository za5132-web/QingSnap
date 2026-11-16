using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using QingSnap.App.Models;
using QingSnap.App.Views;
using Xunit;
using Point = System.Windows.Point;
using Rectangle = System.Drawing.Rectangle;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class CaptureAnnotationCropResizeTests
{
    [Theory]
    [InlineData((int)CaptureAnnotationTool.Pen)]
    [InlineData((int)CaptureAnnotationTool.Line)]
    [InlineData((int)CaptureAnnotationTool.Arrow)]
    [InlineData((int)CaptureAnnotationTool.DoubleArrow)]
    [InlineData((int)CaptureAnnotationTool.Rectangle)]
    [InlineData((int)CaptureAnnotationTool.Ellipse)]
    [InlineData((int)CaptureAnnotationTool.Text)]
    [InlineData((int)CaptureAnnotationTool.Number)]
    [InlineData((int)CaptureAnnotationTool.Mosaic)]
    [InlineData((int)CaptureAnnotationTool.Blur)]
    [InlineData((int)CaptureAnnotationTool.Highlight)]
    public void AllAnnotationKindsStayOnSourceContentDuringCropResize(int toolValue) => Run(() =>
    {
        var tool = (CaptureAnnotationTool)toolValue;
        var (controller, layer) = CreateController();
        Draw(controller, layer, tool, new Point(180, 170), new Point(240, 220));
        var element = layer.Children.OfType<FrameworkElement>().Single();
        var original = Anchor(layer, element);
        foreach (var crop in new[] { new Rect(70, 60, 450, 360), new Rect(150, 140, 200, 160), new Rect(100, 100, 400, 300) })
        {
            controller.SetBounds(crop, 800, 600);
            AssertPoint(original, Anchor(layer, element));
        }
    });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    public void ResizeAndUndoRedoPreservePhysicalAnchors(double scale) => Run(() =>
    {
        var (controller, layer) = CreateController();
        controller.SetBounds(new Rect(100 / scale, 100 / scale, 400 / scale, 300 / scale), 800 / scale, 600 / scale);
        Draw(controller, layer, CaptureAnnotationTool.Rectangle, new Point(180 / scale, 170 / scale), new Point(240 / scale, 220 / scale));
        var original = Anchor(layer, layer.Children.OfType<FrameworkElement>().Single());
        Draw(controller, layer, CaptureAnnotationTool.Ellipse, new Point(300 / scale, 250 / scale), new Point(340 / scale, 280 / scale));
        controller.SetBounds(new Rect(50 / scale, 40 / scale, 480 / scale, 390 / scale), 800 / scale, 600 / scale);
        controller.Undo();
        AssertPoint(original, Anchor(layer, layer.Children.OfType<FrameworkElement>().Single()));
        controller.Redo();
        AssertPoint(original, Anchor(layer, Annotations(controller).OfType<System.Windows.Shapes.Rectangle>().Single()));
        controller.ActiveTool = CaptureAnnotationTool.Select;
        Assert.True(controller.SelectAt(new Point(200 / scale, 190 / scale)));
        Assert.True(controller.NudgeSelection(10 / scale, 0));
        controller.Undo();
        AssertPoint(original, Anchor(layer, Annotations(controller).OfType<System.Windows.Shapes.Rectangle>().Single()));
        controller.Redo();
        var moved = Anchor(layer, Annotations(controller).OfType<System.Windows.Shapes.Rectangle>().Single());
        Assert.Equal(original.X * scale + 10, moved.X * scale, 5);
    });

    [Fact]
    public void CopiedGroupRebasesAfterCropChangeAndPastesWithExistingOffset() => Run(() =>
    {
        var (controller, layer) = CreateController();
        Draw(controller, layer, CaptureAnnotationTool.Rectangle, new Point(180, 170), new Point(240, 220));
        Draw(controller, layer, CaptureAnnotationTool.Ellipse, new Point(300, 250), new Point(340, 280));
        controller.ActiveTool = CaptureAnnotationTool.Select;
        Assert.True(controller.SelectAt(new Point(200, 190)));
        controller.Begin(new Point(320, 265), true);
        Assert.Equal(2, controller.SelectionCount);
        var original = Annotations(controller).Where(element => element is System.Windows.Shapes.Rectangle or Ellipse)
            .Take(2).Select(element => Anchor(layer, element)).ToArray();
        controller.CopySelected();
        controller.SetBounds(new Rect(50, 40, 480, 390), 800, 600);
        controller.PasteSelected();
        Assert.Equal(2, controller.SelectionCount);
        var pasted = Annotations(controller).Where(element => element is System.Windows.Shapes.Rectangle or Ellipse)
            .Take(4).Skip(2).Select(element => Anchor(layer, element)).ToArray();
        Assert.Equal(2, pasted.Length);
        for (var index = 0; index < 2; index++) AssertPoint(original[index] + new Vector(12, 12), pasted[index]);
        controller.Undo();
        controller.Redo();
        Assert.Equal(2, controller.SelectionCount);
    });

    [Fact]
    public void MovingUnresizedCropRetainsPreviousMoveTogetherBehavior() => Run(() =>
    {
        var (controller, layer) = CreateController();
        Draw(controller, layer, CaptureAnnotationTool.Rectangle, new Point(180, 170), new Point(240, 220));
        var element = layer.Children.OfType<FrameworkElement>().Single();
        var original = Anchor(layer, element);
        controller.SetBounds(new Rect(130, 140, 400, 300), 800, 600);
        AssertPoint(original + new Vector(30, 40), Anchor(layer, element));
    });

    [Theory]
    [InlineData("Left", 120, 250)]
    [InlineData("Right", 470, 250)]
    [InlineData("Top", 250, 130)]
    [InlineData("Bottom", 250, 370)]
    [InlineData("TopLeft", 120, 130)]
    [InlineData("TopRight", 470, 130)]
    [InlineData("BottomLeft", 120, 370)]
    [InlineData("BottomRight", 470, 370)]
    public void EightCropHandlesKeepAnnotationAndExportAtSourcePixels(string handle, double x, double y) => Run(() =>
    {
        var image = Image();
        var window = new CaptureOverlayWindow(new ScreenSnapshot(new Rectangle(-800, -600, 800, 600), image));
        try
        {
            var root = (Grid)window.FindName("Root");
            root.Measure(new Size(800, 600));
            root.Arrange(new Rect(0, 0, 800, 600));
            Call(window, "SetSelectionFromPixels", new Rectangle(100, 100, 400, 300));
            var controller = Field<CaptureAnnotationController>(window, "_annotationController");
            var layer = (Canvas)window.FindName("AnnotationLayer");
            Draw(controller, layer, CaptureAnnotationTool.Rectangle, new Point(180, 170), new Point(260, 230));
            var element = layer.Children.OfType<FrameworkElement>().Single();
            var original = Anchor(layer, element);
            var before = window.CreateSelectedImage();
            Set(window, "_selectionAtDragStart", Field<Rect>(window, "_selection"));
            Set(window, "_dragMode", Enum.Parse(typeof(CaptureOverlayWindow).GetNestedType("DragMode", BindingFlags.NonPublic)!, handle));
            Call(window, "ApplyDrag", new Point(x, y));
            root.UpdateLayout();
            AssertPoint(original, Anchor(layer, element));
            var crop = Field<Rectangle?>(window, "_selectionPixelRegion")!.Value;
            var expected = new CroppedBitmap(before, new Int32Rect(crop.X - 100, crop.Y - 100, crop.Width, crop.Height));
            Assert.Equal(Pixels(expected), Pixels(window.CreateSelectedImage()));
            Call(window, "SetSelectionFromPixels", new Rectangle(100, 100, 400, 300));
            Assert.Equal(Pixels(before), Pixels(window.CreateSelectedImage()));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData((int)CaptureAnnotationTool.Rectangle)]
    [InlineData((int)CaptureAnnotationTool.Text)]
    [InlineData((int)CaptureAnnotationTool.Mosaic)]
    [InlineData((int)CaptureAnnotationTool.Blur)]
    public void CroppedOutContentReturnsWhenSelectionExpands(int tool) => Run(() =>
    {
        var (controller, layer) = CreateController();
        Draw(controller, layer, (CaptureAnnotationTool)tool, new Point(180, 170), new Point(300, 260));
        var element = Annotations(controller).Single();
        var original = Anchor(layer, element);
        var source = (element as System.Windows.Controls.Image)?.Source;
        controller.SetBounds(new Rect(210, 200, 60, 40), 800, 600);
        AssertPoint(original, Anchor(layer, element));
        controller.SetBounds(new Rect(100, 100, 400, 300), 800, 600);
        AssertPoint(original, Anchor(layer, element));
        Assert.Same(element, Annotations(controller).Single());
        if (source is not null) Assert.Same(source, ((System.Windows.Controls.Image)element).Source);
        controller.Undo();
        Assert.Empty(Annotations(controller));
        controller.SetBounds(new Rect(60, 50, 500, 400), 800, 600);
        controller.Redo();
        AssertPoint(original, Anchor(layer, Annotations(controller).Single()));
    });

    [Fact]
    public void PendingTextCommitsAtOriginalPositionDuringResize() => Run(() =>
    {
        var (controller, layer) = CreateController();
        controller.ActiveTool = CaptureAnnotationTool.Text;
        controller.Begin(new Point(180, 170));
        layer.Children.OfType<TextBox>().Single().Text = "Editing text";
        controller.SetBounds(new Rect(60, 50, 500, 400), 800, 600);
        Assert.False(controller.IsEditingText);
        AssertPoint(new Point(180, 170), Anchor(layer, Annotations(controller).Single()));
        controller.Undo();
        controller.Redo();
        AssertPoint(new Point(180, 170), Anchor(layer, Annotations(controller).Single()));
    });

    [Fact]
    public void ArrowEndpointAndItsUndoRemainEditableAfterCropResize() => Run(() =>
    {
        var (controller, layer) = CreateController();
        Draw(controller, layer, CaptureAnnotationTool.Arrow, new Point(180, 170), new Point(240, 220));
        controller.SetBounds(new Rect(60, 50, 500, 400), 800, 600);
        controller.ActiveTool = CaptureAnnotationTool.Select;
        Assert.True(controller.Begin(new Point(180, 170)));
        controller.End(new Point(190, 175));
        AssertPoint(new Point(190, 175), Anchor(layer, Annotations(controller).Single()));
        controller.Undo();
        AssertPoint(new Point(180, 170), Anchor(layer, Annotations(controller).Single()));
        controller.Redo();
        AssertPoint(new Point(190, 175), Anchor(layer, Annotations(controller).Single()));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedClippedSelectionCanMoveInwardWithoutClampException(bool group) => Run(() =>
    {
        var (controller, layer) = CreateController();
        Draw(controller, layer, CaptureAnnotationTool.Rectangle, new Point(180, 170), new Point(460, 290));
        if (group) Draw(controller, layer, CaptureAnnotationTool.Ellipse, new Point(200, 200), new Point(220, 215));
        controller.SetBounds(new Rect(120, 140, 200, 120), 800, 600);
        controller.ActiveTool = CaptureAnnotationTool.Select;
        Assert.True(controller.SelectAt(new Point(190, 180)));
        if (group) controller.Begin(new Point(210, 207), true);
        var before = Annotations(controller).Select(element => Anchor(layer, element)).ToArray();
        Assert.False(controller.NudgeSelection(5, 0));
        Assert.True(controller.NudgeSelection(-5, 0));
        var after = Annotations(controller).Select(element => Anchor(layer, element)).ToArray();
        for (var index = 0; index < before.Length; index++) AssertPoint(before[index] + new Vector(-5, 0), after[index]);
        Assert.True(controller.Begin(new Point(240, 210)));
        controller.End(new Point(235, 210));
        var dragged = Annotations(controller).Select(element => Anchor(layer, element)).ToArray();
        for (var index = 0; index < before.Length; index++) AssertPoint(before[index] + new Vector(-10, 0), dragged[index]);
    });

    private static (CaptureAnnotationController Controller, Canvas Layer) CreateController()
    {
        var layer = new Canvas { ClipToBounds = true };
        var controller = new CaptureAnnotationController(layer, new ScreenSnapshot(new Rectangle(-800, -600, 800, 600), Image()), new AppSettings());
        controller.SetBounds(new Rect(100, 100, 400, 300), 800, 600);
        return (controller, layer);
    }

    private static void Draw(CaptureAnnotationController controller, Canvas layer, CaptureAnnotationTool tool, Point start, Point end)
    {
        controller.ActiveTool = tool;
        Assert.True(controller.Begin(start));
        if (tool == CaptureAnnotationTool.Text)
        {
            layer.Children.OfType<TextBox>().Single().Text = "Target";
            controller.CommitText();
        }
        else controller.End(end);
        layer.Measure(new Size(layer.Width, layer.Height));
        layer.Arrange(new Rect(0, 0, layer.Width, layer.Height));
        layer.UpdateLayout();
    }

    private static Point Anchor(Canvas layer, FrameworkElement element)
    {
        var point = element switch
        {
            Polyline pen => pen.Points[0],
            Line line => new Point(line.X1, line.Y1),
            Canvas arrow when arrow.Children.OfType<Line>().FirstOrDefault() is { } line => new Point(line.X1, line.Y1),
            _ => new Point()
        };
        return point + new Vector(Value(Canvas.GetLeft(layer)) + Value(Canvas.GetLeft(element)), Value(Canvas.GetTop(layer)) + Value(Canvas.GetTop(element)));
    }
    private static double Value(double value) => double.IsNaN(value) ? 0 : value;
    private static IEnumerable<FrameworkElement> Annotations(CaptureAnnotationController controller) => Field<List<FrameworkElement>>(controller, "_annotations");
    private static void AssertPoint(Point expected, Point actual) { Assert.Equal(expected.X, actual.X, 5); Assert.Equal(expected.Y, actual.Y, 5); }
    private static BitmapSource Image()
    {
        var pixels = new byte[800 * 600 * 4];
        Array.Fill(pixels, (byte)255);
        var image = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgra32, null, pixels, 800 * 4);
        image.Freeze();
        return image;
    }
    private static byte[] Pixels(BitmapSource image) { var data = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(data, image.PixelWidth * 4, 0); return data; }
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, Flags)!.GetValue(target)!;
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags)!.SetValue(target, value);
    private static void Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags)!.Invoke(target, args);
    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources["UiFont"] = new FontFamily("Segoe UI");
                application.Resources["DataFont"] = new FontFamily("Consolas");
                action();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Crop resize test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
