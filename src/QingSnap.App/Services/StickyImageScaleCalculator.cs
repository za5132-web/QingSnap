namespace QingSnap.App.Services;

internal static class StickyImageScaleCalculator
{
    public const double MinimumWindowSize = 24;

    public static StickyImageScaleResult Calculate(
        double imageWidth,
        double imageHeight,
        double requestedScale,
        double minimumScale,
        double maximumScale,
        double scaleX = 1,
        double scaleY = 1,
        double borderX = 1,
        double borderY = 1)
    {
        imageWidth = Math.Max(1, imageWidth);
        imageHeight = Math.Max(1, imageHeight);
        scaleX = Math.Max(0.01, scaleX);
        scaleY = Math.Max(0.01, scaleY);
        borderX = Math.Max(0, borderX);
        borderY = Math.Max(0, borderY);

        var minimumContentWidth = Math.Max(1, MinimumWindowSize - borderX * 2);
        var minimumContentHeight = Math.Max(1, MinimumWindowSize - borderY * 2);
        var effectiveMinimumScale = Math.Max(
            minimumScale,
            Math.Max(
                minimumContentWidth / (imageWidth * scaleX),
                minimumContentHeight / (imageHeight * scaleY)));
        effectiveMinimumScale = Math.Min(effectiveMinimumScale, maximumScale);

        var effectiveScale = Math.Clamp(requestedScale, effectiveMinimumScale, maximumScale);
        var contentWidth = Math.Max(1, (int)Math.Round(imageWidth * effectiveScale * scaleX));
        var contentHeight = Math.Max(1, (int)Math.Round(imageHeight * effectiveScale * scaleY));

        return new StickyImageScaleResult(
            effectiveScale,
            contentWidth + (int)Math.Round(borderX * 2),
            contentHeight + (int)Math.Round(borderY * 2));
    }

    public static double ScaleThatFits(
        double imageWidth,
        double imageHeight,
        int availableWidth,
        int availableHeight,
        double scaleX,
        double scaleY,
        double borderX,
        double borderY)
    {
        var contentWidth = Math.Max(1, availableWidth - borderX * 2);
        var contentHeight = Math.Max(1, availableHeight - borderY * 2);
        return Math.Min(
            contentWidth / (Math.Max(1, imageWidth) * Math.Max(0.01, scaleX)),
            contentHeight / (Math.Max(1, imageHeight) * Math.Max(0.01, scaleY)));
    }

    public static double ResolveDisplayedScale(
        double imageWidth,
        double imageHeight,
        double storedScale,
        int physicalWidth,
        int physicalHeight,
        double minimumScale,
        double maximumScale,
        double scaleX,
        double scaleY,
        double borderX,
        double borderY)
    {
        var expected = Calculate(imageWidth, imageHeight, storedScale, minimumScale, maximumScale,
            scaleX, scaleY, borderX, borderY);
        // Preserve the precise scale on ordinary completed notches: repeatedly
        // deriving it from rounded pixels would introduce slow scale drift.
        if (Math.Abs(expected.Width - physicalWidth) <= 1 &&
            Math.Abs(expected.Height - physicalHeight) <= 1) return expected.Scale;

        // Initial screenshot placement and a monitor DPI change can resize the HWND
        // independently of its stored DIP scale. Uniform content uses one fitted
        // scale for both axes; never infer independent X/Y magnifications.
        var displayed = ScaleThatFits(imageWidth, imageHeight, physicalWidth, physicalHeight,
            scaleX, scaleY, borderX, borderY);
        return Calculate(imageWidth, imageHeight, displayed, minimumScale, maximumScale,
            scaleX, scaleY, borderX, borderY).Scale;
    }
}

internal readonly record struct StickyImageScaleResult(double Scale, int Width, int Height);
