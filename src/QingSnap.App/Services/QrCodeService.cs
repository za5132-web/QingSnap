using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using ZXing;
using ZXing.Common;

namespace QingSnap.App.Services;

public sealed class QrCodeService
{
    private const int PreferredTilePixels = 2200;
    private const int TileOverlapPixels = 220;
    private const int MaximumTileCount = 64;
    private const double DuplicateDistancePixels = 180;

    private static readonly BarcodeFormat[] SupportedFormats =
    [
        BarcodeFormat.QR_CODE,
        BarcodeFormat.DATA_MATRIX,
        BarcodeFormat.AZTEC,
        BarcodeFormat.PDF_417
    ];

    public Task<IReadOnlyList<QrCodeResult>> RecognizeAsync(
        BitmapSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var frozenSource = FreezeForBackgroundUse(source);
        return Task.Run(() => RecognizeCore(frozenSource, cancellationToken), cancellationToken);
    }

    internal IReadOnlyList<QrCodeResult> RecognizeCore(
        BitmapSource source,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<QrCodeResult>();
        foreach (var tile in CreateTiles(source.PixelWidth, source.PixelHeight))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DecodeTile(source, tile, candidates);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                DiagnosticLog.Warning(
                    "QrCode",
                    $"二维码分块识别失败，已跳过当前区域 {tile.X},{tile.Y},{tile.Width}x{tile.Height}：{exception.Message}");
            }
        }

        return candidates
            .OrderBy(result => result.CenterY)
            .ThenBy(result => result.CenterX)
            .ToArray();
    }

    private static BitmapSource FreezeForBackgroundUse(BitmapSource source)
    {
        if (source.IsFrozen)
        {
            return source;
        }

        var clone = source.CloneCurrentValue();
        clone.Freeze();
        return clone;
    }

    private static IReadOnlyList<Int32Rect> CreateTiles(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        if (width <= PreferredTilePixels && height <= PreferredTilePixels)
        {
            return [new Int32Rect(0, 0, width, height)];
        }

        var tileSize = PreferredTilePixels;
        while (EstimateTileCount(width, height, tileSize) > MaximumTileCount)
        {
            tileSize += 400;
        }

        var xPositions = CreateTilePositions(width, tileSize);
        var yPositions = CreateTilePositions(height, tileSize);
        var tiles = new List<Int32Rect>(Math.Min(MaximumTileCount, xPositions.Count * yPositions.Count));
        foreach (var y in yPositions)
        {
            foreach (var x in xPositions)
            {
                tiles.Add(new Int32Rect(
                    x,
                    y,
                    Math.Min(tileSize, width - x),
                    Math.Min(tileSize, height - y)));
            }
        }

        return tiles;
    }

    private static int EstimateTileCount(int width, int height, int tileSize) =>
        CreateTilePositions(width, tileSize).Count * CreateTilePositions(height, tileSize).Count;

    private static IReadOnlyList<int> CreateTilePositions(int extent, int tileSize)
    {
        if (extent <= tileSize)
        {
            return [0];
        }

        var step = Math.Max(1, tileSize - TileOverlapPixels);
        var positions = new List<int>();
        for (var position = 0; position < extent; position += step)
        {
            var clamped = Math.Min(position, extent - tileSize);
            if (positions.Count == 0 || positions[^1] != clamped)
            {
                positions.Add(clamped);
            }

            if (clamped + tileSize >= extent)
            {
                break;
            }
        }

        return positions;
    }

    private static void DecodeTile(
        BitmapSource source,
        Int32Rect tileRect,
        ICollection<QrCodeResult> collected)
    {
        BitmapSource tile = tileRect.X == 0 &&
                            tileRect.Y == 0 &&
                            tileRect.Width == source.PixelWidth &&
                            tileRect.Height == source.PixelHeight
            ? source
            : new CroppedBitmap(source, tileRect);

        var scale = Math.Min(
            1D,
            Math.Min(
                PreferredTilePixels / (double)tile.PixelWidth,
                PreferredTilePixels / (double)tile.PixelHeight));
        if (scale < 0.999D)
        {
            tile = new TransformedBitmap(tile, new ScaleTransform(scale, scale));
        }

        if (tile.CanFreeze && !tile.IsFrozen)
        {
            tile.Freeze();
        }

        var bgra = tile.Format == PixelFormats.Bgra32
            ? tile
            : new FormatConvertedBitmap(tile, PixelFormats.Bgra32, null, 0);
        var stride = checked(bgra.PixelWidth * 4);
        var pixels = new byte[checked(stride * bgra.PixelHeight)];
        bgra.CopyPixels(pixels, stride, 0);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = SupportedFormats
            }
        };
        var results = reader.DecodeMultiple(
            pixels,
            bgra.PixelWidth,
            bgra.PixelHeight,
            RGBLuminanceSource.BitmapFormat.BGRA32);
        if (results is null)
        {
            return;
        }

        foreach (var result in results)
        {
            if (string.IsNullOrEmpty(result.Text))
            {
                continue;
            }

            var points = result.ResultPoints;
            var localCenterX = points is { Length: > 0 }
                ? points.Average(point => point.X)
                : bgra.PixelWidth / 2D;
            var localCenterY = points is { Length: > 0 }
                ? points.Average(point => point.Y)
                : bgra.PixelHeight / 2D;
            var candidate = new QrCodeResult(
                result.Text,
                GetFormatDisplayName(result.BarcodeFormat),
                tileRect.X + localCenterX / scale,
                tileRect.Y + localCenterY / scale);
            if (!IsDuplicate(collected, candidate))
            {
                collected.Add(candidate);
            }
        }
    }

    private static bool IsDuplicate(IEnumerable<QrCodeResult> existingResults, QrCodeResult candidate)
    {
        foreach (var existing in existingResults)
        {
            if (!string.Equals(existing.Text, candidate.Text, StringComparison.Ordinal) ||
                !string.Equals(existing.Format, candidate.Format, StringComparison.Ordinal))
            {
                continue;
            }

            var deltaX = existing.CenterX - candidate.CenterX;
            var deltaY = existing.CenterY - candidate.CenterY;
            if ((deltaX * deltaX) + (deltaY * deltaY) <= DuplicateDistancePixels * DuplicateDistancePixels)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetFormatDisplayName(BarcodeFormat format) => format switch
    {
        BarcodeFormat.QR_CODE => "QR Code",
        BarcodeFormat.DATA_MATRIX => "Data Matrix",
        BarcodeFormat.AZTEC => "Aztec",
        BarcodeFormat.PDF_417 => "PDF417",
        _ => format.ToString()
    };
}
