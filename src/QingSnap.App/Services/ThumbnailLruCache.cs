using System.IO;
using System.Windows.Media.Imaging;

namespace QingSnap.App.Services;

internal sealed class ThumbnailLruCache : IDisposable
{
    public const int DefaultCapacity = 150;
    public const int DefaultDecodePixelWidth = 280;

    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Dictionary<ThumbnailCacheKey, LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _lru = [];
    private readonly Dictionary<ThumbnailCacheKey, Task<BitmapSource?>> _inflight = [];
    private bool _disposed;

    public ThumbnailLruCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public async Task<BitmapSource?> GetAsync(
        string filePath,
        int decodePixelWidth = DefaultDecodePixelWidth,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            return null;
        }

        var key = new ThumbnailCacheKey(
            Path.GetFullPath(filePath),
            info.LastWriteTimeUtc.Ticks,
            Math.Max(32, decodePixelWidth));
        Task<BitmapSource?> loadTask;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Bitmap;
            }

            if (!_inflight.TryGetValue(key, out loadTask!))
            {
                loadTask = Task.Run(() => Decode(key), CancellationToken.None);
                _inflight[key] = loadTask;
            }
        }

        var bitmap = await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _inflight.Remove(key);
            if (_disposed || bitmap is null)
            {
                return bitmap;
            }

            if (!_entries.ContainsKey(key))
            {
                var node = _lru.AddFirst(new CacheEntry(key, bitmap));
                _entries[key] = node;
                while (_entries.Count > _capacity && _lru.Last is { } oldest)
                {
                    _lru.RemoveLast();
                    _entries.Remove(oldest.Value.Key);
                }

                ResourceDiagnostics.SetGauge("Thumb", _entries.Count);
            }

            return bitmap;
        }
    }

    public void Remove(string filePath)
    {
        var normalized = Path.GetFullPath(filePath);
        lock (_sync)
        {
            foreach (var key in _entries.Keys
                         .Where(key => key.FilePath.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                var node = _entries[key];
                _entries.Remove(key);
                _lru.Remove(node);
            }

            ResourceDiagnostics.SetGauge("Thumb", _entries.Count);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _lru.Clear();
            ResourceDiagnostics.SetGauge("Thumb", 0);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _entries.Clear();
            _lru.Clear();
            _inflight.Clear();
            ResourceDiagnostics.RemoveGauge("Thumb");
        }
    }

    private static BitmapSource? Decode(ThumbnailCacheKey key)
    {
        try
        {
            using var stream = File.Open(key.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = key.DecodePixelWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DiagnosticLog.Warning("HistoryThumbnail", $"缩略图载入失败 {Path.GetFileName(key.FilePath)}：{exception.Message}");
            return null;
        }
    }

    private sealed record CacheEntry(ThumbnailCacheKey Key, BitmapSource Bitmap);

    private readonly record struct ThumbnailCacheKey(string FilePath, long LastWriteTicks, int DecodePixelWidth);
}
