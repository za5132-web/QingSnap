using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

internal sealed class PinHistoryBuffer
{
    public const int Capacity = 5;

    private readonly List<PinHistoryItem> _items = [];
    private int _currentIndex = -1;

    public int Count => _items.Count;

    public void AddCapture(
        BitmapSource image,
        string imagePath,
        CaptureRegion region) =>
        AddOrPromote(new PinHistoryItem(
            PinImageFingerprint.Create(image),
            imagePath,
            region,
            imagePath,
            null,
            false));

    public void AddClipboard(ClipboardImageContent content) =>
        AddOrPromote(new PinHistoryItem(
            PinImageFingerprint.Create(content.Image),
            content.SourceName,
            content.PreferredRegion,
            null,
            content.Image,
            content.PreferredRegion is null));

    public void AddSavedImage(BitmapSource image, string imagePath, CaptureRegion? region) =>
        AddOrPromote(new PinHistoryItem(
            PinImageFingerprint.Create(image),
            imagePath,
            region,
            imagePath,
            null,
            false));

    public PinHistoryItem? SelectLatest()
    {
        if (_items.Count == 0)
        {
            return null;
        }

        _currentIndex = 0;
        return _items[0];
    }

    public PinHistoryItem? SelectNext()
    {
        if (_items.Count == 0)
        {
            return null;
        }

        _currentIndex = (_currentIndex + 1 + _items.Count) % _items.Count;
        return _items[_currentIndex];
    }

    private void AddOrPromote(PinHistoryItem item)
    {
        var existingIndex = _items.FindIndex(candidate => candidate.Fingerprint == item.Fingerprint);
        if (existingIndex < 0 &&
            item.PreferredRegion is not null &&
            _items.FirstOrDefault() is { PreferredRegion: not null } latest &&
            latest.PreferredRegion == item.PreferredRegion &&
            latest.Fingerprint.Width == item.Fingerprint.Width &&
            latest.Fingerprint.Height == item.Fingerprint.Height)
        {
            existingIndex = 0;
        }

        if (existingIndex >= 0)
        {
            var existing = _items[existingIndex];
            _items.RemoveAt(existingIndex);
            var imagePath = item.ImagePath ?? existing.ImagePath;
            item = item with
            {
                ImagePath = imagePath,
                Image = imagePath is null ? item.Image ?? existing.Image : null,
                PreferredRegion = item.UseCursorPosition
                    ? null
                    : item.PreferredRegion ?? existing.PreferredRegion
            };
        }

        _items.Insert(0, item);
        if (_items.Count > Capacity)
        {
            _items.RemoveRange(Capacity, _items.Count - Capacity);
        }

        _currentIndex = -1;
    }
}

internal sealed record PinHistoryItem(
    PinImageFingerprint Fingerprint,
    string SourceName,
    CaptureRegion? PreferredRegion,
    string? ImagePath,
    BitmapSource? Image,
    bool UseCursorPosition);

internal readonly record struct PinImageFingerprint(ulong Hash, int Width, int Height)
{
    public static PinImageFingerprint Create(BitmapSource source)
    {
        const int sampleWidth = 32;
        const int sampleHeight = 32;
        var scaled = new TransformedBitmap(
            source,
            new ScaleTransform(
                sampleWidth / (double)Math.Max(1, source.PixelWidth),
                sampleHeight / (double)Math.Max(1, source.PixelHeight)));
        var pixels = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        const int stride = sampleWidth * 4;
        var samples = new byte[stride * sampleHeight];
        pixels.CopyPixels(samples, stride, 0);

        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var sample in samples)
        {
            hash = (hash ^ sample) * prime;
        }

        hash = (hash ^ (uint)source.PixelWidth) * prime;
        hash = (hash ^ (uint)source.PixelHeight) * prime;
        return new PinImageFingerprint(hash, source.PixelWidth, source.PixelHeight);
    }
}
