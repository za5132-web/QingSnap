namespace QingSnap.App.Services;

internal static class MagnifierPixelGrid
{
    public const int Columns = 28;
    public const int Rows = 12;

    public static int MapPointerToPixel(double position, double surfaceLength, int pixelLength)
    {
        if (surfaceLength <= 0 || pixelLength <= 1)
        {
            return 0;
        }

        // A pointer coordinate represents a pixel boundary. Floor keeps the sampled
        // pixel on the lower/right side of that boundary instead of snapping the
        // crosshair to the nearest pixel centre.
        return Math.Clamp((int)Math.Floor(position * pixelLength / surfaceLength), 0, pixelLength - 1);
    }

    public static int GetCropOrigin(int pixel, int pixelLength, int cropLength)
    {
        return Math.Clamp(
            pixel - cropLength / 2,
            0,
            Math.Max(0, pixelLength - cropLength));
    }

    public static double GetBoundaryOffset(
        int pixel,
        int cropOrigin,
        int visiblePixelCount,
        double displayLength)
    {
        if (visiblePixelCount <= 0 || displayLength <= 0)
        {
            return 0;
        }

        return Math.Clamp(
            (pixel - cropOrigin) * displayLength / visiblePixelCount,
            0,
            displayLength);
    }
}
