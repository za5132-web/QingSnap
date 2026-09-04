using System.IO;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class ClipboardService : IDisposable
{
    private const string CaptureRegionFormat = "QingSnap.CaptureRegion.v1";
    private const int ClipbrdECantOpen = unchecked((int)0x800401D0);
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private static readonly TimeSpan ClipboardWriteTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ClipboardReadTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ClipboardFlushTimeout = TimeSpan.FromSeconds(4);
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
        return ExecuteWithClipboardRetry(() =>
        {
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null)
            {
                return null;
            }

            var sequenceNumber = NativeMethods.GetClipboardSequenceNumber();
            var preferredRegion = ParseCaptureRegion(data.GetData(CaptureRegionFormat) as string);
            var clipboardImage = TryReadClipboardImage(data);
            if (clipboardImage is not null)
            {
                return new ClipboardImageContent(
                    clipboardImage,
                    preferredRegion is null ? "剪贴板图片" : "QingSnap 截图",
                    preferredRegion,
                    sequenceNumber);
            }

            if (data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            {
                foreach (var path in paths)
                {
                    var image = TryLoadImageFile(path);
                    if (image is not null)
                    {
                        return new ClipboardImageContent(image, path, null, sequenceNumber);
                    }
                }
            }

            return null;
        }, ClipboardReadTimeout, "读取图片");
    }

    private static BitmapSource? TryReadClipboardImage(System.Windows.IDataObject data)
    {
        foreach (var format in data.GetFormats(false).Where(format =>
                     string.Equals(format, "PNG", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(format, "image/png", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (TryDecodeClipboardStream(data.GetData(format, false)) is { } encodedImage)
                {
                    return NormalizeClipboardImage(encodedImage);
                }
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
            {
                DiagnosticLog.Warning("Clipboard", $"剪贴板 {format} 数据无法解码，已回退到位图格式：{exception.Message}");
            }
        }

        var bitmap = data.GetData(System.Windows.DataFormats.Bitmap, false) as BitmapSource ??
                     System.Windows.Clipboard.GetImage();
        return bitmap is null ? null : NormalizeClipboardImage(bitmap);
    }

    private static BitmapSource? TryDecodeClipboardStream(object? value)
    {
        if (value is not Stream && value is not byte[])
        {
            return null;
        }

        using var ownedStream = value is byte[] bytes ? new MemoryStream(bytes, writable: false) : null;
        var stream = ownedStream ?? (Stream)value;
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        return decoder.Frames.Count == 0 ? null : decoder.Frames[0];
    }

    internal static BitmapSource NormalizeClipboardImage(BitmapSource image)
    {
        BitmapSource converted = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);

        var hasVisibleAlpha = false;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
            {
                hasVisibleAlpha = true;
                break;
            }
        }

        if (!hasVisibleAlpha)
        {
            for (var index = 3; index < pixels.Length; index += 4)
            {
                pixels[index] = byte.MaxValue;
            }

            DiagnosticLog.Info("Clipboard", "检测到外部 DIB 的透明通道全为 0，已按不透明图片修复。");
        }

        var dpiX = double.IsFinite(converted.DpiX) && converted.DpiX > 0 ? converted.DpiX : 96;
        var dpiY = double.IsFinite(converted.DpiY) && converted.DpiY > 0 ? converted.DpiY : 96;
        var normalized = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            dpiX,
            dpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        normalized.Freeze();
        return normalized;
    }

    private static void SetImage(BitmapSource image, CaptureRegion? region)
    {
        var data = new System.Windows.DataObject();
        data.SetImage(image);
        if (region is not null)
        {
            data.SetData(
                CaptureRegionFormat,
                $"{region.X},{region.Y},{region.Width},{region.Height}");
        }

        // Publish first without forcing an immediate flush. Clipboard managers frequently hold
        // OpenClipboard for a short period; tying publication and flush together turns a harmless
        // persistence delay into a failed screenshot copy.
        ExecuteWithClipboardRetry(
            () => System.Windows.Clipboard.SetDataObject(data, false),
            ClipboardWriteTimeout,
            "复制图片");

        try
        {
            ExecuteWithClipboardRetry(
                System.Windows.Clipboard.Flush,
                ClipboardFlushTimeout,
                "持久化图片");
        }
        catch (InvalidOperationException exception)
        {
            // QingSnap is a tray application, so the published OLE data remains available while
            // it is running. Failure to flush must not turn a successful copy into a failure.
            DiagnosticLog.Warning(
                "Clipboard",
                $"图片已经复制，但暂时无法持久化；程序运行期间仍可正常粘贴。{DescribeClipboardOwner()} {exception.Message}");
        }
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
        var stopwatch = Stopwatch.StartNew();
        Exception? lastException = null;
        var attempt = 0;
        while (true)
        {
            attempt++;
            if (!OpenClipboard(IntPtr.Zero))
            {
                lastException = new Win32Exception(Marshal.GetLastWin32Error());
            }
            else
            {
                try
                {
                    SetUnicodeText(text);
                    LogRecovery("复制文字", attempt, stopwatch.Elapsed);
                    return;
                }
                catch (Exception exception) when (IsClipboardContentionException(exception))
                {
                    lastException = exception;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            if (stopwatch.Elapsed >= ClipboardWriteTimeout)
            {
                ThrowClipboardBusy("复制文字", stopwatch.Elapsed, lastException);
            }

            WaitBeforeRetry(attempt, ClipboardWriteTimeout - stopwatch.Elapsed);
        }
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

    private static T ExecuteWithClipboardRetry<T>(
        Func<T> operation,
        TimeSpan timeout,
        string operationName)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? lastException = null;
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                var result = operation();
                LogRecovery(operationName, attempt, stopwatch.Elapsed);
                return result;
            }
            catch (Exception exception) when (IsClipboardContentionException(exception))
            {
                lastException = exception;
            }

            if (stopwatch.Elapsed >= timeout)
            {
                ThrowClipboardBusy(operationName, stopwatch.Elapsed, lastException);
            }

            WaitBeforeRetry(attempt, timeout - stopwatch.Elapsed);
        }
    }

    private static void ExecuteWithClipboardRetry(
        Action operation,
        TimeSpan timeout,
        string operationName) =>
        ExecuteWithClipboardRetry(() =>
        {
            operation();
            return true;
        }, timeout, operationName);

    internal static bool IsClipboardContentionException(Exception exception) =>
        exception is COMException { ErrorCode: ClipbrdECantOpen } ||
        exception is ExternalException;

    internal static TimeSpan GetRetryDelay(int attempt)
    {
        var normalizedAttempt = Math.Max(1, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(350, 25 + (normalizedAttempt * normalizedAttempt * 7)));
    }

    private static void WaitBeforeRetry(int attempt, TimeSpan remaining)
    {
        var delay = GetRetryDelay(attempt);
        if (delay > remaining)
        {
            delay = remaining;
        }

        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }

    private static void LogRecovery(string operation, int attempt, TimeSpan elapsed)
    {
        if (attempt <= 1)
        {
            return;
        }

        DiagnosticLog.Info(
            "Clipboard",
            $"{operation}在剪贴板占用后恢复：尝试 {attempt} 次，耗时 {elapsed.TotalMilliseconds:0} ms。");
    }

    private static void ThrowClipboardBusy(string operation, TimeSpan elapsed, Exception? exception)
    {
        var message = $"{operation}等待剪贴板 {elapsed.TotalSeconds:0.0} 秒后仍未成功。{DescribeClipboardOwner()}";
        DiagnosticLog.Error("Clipboard", exception ?? new ExternalException(message), message);
        throw CreateClipboardBusyException(exception);
    }

    private static string DescribeClipboardOwner()
    {
        var window = GetOpenClipboardWindow();
        if (window == IntPtr.Zero)
        {
            return "未检测到持续占用进程。";
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return $"占用窗口：0x{window.ToInt64():X}。";
        }

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return $"占用进程：{process.ProcessName}（PID {processId}）。";
        }
        catch
        {
            return $"占用进程 PID：{processId}。";
        }
    }

    private static InvalidOperationException CreateClipboardBusyException(Exception? exception) =>
        new("剪贴板被其他程序持续占用；截图已保存在记录中，请稍后再次复制。", exception);

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

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
    CaptureRegion? PreferredRegion,
    uint SequenceNumber);
