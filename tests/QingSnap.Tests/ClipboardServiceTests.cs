using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class ClipboardServiceTests
{
    [Fact]
    public void RetryDelay_IncreasesAndIsCapped()
    {
        var delays = Enumerable.Range(1, 20)
            .Select(ClipboardService.GetRetryDelay)
            .ToArray();

        Assert.All(delays, delay => Assert.InRange(delay.TotalMilliseconds, 1, 350));
        Assert.True(delays.Zip(delays.Skip(1), (left, right) => right >= left).All(value => value));
        Assert.Equal(350, delays[^1].TotalMilliseconds);
    }

    [Fact]
    public void ClipboardBusyComException_IsRecognizedAsContention()
    {
        var exception = new COMException("clipboard busy", unchecked((int)0x800401D0));

        Assert.True(ClipboardService.IsClipboardContentionException(exception));
        Assert.False(ClipboardService.IsClipboardContentionException(new InvalidOperationException()));
    }

    [Fact]
    public void NormalizeClipboardImage_AllZeroAlpha_BecomesOpaqueWithoutChangingRgb()
    {
        byte[] pixels =
        [
            10, 20, 30, 0,
            40, 50, 60, 0
        ];
        var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);

        var normalized = ClipboardService.NormalizeClipboardImage(source);
        var actual = new byte[8];
        normalized.CopyPixels(actual, 8, 0);

        Assert.Equal(PixelFormats.Bgra32, normalized.Format);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, actual);
        Assert.True(normalized.IsFrozen);
    }

    [Fact]
    public void NormalizeClipboardImage_RealTransparency_IsPreserved()
    {
        byte[] pixels =
        [
            10, 20, 30, 0,
            40, 50, 60, 128
        ];
        var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);

        var normalized = ClipboardService.NormalizeClipboardImage(source);
        var actual = new byte[8];
        normalized.CopyPixels(actual, 8, 0);

        Assert.Equal(pixels, actual);
    }
}
