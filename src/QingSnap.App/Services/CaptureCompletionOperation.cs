namespace QingSnap.App.Services;

// Copy and file persistence have independent outcomes. In particular, neither a cold
// image encoder nor a slow history disk may delay publishing the clipboard image.
internal static class CaptureCompletionOperation
{
    internal static async Task<CaptureCompletionOutcome> RunAsync(
        Func<Task<string>> save,
        Func<Task>? copy,
        Action onClipboardReady)
    {
        var copyTask = CopyAsync(copy, onClipboardReady);
        var saveTask = SaveAsync(save);
        await Task.WhenAll(copyTask, saveTask);
        var saved = await saveTask;
        return new CaptureCompletionOutcome(saved.Path, saved.Error, await copyTask);
    }

    private static async Task<Exception?> CopyAsync(Func<Task>? copy, Action onClipboardReady)
    {
        if (copy is null)
        {
            return null;
        }

        try
        {
            await copy();
        }
        catch (Exception exception)
        {
            return exception;
        }

        // A notification failure cannot change a successful clipboard write into a
        // copy failure, nor prevent the history file from being saved.
        try
        {
            onClipboardReady();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Capture", exception, "图片已复制，但显示复制提示失败。");
        }
        return null;
    }

    private static async Task<(string? Path, Exception? Error)> SaveAsync(Func<Task<string>> save)
    {
        try
        {
            return (await save(), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }
}

internal sealed record CaptureCompletionOutcome(
    string? ImagePath,
    Exception? SaveError,
    Exception? CopyError);
