using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;
using Xunit;
using DrawingRectangle = System.Drawing.Rectangle;
using WpfPoint = System.Windows.Point;

namespace QingSnap.Tests;

public sealed class CaptureAnnotationUndoRedoTests
{
    [Fact]
    public void ThreeRectanglesCanUndoAndRedoInOrder()
    {
        RunInSta(() =>
        {
            var (controller, _) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Rectangle;
            Draw(controller, new WpfPoint(10, 10), new WpfPoint(40, 40));
            Draw(controller, new WpfPoint(50, 10), new WpfPoint(80, 40));
            Draw(controller, new WpfPoint(90, 10), new WpfPoint(120, 40));

            Assert.True(controller.HasAnnotations);
            Assert.True(controller.CanUndo);
            controller.Undo();
            controller.Undo();
            controller.Undo();
            Assert.False(controller.HasAnnotations);
            Assert.False(controller.CanUndo);
            Assert.True(controller.CanRedo);

            controller.Redo();
            controller.Redo();
            controller.Redo();
            Assert.True(controller.HasAnnotations);
            Assert.True(controller.CanUndo);
            Assert.False(controller.CanRedo);
        });
    }

    [Fact]
    public void ClearIsOneOperationAndNewDrawingInvalidatesRedo()
    {
        RunInSta(() =>
        {
            var (controller, _) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Rectangle;
            Draw(controller, new WpfPoint(10, 10), new WpfPoint(40, 40));
            Draw(controller, new WpfPoint(50, 10), new WpfPoint(80, 40));

            controller.Clear();
            Assert.False(controller.HasAnnotations);
            controller.Undo();
            Assert.True(controller.HasAnnotations);
            Assert.True(controller.CanRedo);

            controller.ActiveTool = CaptureAnnotationTool.Ellipse;
            Draw(controller, new WpfPoint(90, 10), new WpfPoint(120, 40));
            Assert.False(controller.CanRedo);
        });
    }

    [Fact]
    public void OneSelectionDragCreatesOneUndoStep()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Rectangle;
            Draw(controller, new WpfPoint(10, 10), new WpfPoint(50, 50));
            layer.Measure(new Size(200, 200));
            layer.Arrange(new Rect(0, 0, 200, 200));
            layer.UpdateLayout();

            controller.ActiveTool = CaptureAnnotationTool.Select;
            Assert.True(controller.Begin(new WpfPoint(25, 25)));
            controller.Update(new WpfPoint(35, 35));
            controller.Update(new WpfPoint(45, 45));
            controller.End(new WpfPoint(55, 55));

            var moved = layer.Children.OfType<System.Windows.Shapes.Rectangle>().First();
            Assert.Equal(40, Canvas.GetLeft(moved), 3);
            controller.Undo();
            var restored = layer.Children.OfType<System.Windows.Shapes.Rectangle>().First();
            Assert.Equal(10, Canvas.GetLeft(restored), 3);
            controller.Redo();
            var redone = layer.Children.OfType<System.Windows.Shapes.Rectangle>().First();
            Assert.Equal(40, Canvas.GetLeft(redone), 3);
        });
    }

    [Fact]
    public void TextEditAndMoveCanUndoAndRedoStepByStep()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Text;
            Assert.True(controller.Begin(new WpfPoint(20, 20)));
            layer.Children.OfType<TextBox>().Single().Text = "first";
            Assert.True(controller.CommitText());
            Arrange(layer);

            controller.ActiveTool = CaptureAnnotationTool.Select;
            Assert.True(controller.Begin(new WpfPoint(24, 24)));
            controller.Update(new WpfPoint(54, 54));
            controller.End(new WpfPoint(54, 54));
            Arrange(layer);

            Assert.True(controller.BeginEditSelectedText());
            layer.Children.OfType<TextBox>().Single().Text = "edited";
            Assert.True(controller.CommitText());

            controller.Undo();
            Assert.Equal("first", layer.Children.OfType<TextBlock>().Single().Text);
            Assert.Equal(50, Canvas.GetLeft(layer.Children.OfType<TextBlock>().Single()), 3);
            controller.Undo();
            Assert.Equal(20, Canvas.GetLeft(layer.Children.OfType<TextBlock>().Single()), 3);

            controller.Redo();
            controller.Redo();
            var restored = layer.Children.OfType<TextBlock>().Single();
            Assert.Equal("edited", restored.Text);
            Assert.Equal(50, Canvas.GetLeft(restored), 3);
        });
    }

    [Fact]
    public void ConsecutiveWheelChangesAreMergedIntoOneUndoStep()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Rectangle;
            Draw(controller, new WpfPoint(10, 10), new WpfPoint(50, 50));
            Arrange(layer);
            var rectangle = layer.Children.OfType<System.Windows.Shapes.Rectangle>().Single();
            var originalThickness = rectangle.StrokeThickness;

            Assert.NotNull(controller.AdjustAnnotationAt(new WpfPoint(25, 25), 120));
            Assert.NotNull(controller.AdjustAnnotationAt(new WpfPoint(25, 25), 120));
            Assert.NotNull(controller.AdjustAnnotationAt(new WpfPoint(25, 25), 120));
            Assert.Equal(originalThickness + 3, rectangle.StrokeThickness, 3);

            controller.Undo();
            var restored = layer.Children.OfType<System.Windows.Shapes.Rectangle>().Single();
            Assert.Equal(originalThickness, restored.StrokeThickness, 3);
            controller.Undo();
            Assert.False(controller.HasAnnotations);
        });
    }

    [Fact]
    public void MosaicSnapshotsRestoreWithoutSerializingBitmapPixels()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Mosaic;
            Draw(controller, new WpfPoint(10, 10), new WpfPoint(60, 60));
            Assert.Single(layer.Children.OfType<Image>());

            controller.Undo();
            Assert.Empty(layer.Children.OfType<Image>());
            controller.Redo();
            Assert.Single(layer.Children.OfType<Image>());
        });
    }

    [Fact]
    public void EndpointNumberDeletePasteAndLayerChangesRemainReversible()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Line;
            Draw(controller, new WpfPoint(10, 10), new WpfPoint(60, 60));
            Arrange(layer);

            controller.ActiveTool = CaptureAnnotationTool.Select;
            Assert.True(controller.Begin(new WpfPoint(10, 10)));
            controller.Update(new WpfPoint(20, 30));
            controller.End(new WpfPoint(20, 30));
            var line = layer.Children.OfType<System.Windows.Shapes.Line>().Single();
            Assert.Equal(20, line.X1, 3);
            Assert.Equal(30, line.Y1, 3);
            controller.Undo();
            line = layer.Children.OfType<System.Windows.Shapes.Line>().Single();
            Assert.Equal(10, line.X1, 3);
            controller.Redo();
            line = layer.Children.OfType<System.Windows.Shapes.Line>().Single();
            Assert.Equal(20, line.X1, 3);

            controller.ActiveTool = CaptureAnnotationTool.Number;
            Assert.True(controller.Begin(new WpfPoint(100, 100)));
            Arrange(layer);
            controller.ActiveTool = CaptureAnnotationTool.Select;
            Assert.True(controller.SelectAt(new WpfPoint(100, 100)));
            Assert.True(controller.SetSelectedNumber(7));
            Assert.Equal("7", ((TextBlock)layer.Children.OfType<Border>().Single().Child).Text);
            controller.Undo();
            Assert.Equal("1", ((TextBlock)layer.Children.OfType<Border>().Single().Child).Text);
            controller.Redo();
            Assert.Equal("7", ((TextBlock)layer.Children.OfType<Border>().Single().Child).Text);

            controller.CopySelected();
            controller.PasteSelected();
            Assert.Equal(2, layer.Children.OfType<Border>().Count());
            controller.Undo();
            Assert.Single(layer.Children.OfType<Border>());
            controller.Redo();
            Assert.Equal(2, layer.Children.OfType<Border>().Count());

            controller.DeleteSelected();
            Assert.Single(layer.Children.OfType<Border>());
            controller.Undo();
            Assert.Equal(2, layer.Children.OfType<Border>().Count());

            Assert.True(controller.SendSelectedToBack());
            var selectedBadge = layer.Children.OfType<Border>()
                .Single(badge => ((TextBlock)badge.Child).Text == "7" && Canvas.GetLeft(badge) > 90);
            Assert.Equal(0, Panel.GetZIndex(selectedBadge));
            controller.Undo();
            selectedBadge = layer.Children.OfType<Border>()
                .Single(badge => ((TextBlock)badge.Child).Text == "7" && Canvas.GetLeft(badge) > 90);
            Assert.Equal(2, Panel.GetZIndex(selectedBadge));
        });
    }

    [Fact]
    public void TextResizeIsCapturedAsOneOperation()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Text;
            Assert.True(controller.Begin(new WpfPoint(20, 20)));
            layer.Children.OfType<TextBox>().Single().Text = "resize me";
            controller.CommitText();
            Arrange(layer);

            controller.ActiveTool = CaptureAnnotationTool.Select;
            Assert.True(controller.SelectAt(new WpfPoint(24, 24)));
            var originalSize = layer.Children.OfType<TextBlock>().Single().FontSize;
            var handle = layer.Children.OfType<System.Windows.Shapes.Rectangle>()
                .Where(item => Math.Abs(item.Width - 9) < 0.01)
                .OrderBy(item => Canvas.GetLeft(item) + Canvas.GetTop(item))
                .Last();
            var handlePoint = new WpfPoint(
                Canvas.GetLeft(handle) + handle.Width / 2,
                Canvas.GetTop(handle) + handle.Height / 2);

            Assert.True(controller.Begin(handlePoint));
            controller.Update(new WpfPoint(handlePoint.X + 30, handlePoint.Y + 30));
            controller.End(new WpfPoint(handlePoint.X + 30, handlePoint.Y + 30));
            Assert.True(layer.Children.OfType<TextBlock>().Single().FontSize > originalSize);

            controller.Undo();
            Assert.Equal(originalSize, layer.Children.OfType<TextBlock>().Single().FontSize, 3);
            controller.Redo();
            Assert.True(layer.Children.OfType<TextBlock>().Single().FontSize > originalSize);
        });
    }

    [Fact]
    public void ShiftClickAddsAndRemovesIndividualAnnotations()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Rectangle;
            Draw(controller, new WpfPoint(10, 10), new WpfPoint(40, 40));
            Draw(controller, new WpfPoint(60, 10), new WpfPoint(90, 40));
            Arrange(layer);

            controller.ActiveTool = CaptureAnnotationTool.Select;
            Assert.True(controller.SelectAt(new WpfPoint(20, 20)));
            Assert.Equal(1, controller.SelectionCount);
            Assert.False(controller.Begin(new WpfPoint(70, 20), toggleSelection: true));
            Assert.Equal(2, controller.SelectionCount);
            Assert.False(controller.Begin(new WpfPoint(20, 20), toggleSelection: true));
            Assert.Equal(1, controller.SelectionCount);
            Assert.False(controller.Begin(new WpfPoint(70, 20), toggleSelection: true));
            Assert.Equal(0, controller.SelectionCount);
        });
    }

    [Fact]
    public void MarqueeWorksInBothDirectionsAndBlankClickClearsSelection()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Rectangle;
            Draw(controller, new WpfPoint(10, 10), new WpfPoint(40, 40));
            Draw(controller, new WpfPoint(55, 10), new WpfPoint(85, 40));
            Draw(controller, new WpfPoint(120, 10), new WpfPoint(150, 40));
            Arrange(layer);

            controller.ActiveTool = CaptureAnnotationTool.Select;
            Assert.True(controller.Begin(new WpfPoint(2, 80)));
            controller.Update(new WpfPoint(90, 2));
            controller.End(new WpfPoint(90, 2));
            Assert.Equal(2, controller.SelectionCount);

            Assert.True(controller.Begin(new WpfPoint(90, 80)));
            controller.Update(new WpfPoint(2, 2));
            controller.End(new WpfPoint(2, 2));
            Assert.Equal(2, controller.SelectionCount);

            Assert.True(controller.Begin(new WpfPoint(180, 180)));
            controller.End(new WpfPoint(180, 180));
            Assert.Equal(0, controller.SelectionCount);
        });
    }

    [Fact]
    public void MultiSelectionMovesCopiesDeletesAndRestoresAsAGroup()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Rectangle;
            Draw(controller, new WpfPoint(10, 10), new WpfPoint(40, 40));
            Draw(controller, new WpfPoint(60, 10), new WpfPoint(90, 40));
            Arrange(layer);

            controller.ActiveTool = CaptureAnnotationTool.Select;
            Assert.True(controller.Begin(new WpfPoint(2, 60)));
            controller.Update(new WpfPoint(100, 2));
            controller.End(new WpfPoint(100, 2));
            Assert.Equal(2, controller.SelectionCount);

            Assert.True(controller.Begin(new WpfPoint(20, 20)));
            controller.Update(new WpfPoint(35, 35));
            controller.End(new WpfPoint(35, 35));
            var annotations = AnnotationRectangles(layer);
            Assert.Equal(new[] { 25d, 75d }, annotations.Select(Canvas.GetLeft).Order().ToArray());
            controller.Undo();
            annotations = AnnotationRectangles(layer);
            Assert.Equal(new[] { 10d, 60d }, annotations.Select(Canvas.GetLeft).Order().ToArray());
            controller.Redo();

            controller.CopySelected();
            controller.PasteSelected();
            Assert.Equal(2, controller.SelectionCount);
            annotations = AnnotationRectangles(layer);
            Assert.Equal(4, annotations.Length);
            var allLefts = annotations.Select(Canvas.GetLeft).Order().ToArray();
            Assert.Equal(new[] { 25d, 37d, 75d, 87d }, allLefts);
            Assert.Equal(50, allLefts[3] - allLefts[1], 3);

            controller.DeleteSelected();
            Assert.Equal(2, AnnotationRectangles(layer).Length);
            controller.Undo();
            Assert.Equal(4, AnnotationRectangles(layer).Length);
            Assert.Equal(2, controller.SelectionCount);
            controller.Redo();
            Assert.Equal(2, AnnotationRectangles(layer).Length);
        });
    }

    [Fact]
    public void MultiSelectionLayerCommandsPreserveGroupOrderAndUndo()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Rectangle;
            Draw(controller, new WpfPoint(10, 10), new WpfPoint(40, 40));
            Draw(controller, new WpfPoint(60, 10), new WpfPoint(90, 40));
            Draw(controller, new WpfPoint(120, 10), new WpfPoint(150, 40));
            Arrange(layer);

            controller.ActiveTool = CaptureAnnotationTool.Select;
            Assert.True(controller.Begin(new WpfPoint(2, 60)));
            controller.Update(new WpfPoint(100, 2));
            controller.End(new WpfPoint(100, 2));
            Assert.Equal(2, controller.SelectionCount);

            Assert.True(controller.BringSelectedForward());
            Assert.Equal(
                new[] { 1, 2, 0 },
                AnnotationRectangles(layer)
                    .OrderBy(Canvas.GetLeft)
                    .Select(Panel.GetZIndex)
                    .ToArray());
            controller.Undo();
            Assert.Equal(
                new[] { 0, 1, 2 },
                AnnotationRectangles(layer)
                    .OrderBy(Canvas.GetLeft)
                    .Select(Panel.GetZIndex)
                    .ToArray());
            controller.Redo();
            Assert.Equal(
                new[] { 1, 2, 0 },
                AnnotationRectangles(layer)
                    .OrderBy(Canvas.GetLeft)
                    .Select(Panel.GetZIndex)
                    .ToArray());

            Assert.True(controller.SendSelectedToBack());
            Assert.Equal(
                new[] { 0, 1, 2 },
                AnnotationRectangles(layer)
                    .OrderBy(Canvas.GetLeft)
                    .Select(Panel.GetZIndex)
                    .ToArray());
        });
    }

    [Fact]
    public void ConsecutiveKeyboardNudgesAreOneUndoRedoOperation()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Rectangle;
            Draw(controller, new WpfPoint(20, 20), new WpfPoint(60, 60));
            Arrange(layer);

            controller.ActiveTool = CaptureAnnotationTool.Select;
            Assert.True(controller.SelectAt(new WpfPoint(30, 30)));
            Assert.True(controller.NudgeSelection(1, 0));
            Assert.True(controller.NudgeSelection(1, 0));
            Assert.True(controller.NudgeSelection(0, 10));
            var moved = AnnotationRectangles(layer).Single();
            Assert.Equal(22, Canvas.GetLeft(moved), 3);
            Assert.Equal(30, Canvas.GetTop(moved), 3);

            controller.Undo();
            var restored = AnnotationRectangles(layer).Single();
            Assert.Equal(20, Canvas.GetLeft(restored), 3);
            Assert.Equal(20, Canvas.GetTop(restored), 3);
            controller.Redo();
            var redone = AnnotationRectangles(layer).Single();
            Assert.Equal(22, Canvas.GetLeft(redone), 3);
            Assert.Equal(30, Canvas.GetTop(redone), 3);
        });
    }

    [Fact]
    public void KeyboardNudgeMovesMultiSelectionTogetherAndKeepsItInsideBounds()
    {
        RunInSta(() =>
        {
            var (controller, layer) = CreateController();
            controller.ActiveTool = CaptureAnnotationTool.Rectangle;
            Draw(controller, new WpfPoint(10, 10), new WpfPoint(40, 40));
            Draw(controller, new WpfPoint(60, 10), new WpfPoint(90, 40));
            Arrange(layer);

            controller.ActiveTool = CaptureAnnotationTool.Select;
            Assert.True(controller.Begin(new WpfPoint(2, 60)));
            controller.Update(new WpfPoint(100, 2));
            controller.End(new WpfPoint(100, 2));
            Assert.Equal(2, controller.SelectionCount);

            Assert.True(controller.NudgeSelection(10, 10));
            var moved = AnnotationRectangles(layer).OrderBy(Canvas.GetLeft).ToArray();
            Assert.Equal(new[] { 20d, 70d }, moved.Select(Canvas.GetLeft).ToArray());
            Assert.Equal(new[] { 20d, 20d }, moved.Select(Canvas.GetTop).ToArray());
            Assert.Equal(50, Canvas.GetLeft(moved[1]) - Canvas.GetLeft(moved[0]), 3);

            Assert.True(controller.NudgeSelection(-1000, -1000));
            moved = AnnotationRectangles(layer).OrderBy(Canvas.GetLeft).ToArray();
            Assert.True(moved.Min(Canvas.GetLeft) >= -0.001);
            Assert.True(moved.Min(Canvas.GetTop) >= -0.001);
            Assert.Equal(50, Canvas.GetLeft(moved[1]) - Canvas.GetLeft(moved[0]), 3);
        });
    }

    [Theory]
    [InlineData(1, 1920, 1920, 1)]
    [InlineData(1, 1280, 1920, 1)]
    [InlineData(10, 1280, 1920, 10)]
    public void ScreenPixelMovementRemainsPhysicalPixelsAtAnyDpi(
        int requestedPixels,
        double surfaceDipLength,
        int sourcePixelLength,
        double expectedPhysicalPixels)
    {
        var dipDistance = ScreenPixelMovement.ToDip(
            requestedPixels,
            surfaceDipLength,
            sourcePixelLength);
        var physicalDistance = dipDistance * sourcePixelLength / surfaceDipLength;

        Assert.Equal(expectedPhysicalPixels, physicalDistance, 8);
    }

    private static (CaptureAnnotationController Controller, Canvas Layer) CreateController()
    {
        var pixels = new byte[200 * 200 * 4];
        var bitmap = BitmapSource.Create(
            200,
            200,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            200 * 4);
        bitmap.Freeze();
        var layer = new Canvas();
        var controller = new CaptureAnnotationController(
            layer,
            new ScreenSnapshot(new DrawingRectangle(0, 0, 200, 200), bitmap),
            new AppSettings());
        controller.SetBounds(new Rect(0, 0, 200, 200), 200, 200);
        return (controller, layer);
    }

    private static void Draw(CaptureAnnotationController controller, WpfPoint start, WpfPoint end)
    {
        Assert.True(controller.Begin(start));
        controller.Update(end);
        controller.End(end);
    }

    private static void Arrange(Canvas layer)
    {
        layer.Measure(new Size(200, 200));
        layer.Arrange(new Rect(0, 0, 200, 200));
        layer.UpdateLayout();
    }

    private static System.Windows.Shapes.Rectangle[] AnnotationRectangles(Canvas layer) =>
        layer.Children
            .OfType<System.Windows.Shapes.Rectangle>()
            .Where(item => item.StrokeThickness >= 2.5 && item.Width >= 20)
            .ToArray();

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
