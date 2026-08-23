using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class ClipboardService
{
    private const string CaptureRegionFormat = "QingSnap.CaptureRegion.v1";
    private const int ClipboardRetryCount = 12;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"
    };

    public void CopyImage(BitmapSource image)
    {
        SetImage(image, null);
    }

    public void CopyCaptureImage(BitmapSource image, CaptureRegion region)
    {
        SetImage(image, region);
    }

    public ClipboardImageContent? TryGetImage()
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
        CopyTextWithRetry(text);
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
