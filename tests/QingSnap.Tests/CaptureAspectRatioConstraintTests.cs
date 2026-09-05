using System.Drawing;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class CaptureAspectRatioConstraintTests
{
    [Theory]
    [InlineData((int)CaptureAspectRatioMode.Square, 1D)]
    [InlineData((int)CaptureAspectRatioMode.FourThree, 4D / 3D)]
    [InlineData((int)CaptureAspectRatioMode.ThreeTwo, 3D / 2D)]
    [InlineData((int)CaptureAspectRatioMode.SixteenNine, 16D / 9D)]
    [InlineData((int)CaptureAspectRatioMode.NineSixteen, 9D / 16D)]
    public void PresetModesReturnExpectedPhysicalRatio(int modeValue, double expected)
    {
        var mode = (CaptureAspectRatioMode)modeValue;
        var ratio = CaptureAspectRatioConstraint.RatioFor(mode, new Rectangle(0, 0, 800, 600));

        Assert.NotNull(ratio);
        Assert.Equal(expected, ratio.Value, 10);
    }

    [Fact]
    public void CurrentModeLocksTheExistingPhysicalRatio()
    {
        var ratio = CaptureAspectRatioConstraint.RatioFor(
            CaptureAspectRatioMode.Current,
            new Rectangle(12, 34, 1379, 863));

        Assert.NotNull(ratio);
        Assert.Equal(1379D / 863D, ratio.Value, 10);
    }

    [Fact]
    public void SixteenNineCreationCorrectsA1920By1079PointerDrag()
    {
        var result = CaptureAspectRatioConstraint.Create(
            new Point(0, 0),
            new Point(1920, 1079),
            16D / 9D,
            1920,
            1080);

        Assert.Equal(new Rectangle(0, 0, 1920, 1080), result);
    }

    [Theory]
    [InlineData((int)CaptureResizeHandle.TopLeft)]
    [InlineData((int)CaptureResizeHandle.TopRight)]
    [InlineData((int)CaptureResizeHandle.BottomLeft)]
    [InlineData((int)CaptureResizeHandle.BottomRight)]
    [InlineData((int)CaptureResizeHandle.Left)]
    [InlineData((int)CaptureResizeHandle.Right)]
    [InlineData((int)CaptureResizeHandle.Top)]
    [InlineData((int)CaptureResizeHandle.Bottom)]
    public void EveryResizeHandleKeepsSixteenNineRatio(int handleValue)
    {
        var handle = (CaptureResizeHandle)handleValue;
        var start = new Rectangle(500, 400, 960, 540);
        var pointer = handle switch
        {
            CaptureResizeHandle.TopLeft => new Point(200, 100),
            CaptureResizeHandle.TopRight => new Point(1800, 100),
            CaptureResizeHandle.BottomLeft => new Point(200, 1100),
            CaptureResizeHandle.BottomRight => new Point(1800, 1100),
            CaptureResizeHandle.Left => new Point(200, 670),
            CaptureResizeHandle.Right => new Point(1800, 670),
            CaptureResizeHandle.Top => new Point(980, 100),
            _ => new Point(980, 1100)
        };

        var result = CaptureAspectRatioConstraint.Resize(
            start,
            pointer,
            handle,
            16D / 9D,
            2560,
            1440);

        Assert.InRange(Math.Abs(result.Width / (double)result.Height - 16D / 9D), 0, 0.001);
        Assert.True(result.Left >= 0 && result.Top >= 0);
        Assert.True(result.Right <= 2560 && result.Bottom <= 1440);
    }

    [Fact]
    public void WidthDrivenGeometryInputRecalculatesHeightWithoutAccumulation()
    {
        var size = CaptureAspectRatioConstraint.ConstrainSize(
            1920,
            1079,
            16D / 9D,
            widthIsPrimary: true,
            2560,
            1440);

        Assert.Equal(new Size(1920, 1080), size);
        for (var index = 0; index < 20; index++)
        {
            size = CaptureAspectRatioConstraint.ConstrainSize(
                size.Width,
                size.Height,
                16D / 9D,
                widthIsPrimary: true,
                2560,
                1440);
        }

        Assert.Equal(new Size(1920, 1080), size);
    }

    [Fact]
    public void GeometryConstraintShrinksBothDimensionsAtScreenBoundary()
    {
        var size = CaptureAspectRatioConstraint.ConstrainSize(
            1800,
            1000,
            16D / 9D,
            widthIsPrimary: true,
            1200,
            700);

        Assert.Equal(new Size(1200, 675), size);
    }

    [Theory]
    [InlineData((int)CaptureResizeHandle.TopLeft, 200, 100)]
    [InlineData((int)CaptureResizeHandle.TopRight, 1760, 100)]
    [InlineData((int)CaptureResizeHandle.BottomLeft, 200, 1240)]
    [InlineData((int)CaptureResizeHandle.BottomRight, 1760, 1240)]
    public void AltCornerResizeKeepsTheSelectionCenterFixed(int handleValue, int pointerX, int pointerY)
    {
        var start = new Rectangle(500, 400, 960, 540);
        var result = CaptureAspectRatioConstraint.ResizeFromCenter(
            start,
            new Point(pointerX, pointerY),
            (CaptureResizeHandle)handleValue,
            ratio: null,
            new Rectangle(0, 0, 2560, 1440));

        Assert.Equal(start.Left + start.Width / 2D, result.Left + result.Width / 2D, 8);
        Assert.Equal(start.Top + start.Height / 2D, result.Top + result.Height / 2D, 8);
    }

    [Fact]
    public void AltShiftCornerResizeCombinesCenterAnchorAndAspectRatio()
    {
        var start = new Rectangle(500, 400, 960, 540);
        var result = CaptureAspectRatioConstraint.ResizeFromCenter(
            start,
            new Point(1780, 1120),
            CaptureResizeHandle.BottomRight,
            16D / 9D,
            new Rectangle(0, 0, 2560, 1440));

        Assert.Equal(new Rectangle(180, 220, 1600, 900), result);
        Assert.Equal(16D / 9D, result.Width / (double)result.Height, 10);
    }

    [Fact]
    public void CenterResizeSupportsNegativeVirtualScreenCoordinatesAndCrossesScreens()
    {
        var virtualBounds = new Rectangle(-1920, -200, 3840, 1280);
        var start = new Rectangle(-480, 100, 960, 540);
        var result = CaptureAspectRatioConstraint.ResizeFromCenter(
            start,
            new Point(-1000, -200),
            CaptureResizeHandle.TopLeft,
            16D / 9D,
            virtualBounds);

        Assert.True(result.Left < 0 && result.Right > 0);
        Assert.True(virtualBounds.Contains(result));
        Assert.InRange(Math.Abs(result.Width / (double)result.Height - 16D / 9D), 0, 0.001);
        Assert.InRange(
            Math.Abs((start.Left + start.Width / 2D) - (result.Left + result.Width / 2D)),
            0,
            0.5);
    }

    [Fact]
    public void CenterResizeCannotFlipWhenPointerCrossesTheCenter()
    {
        var result = CaptureAspectRatioConstraint.ResizeFromCenter(
            new Rectangle(500, 400, 960, 540),
            new Point(1500, 1000),
            CaptureResizeHandle.TopLeft,
            ratio: null,
            new Rectangle(0, 0, 2560, 1440));

        Assert.True(result.Width > 0);
        Assert.True(result.Height > 0);
        Assert.True(result.Left >= 0 && result.Top >= 0);
        Assert.True(result.Right <= 2560 && result.Bottom <= 1440);
    }
}
