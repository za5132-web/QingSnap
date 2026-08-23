using System.Windows;
using System.Windows.Media.Imaging;
using DrawingRectangle = System.Drawing.Rectangle;

namespace QingSnap.App.Models;

public sealed record ScreenSnapshot(DrawingRectangle Bounds, BitmapSource Image)
{
    public BitmapSource Crop(DrawingRectangle globalRegion)
    {
        var localRegion = new Int32Rect(
            globalRegion.X - Bounds.X,
            globalRegion.Y - Bounds.Y,
            globalRegion.Width,
            globalRegion.Height);

        if (localRegion.X < 0 || localRegion.Y < 0 ||
            localRegion.X + localRegion.Width > Image.PixelWidth ||
            localRegion.Y + localRegion.Height > Image.PixelHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(globalRegion), "截图范围超出当前屏幕快照。");
        }

        var cropped = new CroppedBitmap(Image, localRegion);
        cropped.Freeze();
        return cropped;
    }
}
