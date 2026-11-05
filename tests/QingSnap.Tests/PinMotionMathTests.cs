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
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void ZoomResponseIsMonotonicAndRefreshRateIndependent(int refreshRate)
    {
        var position = 0D;
        var velocity = 0D;
        var target = Math.Log(1.1);
        for (var index = 0; index < refreshRate; index++)
        {
            var previous = position;
            (position, velocity) = PinMotionMath.AdvanceZoom(position, velocity, target, 1D / refreshRate);
            Assert.InRange(position, previous, target);
            Assert.True(velocity >= 0);
        }
        var direct = PinMotionMath.AdvanceZoom(0, 0, target, 1);
        Assert.Equal(direct.Position, position, 12);
        Assert.Equal(direct.Velocity, velocity, 12);
        Assert.Equal(target, position, 12);
    }

    [Fact]
    public void ZoomRetargetPreservesVelocityRatherThanPulsingWithEveryNotch()
    {
        var atInput = PinMotionMath.AdvanceZoom(0, 0, Math.Log(1.1), 0.08);
        var retargeted = PinMotionMath.AdvanceZoom(atInput.Position, atInput.Velocity, Math.Log(1.21), 0);
        Assert.Equal(atInput, retargeted);
        var next = PinMotionMath.AdvanceZoom(retargeted.Position, retargeted.Velocity, Math.Log(1.21), 0.000001);
        // Finite acceleration, not a discontinuous 27x velocity restart.
        Assert.InRange(Math.Abs(next.Velocity - retargeted.Velocity), 0, 0.001);
        Assert.True(next.Position > retargeted.Position);
    }

    [Fact]
    public void TighteningTargetDuringFastInputDoesNotOvershootThenSpringBack()
    {
        var state = PinMotionMath.AdvanceZoom(0, 0, Math.Log(1.1), 0.02);
        state = PinMotionMath.AdvanceZoom(state.Position, state.Velocity, Math.Log(1.21), 0.02);
        var target = Math.Log(1.1); // +wheel, +wheel, -wheel while still moving.
        var velocity = PinMotionMath.ZoomRetargetVelocity(state.Position, state.Velocity, target);
        Assert.True(velocity < state.Velocity);
        state = (state.Position, velocity);
        for (var index = 0; index < 1000; index++)
        {
            var previous = state.Position;
            state = PinMotionMath.AdvanceZoom(state.Position, state.Velocity, target, 0.001);
            Assert.InRange(state.Position, previous, target);
        }
        Assert.Equal(target, state.Position, 12);
        Assert.Equal(0, PinMotionMath.ZoomRetargetVelocity(1, 4, 0));
        Assert.Equal(0, PinMotionMath.ZoomRetargetVelocity(0, -4, 1));
        Assert.Equal(0, PinMotionMath.ZoomRetargetVelocity(1, 4, 1));
    }

    [Fact]
    public void ZoomIntegratesLongFramesWithoutOvershootAndKeepsRelativeScaleResponse()
    {
        var low = PinMotionMath.AdvanceZoom(Math.Log(0.01), 0, Math.Log(0.011), 0.04);
        var high = PinMotionMath.AdvanceZoom(Math.Log(3), 0, Math.Log(3.3), 0.04);
        Assert.Equal(Math.Exp(low.Position) / 0.01, Math.Exp(high.Position) / 3, 12);
        var stalled = PinMotionMath.AdvanceZoom(0, 0, Math.Log(4), 30);
        Assert.Equal(Math.Log(4), stalled.Position);
        Assert.Equal(0, stalled.Velocity);
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
