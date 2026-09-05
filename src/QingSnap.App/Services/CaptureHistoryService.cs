using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.VisualBasic.FileIO;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class CaptureHistoryService : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp"
    };

    private readonly AppSettingsService _settingsService;
    private readonly HistoryMetadataStore _metadataStore;
    private readonly CancellationTokenSource _migrationCancellation = new();
    private readonly SemaphoreSlim _migrationGate = new(1, 1);
    private readonly object _migrationTaskSync = new();
    private Task _migrationTask = Task.CompletedTask;
    private bool _migrationRunning;
    private bool _migrationRequested;
    private DateTime _lastCleanupDate;
    private readonly object _favoritesSync = new();
    private readonly object _searchIndexSync = new();
    private bool _disposed;

    public CaptureHistoryService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        Directory.CreateDirectory(HistoryDirectory);
        _metadataStore = new HistoryMetadataStore(settingsService.DataDirectory);
        _settingsService.SettingsChanged += OnSettingsChanged;
        ScheduleLegacyMigration();
    }

    public string HistoryDirectory => _settingsService.Current.HistoryDirectory;

    public string MetadataDatabasePath => _metadataStore.DatabasePath;

    public event EventHandler? MetadataIndexRefreshed;

    private string FavoritesPath => Path.Combine(HistoryDirectory, ".qingsnap-favorites.json");

    private string SearchIndexDirectory => Path.Combine(HistoryDirectory, ".qingsnap-index");

    public string Save(BitmapSource image, CaptureHistoryContext? context = null)
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

        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        var file = new FileInfo(path);
        var metadata = CreateMetadata(
            file,
            image.PixelWidth,
            image.PixelHeight,
            false,
            string.Empty,
            HistoryOcrIndexState.NotIndexed,
            context);
        _metadataStore.QueueUpsert(metadata);
        _ = PopulateImageHashAsync(metadata, _migrationCancellation.Token);
        return path;
    }

    public HistorySnapshot LoadSnapshot(int maximumItems, CancellationToken cancellationToken)
    {
        CleanupExpiredItems();
        Directory.CreateDirectory(HistoryDirectory);

        var files = EnumerateImageFiles().ToArray();
        var existingPaths = files
            .Select(file => file.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _metadataStore.QueueRemoveMissing(existingPaths);

        IReadOnlyDictionary<string, HistoryMetadata> metadataByPath;
        IReadOnlyDictionary<string, IReadOnlyList<string>> tagsByPath;
        try
        {
            metadataByPath = _metadataStore
                .LoadByPathsAsync(existingPaths, cancellationToken)
                .GetAwaiter()
                .GetResult();
            tagsByPath = _metadataStore
                .LoadTagsByPathsAsync(existingPaths, cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DiagnosticLog.Error("HistoryMetadata", exception, "读取截图 Metadata 失败，已回退到图片目录扫描。");
            metadataByPath = new Dictionary<string, HistoryMetadata>(StringComparer.OrdinalIgnoreCase);
            tagsByPath = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }

        HashSet<string> favorites;
        lock (_favoritesSync)
        {
            favorites = LoadFavorites();
        }

        var items = new List<HistoryItem>(Math.Min(files.Length, maximumItems));
        foreach (var file in files.OrderByDescending(file => file.LastWriteTimeUtc).Take(maximumItems))
        {
            cancellationToken.ThrowIfCancellationRequested();
            metadataByPath.TryGetValue(file.FullName, out var metadata);
            tagsByPath.TryGetValue(file.FullName, out var tags);
            var item = TryLoadItem(file, favorites, metadata, tags ?? []);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        if (items.Any(item => item.MetadataId <= 0))
        {
            try
            {
                // Newly discovered legacy files are queued above.  Wait for that one-time
                // import batch before exposing the snapshot so every selectable card has
                // a durable database identity from its first appearance.
                _metadataStore
                    .FlushAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                var refreshedMetadata = _metadataStore
                    .LoadByPathsAsync(items.Select(item => item.FilePath), cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                items = items
                    .Select(item => refreshedMetadata.TryGetValue(item.FilePath, out var value)
                        ? item with { MetadataId = value.Id }
                        : item)
                    .ToList();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                DiagnosticLog.Error("HistoryMetadata", exception, "补齐历史记录 Metadata Id 失败。");
            }
        }

        return new HistorySnapshot(files.Length, files.Sum(file => file.Length), items);
    }


    public async Task<HistoryItemPage> QueryHistoryAsync(
        HistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(CleanupExpiredItems, cancellationToken).ConfigureAwait(false);
        var page = await _metadataStore.QueryHistoryAsync(query, cancellationToken).ConfigureAwait(false);
        var items = page.Items.Select(metadata => new HistoryItem(
            metadata.FilePath,
            Path.GetFileName(metadata.FilePath),
            metadata.CaptureTime.LocalDateTime,
            metadata.Width,
            metadata.Height,
            metadata.FileSize,
            Thumbnail: null,
            metadata.IsFavorite,
            SearchText: string.Empty,
            metadata.SourceProcess,
            metadata.SourceWindowTitle,
            metadata.Tags,
            metadata.Id,
            metadata.IsLongCapture)).ToArray();
        return new HistoryItemPage(items, page.TotalCount, page.TotalBytes, page.HasMore);
    }

    public Task RemoveMissingMetadataAsync(string filePath, CancellationToken cancellationToken = default) =>
        _metadataStore.DeleteAsync(filePath, cancellationToken);

    private void CleanupExpiredItems()
    {
        var retentionDays = _settingsService.Current.HistoryRetentionDays;
        if (retentionDays <= 0 || _lastCleanupDate == DateTime.Today)
        {
            return;
        }

        Directory.CreateDirectory(HistoryDirectory);
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        foreach (var file in EnumerateImageFiles())
        {
            try
            {
                if (file.LastWriteTime < cutoff)
                {
                    FileSystem.DeleteFile(file.FullName, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    DeleteMetadata(file.FullName);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warning("History", $"清理过期截图失败 {file.Name}：{exception.Message}");
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

    public string? FindLatestImagePath() => FindRecentImagePaths(1).FirstOrDefault();

    public IReadOnlyList<string> FindRecentImagePaths(int maximumItems)
    {
        if (maximumItems <= 0)
        {
            return [];
        }

        Directory.CreateDirectory(HistoryDirectory);
        return EnumerateImageFiles()
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .Take(maximumItems)
            .ToArray();
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
        FileSystem.DeleteFile(filePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        SetFavorite(filePath, false);
        var indexPath = GetSearchIndexPath(filePath);
        if (File.Exists(indexPath))
        {
            File.Delete(indexPath);
        }

        DeleteMetadata(filePath);
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
            _metadataStore.QueueFavorite(filePath, isFavorite);
            return isFavorite;
        }
    }

    public void SetFavoriteState(IEnumerable<string> filePaths, bool isFavorite)
    {
        var paths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        lock (_favoritesSync)
        {
            var favorites = LoadFavorites();
            foreach (var filePath in paths)
            {
                var relativePath = ToRelativeHistoryPath(filePath);
                if (isFavorite)
                {
                    favorites.Add(relativePath);
                }
                else
                {
                    favorites.Remove(relativePath);
                }

                _metadataStore.QueueFavorite(filePath, isFavorite);
            }

            SaveFavorites(favorites);
        }
    }

    public void SaveOcrText(string filePath, string text)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        lock (_searchIndexSync)
        {
            Directory.CreateDirectory(SearchIndexDirectory);
            var indexPath = GetSearchIndexPath(filePath);
            var temporaryPath = indexPath + ".tmp";
            File.WriteAllText(temporaryPath, text.Trim(), new UTF8Encoding(false));
            File.Move(temporaryPath, indexPath, true);
        }

        _metadataStore.QueueOcrText(filePath, text);
    }

    public bool HasOcrIndex(string filePath)
    {
        lock (_searchIndexSync)
        {
            if (File.Exists(GetSearchIndexPath(filePath)))
            {
                return true;
            }
        }

        try
        {
            var metadata = _metadataStore.LoadByPathsAsync([filePath]).GetAwaiter().GetResult();
            return metadata.TryGetValue(Path.GetFullPath(filePath), out var value) &&
                   value.OcrIndexState == HistoryOcrIndexState.Indexed;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("HistoryMetadata", exception, "检查 OCR Metadata 状态失败。");
            return false;
        }
    }

    public IReadOnlyList<string> FindImagesWithoutOcrIndex(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(HistoryDirectory);
        var files = EnumerateImageFiles()
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        IReadOnlyDictionary<string, HistoryMetadata> metadataByPath;
        try
        {
            metadataByPath = _metadataStore
                .LoadByPathsAsync(files.Select(file => file.FullName), cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DiagnosticLog.Error("HistoryMetadata", exception, "读取 OCR Metadata 状态失败，已使用旧索引检查。");
            metadataByPath = new Dictionary<string, HistoryMetadata>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var legacyIndexed = File.Exists(GetSearchIndexPath(file.FullName));
            var databaseIndexed = metadataByPath.TryGetValue(file.FullName, out var metadata) &&
                                  metadata.OcrIndexState == HistoryOcrIndexState.Indexed;
            if (!legacyIndexed && !databaseIndexed)
            {
                result.Add(file.FullName);
            }
        }

        return result;
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

    public Task<IReadOnlyList<string>> LoadAllTagsAsync(CancellationToken cancellationToken = default) =>
        _metadataStore.LoadAllTagsAsync(cancellationToken);

    public async Task AddTagsAsync(
        string filePath,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("截图文件已经不存在。", filePath);
        }

        await _metadataStore.AddTagsAsync(filePath, tags, cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveTagAsync(
        string filePath,
        string tag,
        CancellationToken cancellationToken = default) =>
        _metadataStore.RemoveTagAsync(filePath, tag, cancellationToken);

    private HistoryItem? TryLoadItem(
        FileInfo file,
        IReadOnlySet<string> legacyFavorites,
        HistoryMetadata? metadata,
        IReadOnlyList<string> tags)
    {
        try
        {
            int width;
            int height;
            if (metadata is not null && metadata.FileSize == file.Length)
            {
                width = metadata.Width;
                height = metadata.Height;
            }
            else
            {
                using var imageInfo = Image.FromFile(file.FullName);
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

            var legacyFavorite = legacyFavorites.Contains(ToRelativeHistoryPath(file.FullName));
            var legacyIndexExists = File.Exists(GetSearchIndexPath(file.FullName));
            var searchText = metadata?.OcrIndexState == HistoryOcrIndexState.Indexed
                ? metadata.OcrText
                : LoadOcrText(file.FullName);
            var isFavorite = metadata?.IsFavorite == true || legacyFavorite;
            if (metadata is null || metadata.FileSize != file.Length ||
                (legacyFavorite && !metadata.IsFavorite) ||
                (legacyIndexExists && metadata.OcrIndexState != HistoryOcrIndexState.Indexed))
            {
                var migrated = CreateMetadata(
                    file,
                    width,
                    height,
                    isFavorite,
                    searchText,
                    legacyIndexExists ? HistoryOcrIndexState.Indexed : HistoryOcrIndexState.NotIndexed,
                    context: null,
                    existing: metadata);
                _metadataStore.QueueUpsert(migrated);
                if (string.IsNullOrWhiteSpace(migrated.ImageHash))
                {
                    _ = PopulateImageHashAsync(migrated, _migrationCancellation.Token);
                }
            }

            return new HistoryItem(
                file.FullName,
                file.Name,
                metadata?.CaptureTime.LocalDateTime ?? file.LastWriteTime,
                width,
                height,
                file.Length,
                thumbnail,
                isFavorite,
                searchText,
                metadata?.SourceProcess,
                metadata?.SourceWindowTitle,
                tags,
                metadata?.Id ?? 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            DiagnosticLog.Warning("History", $"无法载入历史截图 {file.Name}：{exception.Message}");
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
            _metadataStore.QueueFavorite(filePath, favorite);
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

    private IEnumerable<FileInfo> EnumerateImageFiles() =>
        Directory
            .EnumerateFiles(HistoryDirectory, "*.*", System.IO.SearchOption.AllDirectories)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Select(path => new FileInfo(path));

    private static HistoryMetadata CreateMetadata(
        FileInfo file,
        int width,
        int height,
        bool isFavorite,
        string ocrText,
        HistoryOcrIndexState ocrIndexState,
        CaptureHistoryContext? context,
        HistoryMetadata? existing = null)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new HistoryMetadata(
            existing?.Id ?? 0,
            file.FullName,
            existing?.CaptureTime ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
            width,
            height,
            file.Length,
            NormalizeFormat(file.Extension),
            context?.IsLongCapture ?? existing?.IsLongCapture ?? IsProbablyLongCapture(width, height),
            isFavorite,
            ocrText,
            ocrIndexState,
            context?.SourceProcess ?? existing?.SourceProcess,
            context?.SourceWindowTitle ?? existing?.SourceWindowTitle,
            context?.MonitorId ?? existing?.MonitorId,
            context?.MonitorDeviceName ?? existing?.MonitorDeviceName,
            context?.CaptureX ?? existing?.CaptureX,
            context?.CaptureY ?? existing?.CaptureY,
            context?.CaptureWidth ?? existing?.CaptureWidth,
            context?.CaptureHeight ?? existing?.CaptureHeight,
            existing?.ImageHash,
            existing?.CreatedAt ?? timestamp,
            timestamp);
    }

    private async Task PopulateImageHashAsync(HistoryMetadata metadata, CancellationToken cancellationToken)
    {
        try
        {
            var hash = await ComputeImageHashAsync(metadata.FilePath, cancellationToken).ConfigureAwait(false);
            _metadataStore.QueueImageHash(metadata.FilePath, hash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            DiagnosticLog.Warning("HistoryMetadata", $"计算图片哈希失败 {Path.GetFileName(metadata.FilePath)}：{exception.Message}");
        }
    }

    private static async Task<string> ComputeImageHashAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private void ScheduleLegacyMigration()
    {
        if (_disposed)
        {
            return;
        }

        lock (_migrationTaskSync)
        {
            _migrationRequested = true;
            if (_migrationRunning)
            {
                return;
            }

            _migrationRunning = true;
            _migrationTask = Task.Run(() => RunMigrationPassesAsync(_migrationCancellation.Token));
        }
    }

    private async Task RunMigrationPassesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_migrationTaskSync)
            {
                _migrationRequested = false;
            }

            await ImportLegacyHistoryAsync(cancellationToken).ConfigureAwait(false);

            lock (_migrationTaskSync)
            {
                if (!_migrationRequested || cancellationToken.IsCancellationRequested)
                {
                    _migrationRunning = false;
                    return;
                }
            }
        }
    }

    private async Task ImportLegacyHistoryAsync(CancellationToken cancellationToken)
    {
        if (!await _migrationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(HistoryDirectory);
            HashSet<string> favorites;
            lock (_favoritesSync)
            {
                favorites = LoadFavorites();
            }

            var files = EnumerateImageFiles().ToArray();
            var existingPaths = files
                .Select(file => file.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var migrationIndex = await _metadataStore
                .LoadMigrationIndexAsync(cancellationToken)
                .ConfigureAwait(false);
            var existingMetadata = migrationIndex
                .Where(metadata => existingPaths.Contains(metadata.FilePath))
                .ToDictionary(metadata => metadata.FilePath, StringComparer.OrdinalIgnoreCase);
            var metadataByHash = migrationIndex
                .Where(metadata => !string.IsNullOrWhiteSpace(metadata.ImageHash))
                .GroupBy(metadata => metadata.ImageHash!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase);
            var imported = 0;
            foreach (var file in files.OrderByDescending(file => file.LastWriteTimeUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                existingMetadata.TryGetValue(file.FullName, out var existing);
                if (existing is not null && existing.FileSize == file.Length &&
                    !string.IsNullOrWhiteSpace(existing.ImageHash))
                {
                    continue;
                }

                try
                {
                    int width;
                    int height;
                    using (var image = Image.FromFile(file.FullName))
                    {
                        width = image.Width;
                        height = image.Height;
                    }

                    var imageHash = await ComputeImageHashAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                    HistoryMetadata? inherited = null;
                    var inheritedPath = existing?.FilePath;
                    if (inheritedPath is null && metadataByHash.TryGetValue(imageHash, out var movedMetadata))
                    {
                        inheritedPath = movedMetadata.FilePath;
                    }

                    if (inheritedPath is not null)
                    {
                        var inheritedLookup = await _metadataStore
                            .LoadByPathsAsync([inheritedPath], cancellationToken)
                            .ConfigureAwait(false);
                        inheritedLookup.TryGetValue(inheritedPath, out inherited);
                    }

                    var indexExists = File.Exists(GetSearchIndexPath(file.FullName));
                    var metadata = CreateMetadata(
                        file,
                        width,
                        height,
                        favorites.Contains(ToRelativeHistoryPath(file.FullName)) || inherited?.IsFavorite == true,
                        indexExists ? LoadOcrText(file.FullName) : inherited?.OcrText ?? string.Empty,
                        indexExists ? HistoryOcrIndexState.Indexed : inherited?.OcrIndexState ?? HistoryOcrIndexState.NotIndexed,
                        context: null,
                        inherited) with { ImageHash = imageHash };
                    _metadataStore.QueueUpsert(metadata);
                    if (inherited is not null &&
                        !string.Equals(inherited.FilePath, file.FullName, StringComparison.OrdinalIgnoreCase) &&
                        (await _metadataStore.LoadTagsByPathsAsync([inherited.FilePath], cancellationToken).ConfigureAwait(false))
                        .TryGetValue(inherited.FilePath, out var inheritedTags) && inheritedTags.Count > 0)
                    {
                        await _metadataStore.AddTagsAsync(file.FullName, inheritedTags, cancellationToken).ConfigureAwait(false);
                    }

                    imported++;
                    if (imported % 20 == 0)
                    {
                        await Task.Yield();
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
                {
                    DiagnosticLog.Warning("HistoryMetadata", $"迁移历史截图失败 {file.Name}：{exception.Message}");
                }
            }

            _metadataStore.QueueRemoveMissing(existingPaths);
            await _metadataStore.FlushAsync(cancellationToken).ConfigureAwait(false);
            DiagnosticLog.Info("HistoryMetadata", $"历史 Metadata 后台同步完成：扫描 {files.Length} 张，更新 {imported} 张。");
            MetadataIndexRefreshed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("HistoryMetadata", exception, "历史 Metadata 后台迁移失败。");
        }
        finally
        {
            _migrationGate.Release();
        }
    }

    private void DeleteMetadata(string filePath)
    {
        try
        {
            _metadataStore.DeleteAsync(filePath).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("HistoryMetadata", exception, $"删除 Metadata 失败 {Path.GetFileName(filePath)}；下次刷新将自动清理。");
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _lastCleanupDate = default;
        ScheduleLegacyMigration();
    }

    internal Task WaitForMigrationAsync()
    {
        lock (_migrationTaskSync)
        {
            return _migrationTask;
        }
    }

    private static bool IsProbablyLongCapture(int width, int height) =>
        height / (double)Math.Max(1, width) >= 2.15;

    private static string NormalizeFormat(string extension) => extension.TrimStart('.').ToUpperInvariant() switch
    {
        "JPEG" => "JPG",
        var value => value
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _migrationCancellation.Cancel();
        Task migrationTask;
        lock (_migrationTaskSync)
        {
            migrationTask = _migrationTask;
        }

        try
        {
            migrationTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is AggregateException or OperationCanceledException)
        {
            DiagnosticLog.Warning("HistoryMetadata", $"停止 Metadata 迁移任务时发生异常：{exception.Message}");
        }

        _metadataStore.Dispose();
        if (migrationTask.IsCompleted)
        {
            _migrationGate.Dispose();
        }
        _migrationCancellation.Dispose();
    }
}
