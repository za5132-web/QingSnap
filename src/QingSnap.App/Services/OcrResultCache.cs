using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

internal sealed class OcrResultCache
{
    private const int MaximumEntries = 24;
    private readonly object _sync = new();
    private readonly Dictionary<OcrImageFingerprint, CacheEntry> _entries = [];
    private readonly LinkedList<OcrImageFingerprint> _recent = [];

    public OcrImageFingerprint? CreateFingerprint(BitmapSource source)
    {
        try
        {
            const int sampleWidth = 64;
            const int sampleHeight = 64;
            var scaled = new TransformedBitmap(
                source,
                new ScaleTransform(
                    sampleWidth / (double)Math.Max(1, source.PixelWidth),
                    sampleHeight / (double)Math.Max(1, source.PixelHeight)));
            var grayscale = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
            var samples = new byte[sampleWidth * sampleHeight];
            grayscale.CopyPixels(samples, sampleWidth, 0);

            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var sample in samples)
            {
                hash = (hash ^ sample) * prime;
            }

            hash = (hash ^ (uint)source.PixelWidth) * prime;
            hash = (hash ^ (uint)source.PixelHeight) * prime;
            return new OcrImageFingerprint(hash, source.PixelWidth, source.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    public OcrRecognitionResult? TryGet(OcrImageFingerprint fingerprint, bool detailed)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(fingerprint, out var entry))
            {
                return null;
            }

            Touch(fingerprint, entry);
            return detailed ? entry.Detailed : entry.Basic ?? entry.Detailed;
        }
    }

    public void Set(OcrImageFingerprint fingerprint, bool detailed, OcrRecognitionResult result)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(fingerprint, out var entry))
            {
                entry = new CacheEntry();
                _entries.Add(fingerprint, entry);
            }

            if (detailed)
            {
                entry.Detailed = result;
            }
            else
            {
                entry.Basic = result;
            }

            Touch(fingerprint, entry);
            while (_entries.Count > MaximumEntries && _recent.First is { } oldest)
            {
                _recent.RemoveFirst();
                _entries.Remove(oldest.Value);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _recent.Clear();
        }
    }

    private void Touch(OcrImageFingerprint fingerprint, CacheEntry entry)
    {
        if (entry.RecentNode is not null)
        {
            _recent.Remove(entry.RecentNode);
        }

        entry.RecentNode = _recent.AddLast(fingerprint);
    }

    internal readonly record struct OcrImageFingerprint(ulong Hash, int Width, int Height);

    private sealed class CacheEntry
    {
        public OcrRecognitionResult? Basic { get; set; }

        public OcrRecognitionResult? Detailed { get; set; }

        public LinkedListNode<OcrImageFingerprint>? RecentNode { get; set; }
    }
}
