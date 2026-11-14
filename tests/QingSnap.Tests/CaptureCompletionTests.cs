using System.IO;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class CaptureCompletionTests
{
    [Fact]
    public async Task SlowHistorySaveDoesNotDelayCopyOrReadyNotification()
    {
        var save = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var completion = CaptureCompletionOperation.RunAsync(
            () => { events.Add("save"); return save.Task; },
            () => { events.Add("copy"); return Task.CompletedTask; },
            () => ready.SetResult());

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(completion.IsCompleted);
        Assert.Equal(new[] { "copy", "save" }, events);
        save.SetResult("capture.png");
        var result = await completion;
        Assert.Equal("capture.png", result.ImagePath);
        Assert.Null(result.CopyError);
        Assert.Null(result.SaveError);
    }

    [Fact]
    public async Task SlowClipboardDoesNotPreventSavingAndDoesNotClaimCopySucceeded()
    {
        var copy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = false;
        var notificationCount = 0;
        var completion = CaptureCompletionOperation.RunAsync(
            () => { saved = true; return Task.FromResult("capture.png"); },
            () => copy.Task,
            () => notificationCount++);

        Assert.True(saved);
        Assert.Equal(0, notificationCount);
        Assert.False(completion.IsCompleted);
        copy.SetResult();
        var result = await completion;
        Assert.Null(result.CopyError);
        Assert.Equal(1, notificationCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SaveAndCopyFailuresAreIndependent(bool saveFails, bool copyFails)
    {
        var saveError = new IOException("synthetic history failure");
        var copyError = new InvalidOperationException("synthetic clipboard busy");
        var notificationCount = 0;
        var result = await CaptureCompletionOperation.RunAsync(
            () => saveFails ? Task.FromException<string>(saveError) : Task.FromResult("capture.png"),
            () => copyFails ? Task.FromException(copyError) : Task.CompletedTask,
            () => notificationCount++);

        Assert.Equal(saveFails ? saveError : null, result.SaveError);
        Assert.Equal(copyFails ? copyError : null, result.CopyError);
        Assert.Equal(saveFails ? null : "capture.png", result.ImagePath);
        Assert.Equal(copyFails ? 0 : 1, notificationCount);
    }

    [Fact]
    public async Task SynchronousClipboardExceptionStillStartsSave()
    {
        var error = new InvalidOperationException("synthetic copy initialization failure");
        var result = await CaptureCompletionOperation.RunAsync(
            () => Task.FromResult("capture.png"),
            () => throw error,
            () => Assert.Fail("Failed copy must not display a success notification."));

        Assert.Equal("capture.png", result.ImagePath);
        Assert.Same(error, result.CopyError);
    }

    [Fact]
    public async Task SynchronousSaveExceptionDoesNotPreventCopy()
    {
        var error = new IOException("synthetic invalid history directory");
        var notificationCount = 0;
        var result = await CaptureCompletionOperation.RunAsync(
            () => throw error,
            () => Task.CompletedTask,
            () => notificationCount++);

        Assert.Equal(1, notificationCount);
        Assert.Null(result.CopyError);
        Assert.Same(error, result.SaveError);
    }

    [Fact]
    public async Task AutoCopyDisabledSavesWithoutCopyNotification()
    {
        var result = await CaptureCompletionOperation.RunAsync(
            () => Task.FromResult("capture.png"),
            null,
            () => Assert.Fail("Auto-copy is disabled."));

        Assert.Equal("capture.png", result.ImagePath);
        Assert.Null(result.SaveError);
    }

    [Fact]
    public async Task NotificationFailureDoesNotChangeSuccessfulCopyOrSave()
    {
        var result = await CaptureCompletionOperation.RunAsync(
            () => Task.FromResult("capture.png"),
            () => Task.CompletedTask,
            () => throw new InvalidOperationException("synthetic notification failure"));

        Assert.Equal("capture.png", result.ImagePath);
        Assert.Null(result.CopyError);
        Assert.Null(result.SaveError);
    }
}
