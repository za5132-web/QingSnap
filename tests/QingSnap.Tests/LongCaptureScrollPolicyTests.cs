using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class LongCaptureScrollPolicyTests
{
    [Fact]
    public void ReliableMeasurementsConvergeTowardSixtyPercentOfScrollableHeight()
    {
        var policy = new LongCaptureScrollPolicy(20);
        var delta = -360;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // Synthetic application: a wheel detent moves 100 real pixels.
            delta = policy.NextDelta(delta, Math.Abs(delta) / 120 * 100, 1000, 0.9, false);
        }

        Assert.Equal(-720, delta);
        Assert.Equal(-720, policy.NextDelta(delta, 600, 1000, 0.9, false));
    }

    [Fact]
    public void UsesScrollableBandNotTheFullCaptureViewport()
    {
        var policy = new LongCaptureScrollPolicy(20);

        Assert.Equal(-720, policy.NextDelta(-720, 360, 600, 0.9, false));
        Assert.Equal(-840, policy.NextDelta(-720, 360, 1000, 0.9, false));
    }

    [Fact]
    public void HigherMinimumOverlapReducesTheTargetAdvance()
    {
        var ordinary = new LongCaptureScrollPolicy(20);
        var highOverlap = new LongCaptureScrollPolicy(50);

        Assert.Equal(-720, ordinary.NextDelta(-720, 600, 1000, 0.9, false));
        Assert.Equal(-600, highOverlap.NextDelta(-720, 600, 1000, 0.9, false));
        Assert.Equal(-720, highOverlap.NextDelta(-720, 450, 1000, 0.9, false));
    }

    [Theory]
    [InlineData(10, 1000, 0.9, false)]
    [InlineData(100, 1000, 0.9, false)]
    [InlineData(300, 1000, 0.04, false)]
    [InlineData(300, 1000, 0.9, true)]
    public void PartialBottomOrAmbiguousMatchNeverAccelerates(
        int appendedHeight, int scrollableHeight, double confidence, bool fallback)
    {
        var policy = new LongCaptureScrollPolicy(20);

        Assert.Equal(-360, policy.NextDelta(-360, appendedHeight, scrollableHeight, confidence, fallback));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(200, 0)]
    [InlineData(-20, 1000)]
    public void MissingMeasurementsDoNotInventCalibration(int appendedHeight, int scrollableHeight)
    {
        var policy = new LongCaptureScrollPolicy(20);

        Assert.Equal(-720, policy.NextDelta(-720, appendedHeight, scrollableHeight, 0.9, false));
    }

    [Fact]
    public void OverscrollIsReducedEvenWhenConfidenceIsLow()
    {
        var policy = new LongCaptureScrollPolicy(20);

        Assert.Equal(-480, policy.NextDelta(-720, 900, 1000, 0.05, true));
    }

    [Fact]
    public void GrowthIsBoundedAndNeverCrossesConfiguredAbsoluteSafetyRange()
    {
        var policy = new LongCaptureScrollPolicy(20);
        Assert.Equal(-480, policy.NextDelta(-360, 130, 1000, 1, false));
        Assert.Equal(-1200, policy.NextDelta(-1200, 130, 1000, 1, false));
        Assert.Equal(-120, policy.NextDelta(-120, 1000, 1000, 1, false));
        Assert.Equal(480, policy.NextDelta(360, 130, 1000, 1, false));
        Assert.Equal(-720, policy.NextDelta(-720, 500, 1000, double.NaN, false));
    }

    [Theory]
    [InlineData(-720, -360)]
    [InlineData(-360, -120)]
    [InlineData(-120, -120)]
    [InlineData(-1200, -600)]
    [InlineData(720, 360)]
    [InlineData(int.MinValue, -600)]
    public void FailureReductionPreservesDirectionAndWholeDetents(int current, int expected)
    {
        Assert.Equal(expected, LongCaptureScrollPolicy.ReduceDelta(current));
    }
}
