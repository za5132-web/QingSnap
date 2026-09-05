namespace QingSnap.App.Services;

internal static class ScreenPixelMovement
{
    public static double ToDip(int pixelDistance, double surfaceDipLength, int sourcePixelLength)
    {
        if (pixelDistance == 0)
        {
            return 0;
        }

        if (!double.IsFinite(surfaceDipLength) || surfaceDipLength <= 0 || sourcePixelLength <= 0)
        {
            return pixelDistance;
        }

        return pixelDistance * surfaceDipLength / sourcePixelLength;
    }
}
