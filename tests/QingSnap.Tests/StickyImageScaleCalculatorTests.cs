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

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void FirstWheelUsesDisplayedPhysicalPixelsRatherThanStaleDipScale(double dpi)
    {
        var border = Math.Max(1, (int)Math.Round(dpi));
        var resolved = StickyImageScaleCalculator.ResolveDisplayedScale(
            800, 500, 1, 800 + border * 2, 500 + border * 2, 0.01, 4,
            dpi, dpi, border, border);
        Assert.Equal(1 / dpi, resolved, 8);
        var next = StickyImageScaleCalculator.Calculate(800, 500,
            PinMotionMath.WheelTarget(resolved, 120), 0.01, 4, dpi, dpi, border, border);
        Assert.Equal(880 + border * 2, next.Width);
        Assert.Equal(550 + border * 2, next.Height);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void PhysicalCapturePlacementWithOnePixelBorderDoesNotJumpOnFirstWheel(double dpi)
    {
        var border = Math.Max(1, (int)Math.Round(dpi));
        var resolved = StickyImageScaleCalculator.ResolveDisplayedScale(
            800, 500, 1, 802, 502, 0.01, 4, dpi, dpi, border, border);
        var next = StickyImageScaleCalculator.Calculate(800, 500,
            PinMotionMath.WheelTarget(resolved, 120), 0.01, 4, dpi, dpi, border, border);
        Assert.InRange(Math.Abs(next.Width - (800 * 1.1 + 2)), 0, 3);
        Assert.InRange(Math.Abs(next.Height - (500 * 1.1 + 2)), 0, 3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void ResolvingRoundedBoundsPreservesPreciseScaleInsteadOfDrifting(double dpi)
    {
        const double preciseScale = 0.8451234567;
        var border = Math.Max(1, (int)Math.Round(dpi));
        var current = StickyImageScaleCalculator.Calculate(1300, 121, preciseScale,
            0.01, 4, dpi, dpi, border, border);
        for (var index = 0; index < 1000; index++)
        {
            var resolved = StickyImageScaleCalculator.ResolveDisplayedScale(1300, 121, current.Scale,
                current.Width, current.Height, 0.01, 4, dpi, dpi, border, border);
            Assert.Equal(preciseScale, resolved);
        }
    }

    private static void AssertAspectRatio(double expected, int width, int height)
    {
        var relativeError = Math.Abs(width / (double)height - expected) / expected;
        Assert.InRange(relativeError, 0, 0.002);
    }
}
