using DrawingRectangle = System.Drawing.Rectangle;

namespace QingSnap.App.Models;

public sealed record CaptureRegion(int X, int Y, int Width, int Height)
{
    public bool IsValid => Width > 0 && Height > 0;

    public DrawingRectangle ToRectangle() => new(X, Y, Width, Height);

    public static CaptureRegion FromRectangle(DrawingRectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
}
