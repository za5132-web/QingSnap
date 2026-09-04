using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class StickyImageScaleCalculatorTests
{
    [Fact]
    public void TinyWideImageUsesOneEffectiveScaleForBothAxes()
    {
        var result = StickyImageScaleCalculator.Calculate(
            imageWidth: 1300,
            imageHeight: 120,
            requestedScale: 0.01,
            minimumScale: 0.01,
            maximumScale: 4);

        Assert.Equal(24, result.Height);
        Assert.Equal(240, result.Width);
        Assert.Equal(22D / 120, result.Scale, 8);
        AssertAspectRatio(1300D / 120, result.Width - 2, result.Height - 2);
    }

    [Fact]
    public void MaximumScaleKeepsTheOriginalAspectRatio()
    {
        var result = StickyImageScaleCalculator.Calculate(
            imageWidth: 986,
            imageHeight: 724,
            requestedScale: 20,
            minimumScale: 0.01,
            maximumScale: 4,
            scaleX: 1.5,
            scaleY: 1.5,
            borderX: 2,
            borderY: 2);

        Assert.Equal(4, result.Scale);
        AssertAspectRatio(986D / 724, result.Width - 4, result.Height - 4);
    }

    [Fact]
    public void SystemLimitProducesOneSmallerScaleForBothAxes()
    {
        var scale = StickyImageScaleCalculator.ScaleThatFits(
            imageWidth: 1920,
            imageHeight: 1080,
            availableWidth: 2400,
            availableHeight: 900,
            scaleX: 1,
            scaleY: 1,
            borderX: 1,
            borderY: 1);
        var result = StickyImageScaleCalculator.Calculate(1920, 1080, scale, 0.01, 4);

        Assert.True(result.Width <= 2400);
        Assert.True(result.Height <= 900);
        AssertAspectRatio(1920D / 1080, result.Width - 2, result.Height - 2);
    }

    private static void AssertAspectRatio(double expected, int width, int height)
    {
        var relativeError = Math.Abs(width / (double)height - expected) / expected;
        Assert.InRange(relativeError, 0, 0.002);
    }
}
