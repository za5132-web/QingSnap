using System.IO;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class ClipboardService : IDisposable
{
    private const string CaptureRegionFormat = "QingSnap.CaptureRegion.v1";
    private const int ClipboardRetryCount = 12;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
    };
    private readonly StaClipboardQueue _queue = new();

    public Task CopyImageAsync(BitmapSource image) =>
        _queue.InvokeAsync(() => SetImage(FreezeImage(image), null));

    public Task CopyCaptureImageAsync(BitmapSource image, CaptureRegion region) =>
        _queue.InvokeAsync(() => SetImage(FreezeImage(image), region));

    public Task<ClipboardImageContent?> TryGetImageAsync() =>
        _queue.InvokeAsync(TryGetImageCore);

    public Task CopyTextAsync(string text) =>
        _queue.InvokeAsync(() => CopyTextWithRetry(text));

    public void CopyImage(BitmapSource image)
    {
        CopyImageAsync(image).GetAwaiter().GetResult();
    }

    public void CopyCaptureImage(BitmapSource image, CaptureRegion region)
    {
        CopyCaptureImageAsync(image, region).GetAwaiter().GetResult();
    }

    public ClipboardImageContent? TryGetImage()
    {
        return TryGetImageAsync().GetAwaiter().GetResult();
    }

    private static ClipboardImageContent? TryGetImageCore()
    {
        COMException? lastException = null;
        for (var attempt = 1; attempt <= ClipboardRetryCount; attempt++)
        {
            try
            {
                var data = System.Windows.Clipboard.GetDataObject();
                if (data is null)
                {
                    return null;
                }

                var preferredRegion = ParseCaptureRegion(data.GetData(CaptureRegionFormat) as string);
                var clipboardImage = System.Windows.Clipboard.GetImage();
                if (clipboardImage is not null)
                {
                    return new ClipboardImageContent(
                        FreezeImage(clipboardImage),
                        preferredRegion is null ? "剪贴板图片" : "QingSnap 截图",
                        preferredRegion);
                }

                if (data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
                {
                    foreach (var path in paths)
                    {
                        var image = TryLoadImageFile(path);
                        if (image is not null)
                        {
                            return new ClipboardImageContent(image, path, null);
                        }
                    }
                }

                return null;
            }
            catch (COMException exception)
            {
                lastException = exception;
                WaitBeforeRetry(attempt);
            }
        }

        throw CreateClipboardBusyException(lastException);
    }

    private static void SetImage(BitmapSource image, CaptureRegion? region)
    {
        COMException? lastException = null;
        for (var attempt = 1; attempt <= ClipboardRetryCount; attempt++)
        {
            try
            {
                var data = new System.Windows.DataObject();
                data.SetImage(image);
                if (region is not null)
                {
                    data.SetData(
                        CaptureRegionFormat,
                        $"{region.X},{region.Y},{region.Width},{region.Height}");
                }

                System.Windows.Clipboard.SetDataObject(data, true);
                return;
            }
            catch (COMException exception)
            {
                lastException = exception;
                WaitBeforeRetry(attempt);
            }
        }

        throw CreateClipboardBusyException(lastException);
    }

    private static CaptureRegion? ParseCaptureRegion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',');
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], out var x) ||
            !int.TryParse(parts[1], out var y) ||
            !int.TryParse(parts[2], out var width) ||
            !int.TryParse(parts[3], out var height))
        {
            return null;
        }

        var region = new CaptureRegion(x, y, width, height);
        return region.IsValid ? region : null;
    }

    private static BitmapSource? TryLoadImageFile(string path)
    {
        if (!File.Exists(path) || !SupportedImageExtensions.Contains(Path.GetExtension(path)))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static BitmapSource FreezeImage(BitmapSource image)
    {
        if (image.IsFrozen)
        {
            return image;
        }

        var frozen = image.Clone();
        frozen.Freeze();
        return frozen;
    }

    public void CopyText(string text)
    {
        CopyTextAsync(text).GetAwaiter().GetResult();
    }

    internal static void CopyTextWithRetry(string text)
    {
        int lastError = 0;
        for (var attempt = 1; attempt <= ClipboardRetryCount; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                lastError = Marshal.GetLastWin32Error();
                WaitBeforeRetry(attempt);
                continue;
            }

            try
            {
                SetUnicodeText(text);
                return;
            }
            finally
            {
                CloseClipboard();
            }
        }

        throw CreateClipboardBusyException(
            lastError == 0 ? null : new Win32Exception(lastError));
    }

    private static void SetUnicodeText(string text)
    {
        if (!EmptyClipboard())
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法清空系统剪贴板。");
        }

        var bytes = checked((nuint)((text.Length + 1) * sizeof(char)));
        var memory = GlobalAlloc(GmemMoveable, bytes);
        if (memory == IntPtr.Zero)
        {
            throw new OutOfMemoryException("无法为剪贴板文字分配内存。");
        }

        var transferred = false;
        try
        {
            var target = GlobalLock(memory);
            if (target == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入剪贴板文字。");
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法提交剪贴板文字。");
            }

            transferred = true;
        }
        finally
        {
            if (!transferred)
            {
                GlobalFree(memory);
            }
        }
    }

    private static void WaitBeforeRetry(int attempt)
    {
        if (attempt < ClipboardRetryCount)
        {
            Thread.Sleep(Math.Min(400, 40 * attempt));
        }
    }

    private static InvalidOperationException CreateClipboardBusyException(Exception? exception) =>
        new("剪贴板暂时被其他程序占用，请稍后再试。", exception);

    public void Dispose() => _queue.Dispose();

    private sealed class StaClipboardQueue : IDisposable
    {
        private readonly BlockingCollection<Action> _workItems = new();
        private readonly Thread _thread;
        private bool _disposed;

        public StaClipboardQueue()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "QingSnap Clipboard"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task InvokeAsync(Action action) => InvokeAsync(() =>
        {
            action();
            return true;
        });

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            if (_disposed)
            {
                return Task.FromException<T>(new ObjectDisposedException(nameof(StaClipboardQueue)));
            }

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _workItems.Add(() =>
                {
                    try
                    {
                        completion.TrySetResult(action());
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                completion.TrySetException(new ObjectDisposedException(nameof(StaClipboardQueue)));
            }

            return completion.Task;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _workItems.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(1));
            _workItems.Dispose();
        }

        private void Run()
        {
            foreach (var workItem in _workItems.GetConsumingEnumerable())
            {
                workItem();
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}

public sealed record ClipboardImageContent(
    BitmapSource Image,
    string SourceName,
    CaptureRegion? PreferredRegion);
