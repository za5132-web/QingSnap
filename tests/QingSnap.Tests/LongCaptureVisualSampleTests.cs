using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class LongCaptureVisualSampleTests
{
    [Fact]
    public void SamplesAreBoundedEvenForLargeFrame()
    {
        var sample = LongCaptureVisualSample.Create(CreateImage(1200, 900));

        Assert.Equal(128 * 96, sample.SampleCount);
        Assert.True(sample.MatchesAnchor(sample));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 16)]
    public void SmallImagesKeepValidSampleCoordinates(int width, int height)
    {
        var first = LongCaptureVisualSample.Create(CreateImage(width, height));
        var second = LongCaptureVisualSample.Create(CreateImage(width, height));

        Assert.Equal(width * height, first.SampleCount);
        Assert.Equal(0, first.CompareTo(second));
    }

    [Fact]
    public void ScrollRegionIgnoresChangingFixedHeader()
    {
        var region = new PixelRegion(0, 20, 120, 100);
        var first = LongCaptureVisualSample.Create(CreateImage(120, 100), region);
        var second = LongCaptureVisualSample.Create(CreateImage(120, 100, changedHeader: true), region);

        Assert.True(first.MatchesAnchor(second));
        Assert.False(LongCaptureVisualSample.Create(CreateImage(120, 100))
            .MatchesAnchor(LongCaptureVisualSample.Create(CreateImage(120, 100, changedHeader: true))));
    }

    [Fact]
    public void DisplacedImageDoesNotMatchRollbackAnchor()
    {
        var first = LongCaptureVisualSample.Create(CreateImage(240, 180));
        var second = LongCaptureVisualSample.Create(CreateImage(240, 180, offset: 32));

        Assert.False(first.MatchesAnchor(second));
    }

    [Fact]
    public void DifferentRegionsAreNotComparable()
    {
        var image = CreateImage(120, 100);
        var first = LongCaptureVisualSample.Create(image, new PixelRegion(0, 0, 120, 80));
        var second = LongCaptureVisualSample.Create(image, new PixelRegion(0, 20, 120, 100));

        Assert.Equal(double.PositiveInfinity, first.CompareTo(second));
        Assert.False(first.MatchesAnchor(second));
    }

    [Fact]
    public void InvalidRegionIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LongCaptureVisualSample.Create(CreateImage(20, 20), new PixelRegion(-1, 0, 20, 20)));
    }

    private static BitmapSource CreateImage(int width, int height, bool changedHeader = false, int offset = 0)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 4;
                var value = changedHeader && y < 20 ? 255 : (x * 7 + (y + offset) * 13) % 256;
                pixels[index] = pixels[index + 1] = pixels[index + 2] = (byte)value;
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }
}
