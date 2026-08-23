using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic.FileIO;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class CaptureHistoryService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp"
    };

    private readonly AppSettingsService _settingsService;
    private DateTime _lastCleanupDate;

    public CaptureHistoryService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        Directory.CreateDirectory(HistoryDirectory);
    }

    public string HistoryDirectory => _settingsService.Current.HistoryDirectory;

    public string Save(BitmapSource image)
    {
        var now = DateTime.Now;
        var monthDirectory = Path.Combine(HistoryDirectory, now.ToString("yyyy-MM"));
        Directory.CreateDirectory(monthDirectory);

        CleanupExpiredItems();
        var format = _settingsService.Current.OutputFormat;
        var extension = format == "JPG" ? ".jpg" : format == "BMP" ? ".bmp" : ".png";
        var path = Path.Combine(monthDirectory, $"{now:yyyyMMdd_HHmmss_fff}{extension}");
        BitmapEncoder encoder = format switch
        {
            "JPG" => new JpegBitmapEncoder { QualityLevel = _settingsService.Current.JpegQuality },
            "BMP" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    public HistorySnapshot LoadSnapshot(int maximumItems, CancellationToken cancellationToken)
    {
        CleanupExpiredItems();
        Directory.CreateDirectory(HistoryDirectory);

        var files = Directory
            .EnumerateFiles(HistoryDirectory, "*.*", System.IO.SearchOption.AllDirectories)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Select(path => new FileInfo(path))
            .ToArray();

        var items = new List<HistoryItem>(Math.Min(files.Length, maximumItems));
        foreach (var file in files.OrderByDescending(file => file.LastWriteTime).Take(maximumItems))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = TryLoadItem(file);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return new HistorySnapshot(files.Length, files.Sum(file => file.Length), items);
    }

    private void CleanupExpiredItems()
    {
        var retentionDays = _settingsService.Current.HistoryRetentionDays;
        if (retentionDays <= 0 || _lastCleanupDate == DateTime.Today)
        {
            return;
        }

        Directory.CreateDirectory(HistoryDirectory);
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        foreach (var path in Directory.EnumerateFiles(HistoryDirectory, "*.*", System.IO.SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTime(path) < cutoff)
                {
                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        _lastCleanupDate = DateTime.Today;
    }

    public BitmapSource LoadFullImage(string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public string? FindLatestImagePath()
    {
        Directory.CreateDirectory(HistoryDirectory);
        return Directory
            .EnumerateFiles(HistoryDirectory, "*.*", System.IO.SearchOption.AllDirectories)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    public void OpenFile(string filePath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            UseShellExecute = true
        });
    }

    public void DeleteToRecycleBin(string filePath)
    {
        FileSystem.DeleteFile(
            filePath,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin);
    }

    public void OpenHistoryDirectory()
    {
        Directory.CreateDirectory(HistoryDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = HistoryDirectory,
            UseShellExecute = true
        });
    }

    private static HistoryItem? TryLoadItem(FileInfo file)
    {
        try
        {
            int width;
            int height;
            using (var imageInfo = Image.FromFile(file.FullName))
            {
                width = imageInfo.Width;
                height = imageInfo.Height;
            }

            using var stream = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var thumbnail = new BitmapImage();
            thumbnail.BeginInit();
            thumbnail.CacheOption = BitmapCacheOption.OnLoad;
            thumbnail.DecodePixelWidth = 360;
            thumbnail.StreamSource = stream;
            thumbnail.EndInit();
            thumbnail.Freeze();

            return new HistoryItem(
                file.FullName,
                file.Name,
                file.LastWriteTime,
                width,
                height,
                file.Length,
                thumbnail);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }
}
