using System.Runtime.InteropServices;
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
}
