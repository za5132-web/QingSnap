using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class MagnifierPixelGridTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(10.99, 10)]
    [InlineData(11, 11)]
    [InlineData(99.99, 99)]
    [InlineData(100, 99)]
    public void PointerCoordinatesMapToThePixelAfterEachBoundary(double position, int expected)
    {
        Assert.Equal(expected, MagnifierPixelGrid.MapPointerToPixel(position, 100, 100));
    }

    [Fact]
    public void CrosshairUsesTheCentralPixelBoundary()
    {
        const int pixel = 331;
        var cropOrigin = MagnifierPixelGrid.GetCropOrigin(pixel, 1920, MagnifierPixelGrid.Columns);

        var offset = MagnifierPixelGrid.GetBoundaryOffset(
            pixel,
            cropOrigin,
            MagnifierPixelGrid.Columns,
            280);

        Assert.Equal(140, offset);
    }

    [Fact]
    public void CrosshairStillTracksTheTargetWhenCropTouchesScreenEdge()
    {
        var cropOrigin = MagnifierPixelGrid.GetCropOrigin(2, 1920, MagnifierPixelGrid.Columns);

        var offset = MagnifierPixelGrid.GetBoundaryOffset(
            2,
            cropOrigin,
            MagnifierPixelGrid.Columns,
            280);

        Assert.Equal(20, offset);
    }
}
