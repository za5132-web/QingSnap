using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;
using ZXing;
using ZXing.Common;

namespace QingSnap.Tests;

public sealed class QrCodeServiceTests
{
    [Fact]
    public async Task RecognizeAsync_DecodesUrlWithoutOpeningIt()
    {
        const string url = "https://example.com/qingsnap?from=qr";
        var source = CreateQrCode(url, 360);

        var results = await new QrCodeService().RecognizeAsync(source);

        var result = Assert.Single(results);
        Assert.Equal(url, result.Text);
        Assert.True(result.IsUrl);
        Assert.Equal(Uri.UriSchemeHttps, result.SafeUrl!.Scheme);
    }

    [Fact]
    public async Task RecognizeAsync_FindsQrCodeNearBottomOfLongImage()
    {
        const string text = "滚滚长江东逝水 · QingSnap long capture";
        var qrCode = CreateQrCode(text, 360);
        var longImage = PlaceOnCanvas(qrCode, 900, 5200, 270, 4520);

        var results = await new QrCodeService().RecognizeAsync(longImage);

        Assert.Contains(results, result => result.Text == text);
    }

    [Fact]
    public async Task RecognizeAsync_ReturnsMultipleQrCodes()
    {
        var first = CreateQrCode("first-result", 300);
        var second = CreateQrCode("https://example.com/second", 300);
        var source = PlaceOnCanvas(
            760,
            380,
            (first, 30, 40),
            (second, 430, 40));

        var results = await new QrCodeService().RecognizeAsync(source);

        Assert.Contains(results, result => result.Text == "first-result");
        Assert.Contains(results, result => result.Text == "https://example.com/second");
    }

    [Fact]
    public void Result_OnlyTreatsHttpAndHttpsAsOpenableAndKeepsFullText()
    {
        var longText = new string('A', 900);
        var textResult = new QrCodeResult(longText, "QR Code");
        var unsafeUrlResult = new QrCodeResult("file:///C:/Windows/System32", "QR Code");

        Assert.False(textResult.IsUrl);
        Assert.True(textResult.DisplayText.Length < textResult.Text.Length);
        Assert.Equal(900, textResult.Text.Length);
        Assert.False(unsafeUrlResult.IsUrl);
    }

    private static BitmapSource CreateQrCode(string text, int size)
    {
        var matrix = new MultiFormatWriter().encode(
            text,
            BarcodeFormat.QR_CODE,
            size,
            size,
            new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.MARGIN] = 4,
                [EncodeHintType.CHARACTER_SET] = "UTF-8"
            });
        var stride = size * 4;
        var pixels = new byte[stride * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var offset = (y * stride) + (x * 4);
                var value = matrix[x, y] ? (byte)0 : (byte)255;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }

        var source = BitmapSource.Create(
            size,
            size,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        source.Freeze();
        return source;
    }

    private static BitmapSource PlaceOnCanvas(
        BitmapSource source,
        int width,
        int height,
        int left,
        int top) => PlaceOnCanvas(width, height, (source, left, top));

    private static BitmapSource PlaceOnCanvas(
        int width,
        int height,
        params (BitmapSource Source, int Left, int Top)[] placements)
    {
        var stride = width * 4;
        var canvas = new byte[stride * height];
        Array.Fill(canvas, (byte)255);
        foreach (var placement in placements)
        {
            var sourceStride = placement.Source.PixelWidth * 4;
            var sourcePixels = new byte[sourceStride * placement.Source.PixelHeight];
            placement.Source.CopyPixels(sourcePixels, sourceStride, 0);
            for (var y = 0; y < placement.Source.PixelHeight; y++)
            {
                Buffer.BlockCopy(
                    sourcePixels,
                    y * sourceStride,
                    canvas,
                    ((placement.Top + y) * stride) + (placement.Left * 4),
                    sourceStride);
            }
        }

        var result = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            canvas,
            stride);
        result.Freeze();
        return result;
    }
}
