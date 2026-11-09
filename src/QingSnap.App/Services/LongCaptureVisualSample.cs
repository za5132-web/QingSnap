using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingSnap.App.Services;

// A bounded, screen-position-preserving sample for settling and rollback checks.
// Full-resolution pixels and the existing confidence rules still decide seams.
internal sealed class LongCaptureVisualSample
{
    private const int MaximumColumns = 128;
    private const int MaximumRows = 96;
    private readonly byte[] _luminance;
    private readonly PixelRegion _region;

    private LongCaptureVisualSample(byte[] luminance, PixelRegion region)
    {
        _luminance = luminance;
        _region = region;
    }

    internal int SampleCount => _luminance.Length;

    public static LongCaptureVisualSample Create(BitmapSource source, PixelRegion? region = null)
    {
        var bounds = region ?? PixelRegion.Full(source.PixelWidth, source.PixelHeight);
        if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > source.PixelWidth ||
            bounds.Bottom > source.PixelHeight || bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        BitmapSource pixels = source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Bgra32 ||
                              source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var columns = Math.Min(MaximumColumns, bounds.Width);
        var rows = Math.Min(MaximumRows, bounds.Height);
        var luminance = new byte[columns * rows];
        var rowBuffer = new byte[checked(bounds.Width * 4)];
        for (var row = 0; row < rows; row++)
        {
            var y = bounds.Top + (int)((row + 0.5) * bounds.Height / rows);
            pixels.CopyPixels(new Int32Rect(bounds.Left, y, bounds.Width, 1), rowBuffer, rowBuffer.Length, 0);
            for (var column = 0; column < columns; column++)
            {
                var x = (int)((column + 0.5) * bounds.Width / columns) * 4;
                luminance[row * columns + column] =
                    (byte)((rowBuffer[x] * 29 + rowBuffer[x + 1] * 150 + rowBuffer[x + 2] * 77) >> 8);
            }
        }

        return new LongCaptureVisualSample(luminance, bounds);
    }

    public double CompareTo(LongCaptureVisualSample other)
    {
        if (_region != other._region || _luminance.Length != other._luminance.Length)
        {
            return double.PositiveInfinity;
        }

        long difference = 0;
        for (var index = 0; index < _luminance.Length; index++)
        {
            difference += Math.Abs(_luminance[index] - other._luminance[index]);
        }

        return difference / (double)_luminance.Length;
    }

    public bool MatchesAnchor(LongCaptureVisualSample other)
    {
        if (CompareTo(other) > 2.2)
        {
            return false;
        }

        // A mostly blank page must not hide a displaced text band in the mean.
        // This is a conservative rollback gate, never proof that two blank pages
        // represent the same document position; those remain ambiguous to seams.
        var changed = 0;
        for (var index = 0; index < _luminance.Length; index++)
        {
            if (Math.Abs(_luminance[index] - other._luminance[index]) >= 8)
            {
                changed++;
            }
        }

        return changed <= _luminance.Length * 0.035;
    }
}
