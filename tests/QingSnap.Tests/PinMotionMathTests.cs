using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class PinMotionMathTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.875)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void EaseOutIsBoundedAndEndsExactly(double progress, double expected) =>
        Assert.Equal(expected, PinMotionMath.EaseOut(progress), 8);

    [Fact]
    public void WheelInputsAccumulateAgainstTargetNotIntermediateFrame()
    {
        var target = 1D;
        for (var index = 0; index < 5; index++) target = PinMotionMath.WheelTarget(target, 120);
        Assert.Equal(Math.Pow(1.1, 5), target, 8);
        for (var index = 0; index < 5; index++) target = PinMotionMath.WheelTarget(target, -120);
        Assert.Equal(1, target, 8);
        Assert.Equal(Math.Pow(1.1, 0.25), PinMotionMath.WheelTarget(1, 30), 8);
    }

    [Theory]
    [InlineData(-1800.5)]
    [InlineData(-1)]
    [InlineData(960.5)]
    public void PhysicalAnchorDoesNotAccumulateAcrossAlternatingSizes(double center)
    {
        for (var index = 0; index < 2000; index++)
        {
            var size = 500 + index % 13;
            var origin = PinMotionMath.AnchoredOrigin(center, size);
            Assert.InRange(Math.Abs(origin + size / 2D - center), 0, 0.5);
        }
    }

    [Theory]
    [InlineData(false, 1919)]
    [InlineData(false, 1862)]
    [InlineData(true, -1920)]
    [InlineData(true, -1862)]
    public void DockActivationZoneIsStableWhileLabelSlides(bool left, int x)
    {
        Assert.True(PinMotionMath.IsInsideDockZone(x, 100, -1920, 1920, 80, 58, 44, left, 3));
        Assert.False(PinMotionMath.IsInsideDockZone(x, 200, -1920, 1920, 80, 58, 44, left, 3));
        Assert.False(PinMotionMath.IsInsideDockZone(0, 100, -1920, 1920, 80, 58, 44, left, 3));
    }
}
