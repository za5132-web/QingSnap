using System.Drawing;
using System.Drawing.Imaging;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class ScreenImageConversionTests
{
    [Fact]
    public void ScreenPixelsWithZeroAlphaRemainOpaque()
    {
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, 1, 1),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { System.Runtime.InteropServices.Marshal.Copy(new byte[] { 17, 34, 51, 0 }, 0, data.Scan0, 4); }
        finally { bitmap.UnlockBits(data); }
        var image = ScreenCaptureService.CreateScreenImage(bitmap);
        var opaque = new System.Windows.Media.Imaging.FormatConvertedBitmap(image,
            System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[4];
        opaque.CopyPixels(pixels, 4, 0);
        Assert.Equal(new byte[] { 17, 34, 51, 255 }, pixels);
    }

    [Fact]
    public void ImageOwnsItsPixelsAfterBitmapIsDisposed()
    {
        System.Windows.Media.Imaging.BitmapSource image;
        using (var bitmap = new Bitmap(3, 2, PixelFormat.Format32bppPArgb))
        {
            bitmap.SetPixel(0, 0, Color.Red);
            bitmap.SetPixel(2, 1, Color.Blue);
            image = ScreenCaptureService.CreateScreenImage(bitmap);
        }
        Assert.True(image.IsFrozen);
        Assert.Equal(System.Windows.Media.PixelFormats.Bgr32, image.Format);
        var pixels = new byte[24];
        image.CopyPixels(pixels, 12, 0);
        Assert.Equal(255, pixels[2]);
        Assert.Equal(255, pixels[20]);
        Assert.Equal(3, image.PixelWidth);
        Assert.Equal(2, image.PixelHeight);
    }
}
