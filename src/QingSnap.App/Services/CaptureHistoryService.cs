using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
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
    private readonly object _favoritesSync = new();

    public CaptureHistoryService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        Directory.CreateDirectory(HistoryDirectory);
    }

    public string HistoryDirectory => _settingsService.Current.HistoryDirectory;

    private string FavoritesPath => Path.Combine(HistoryDirectory, ".qingsnap-favorites.json");

    private string SearchIndexDirectory => Path.Combine(HistoryDirectory, ".qingsnap-index");

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
        HashSet<string> favorites;
        lock (_favoritesSync)
        {
            favorites = LoadFavorites();
        }
        foreach (var file in files.OrderByDescending(file => file.LastWriteTime).Take(maximumItems))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = TryLoadItem(file, favorites);
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
        SetFavorite(filePath, false);
        var indexPath = GetSearchIndexPath(filePath);
        if (File.Exists(indexPath))
        {
            File.Delete(indexPath);
        }
    }

    public bool ToggleFavorite(string filePath)
    {
        lock (_favoritesSync)
        {
            var favorites = LoadFavorites();
            var relativePath = ToRelativeHistoryPath(filePath);
            var isFavorite = !favorites.Remove(relativePath);
            if (isFavorite)
            {
                favorites.Add(relativePath);
            }

            SaveFavorites(favorites);
            return isFavorite;
        }
    }

    public void SaveOcrText(string filePath, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Directory.CreateDirectory(SearchIndexDirectory);
        var indexPath = GetSearchIndexPath(filePath);
        var temporaryPath = indexPath + ".tmp";
        File.WriteAllText(temporaryPath, text.Trim(), new UTF8Encoding(false));
        File.Move(temporaryPath, indexPath, true);
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

    private HistoryItem? TryLoadItem(FileInfo file, HashSet<string> favorites)
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
            thumbnail.DecodePixelWidth = 260;
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
                thumbnail,
                favorites.Contains(ToRelativeHistoryPath(file.FullName)),
                LoadOcrText(file.FullName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    private void SetFavorite(string filePath, bool favorite)
    {
        lock (_favoritesSync)
        {
            var favorites = LoadFavorites();
            var relativePath = ToRelativeHistoryPath(filePath);
            if (favorite)
            {
                favorites.Add(relativePath);
            }
            else
            {
                favorites.Remove(relativePath);
            }

            SaveFavorites(favorites);
        }
    }

    private HashSet<string> LoadFavorites()
    {
        try
        {
            if (!File.Exists(FavoritesPath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FavoritesPath)) ?? [];
            return paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warning("History", $"Favorites index could not be read: {exception.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveFavorites(HashSet<string> favorites)
    {
        Directory.CreateDirectory(HistoryDirectory);
        var temporaryPath = FavoritesPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(favorites.OrderBy(path => path)));
        File.Move(temporaryPath, FavoritesPath, true);
    }

    private string ToRelativeHistoryPath(string filePath) =>
        Path.GetRelativePath(HistoryDirectory, filePath).Replace('\\', '/');

    private string LoadOcrText(string filePath)
    {
        try
        {
            var indexPath = GetSearchIndexPath(filePath);
            return File.Exists(indexPath) ? File.ReadAllText(indexPath) : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warning("History", $"OCR search index could not be read: {exception.Message}");
            return string.Empty;
        }
    }

    private string GetSearchIndexPath(string filePath)
    {
        var relativePath = ToRelativeHistoryPath(filePath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)));
        return Path.Combine(SearchIndexDirectory, hash + ".txt");
    }
}
