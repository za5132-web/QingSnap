using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class PinSurfaceBudgetTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void ZoomMayExceedDesktopButNeverExceedsSurfaceBudget(double dpi)
    {
        var budget = PinSurfaceBudget.FromHardware(16384, 16384, 2);
        var border = Math.Max(1, Math.Round(dpi));
        var maximum = budget.MaximumScaleFor(1920, 1080, dpi, dpi, border, border, 4);
        var result = StickyImageScaleCalculator.Calculate(1920, 1080, 20, 0.01,
            maximum, dpi, dpi, border, border);
        Assert.True(result.Width > 1920);
        Assert.True(result.Height > 1080);
        Assert.InRange(result.Width, 1, budget.MaximumWidth);
        Assert.InRange(result.Height, 1, budget.MaximumHeight);
        Assert.True((long)result.Width * result.Height <= budget.MaximumPixels);
        Assert.InRange(Math.Abs((result.Width - border * 2) /
            (result.Height - border * 2) / (1920D / 1080) - 1), 0, 0.001);
        var origin = PinMotionMath.AnchoredOrigin(-900.5, result.Width);
        Assert.InRange(Math.Abs(origin + result.Width / 2D - -900.5), 0, 0.5);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void FractionalDesktopCarrierAlwaysFitsTheHardwareAndAreaBudget(double dpi)
    {
        var budget = PinSurfaceBudget.FromHardware(16384, 16384, 2);
        var border = Math.Max(1, Math.Round(dpi));
        foreach (var (width, height) in new[] { (1920, 1080), (60000, 1200), (1200, 60000) })
        {
            var scale = budget.MaximumScaleFor(width, height, dpi, dpi, border, border, 4);
            var halfWidth = width * scale * dpi / 2 + border;
            var halfHeight = height * scale * dpi / 2 + border;
            foreach (var center in new[] { -3000.25, -300.5, 0, 100.5, 200.125 })
            {
                var carrierWidth = (int)(Math.Ceiling(center + halfWidth) - Math.Floor(center - halfWidth));
                var carrierHeight = (int)(Math.Ceiling(center + halfHeight) - Math.Floor(center - halfHeight));
                Assert.InRange(carrierWidth, 1, budget.MaximumWidth);
                Assert.InRange(carrierHeight, 1, budget.MaximumHeight);
                Assert.True((long)carrierWidth * carrierHeight <= budget.MaximumPixels);
            }
        }
    }

    [Theory]
    [InlineData(60000, 1200)]
    [InlineData(1200, 60000)]
    [InlineData(60000, 60000)]
    public void HugeSourceNeverBecomesAHugeWindowSurface(int width, int height)
    {
        var budget = PinSurfaceBudget.FromHardware(16384, 16384, 2);
        var maximum = budget.MaximumScaleFor(width, height, 2, 2, 2, 2, 4);
        var result = StickyImageScaleCalculator.Calculate(width, height, 4,
            0.01, maximum, 2, 2, 2, 2);
        Assert.InRange(result.Width, 1, 8192);
        Assert.InRange(result.Height, 1, 8192);
        Assert.True((long)result.Width * result.Height <= budget.MaximumPixels);
    }

    [Theory]
    [InlineData(2048, 4096, 2, 2048, 4096)]
    [InlineData(16384, 16384, 2, 8192, 8192)]
    [InlineData(0, 0, 0, 4096, 4096)]
    [InlineData(0, 0, 2, 8192, 8192)]
    [InlineData(double.NaN, double.PositiveInfinity, 0, 4096, 4096)]
    public void HardwareAndSoftwareFallbackRemainBounded(double hardwareWidth, double hardwareHeight,
        int tier, int expectedWidth, int expectedHeight)
    {
        var budget = PinSurfaceBudget.FromHardware(hardwareWidth, hardwareHeight, tier);
        Assert.Equal(expectedWidth, budget.MaximumWidth);
        Assert.Equal(expectedHeight, budget.MaximumHeight);
    }
}
