namespace QingSnap.App.Services;

// All values are image/view-local DIPs, never virtual-desktop coordinates.
internal static class PinContentViewportMath
{
    public static double Fit(double width, double height, double viewportWidth, double viewportHeight) =>
        Math.Max(0.000001, Math.Min(viewportWidth / Math.Max(1, width), viewportHeight / Math.Max(1, height)));

    public static double Padding(double extent, double viewport) => Math.Max(0, (viewport - extent) / 2);

    public static double ImageCoordinate(double cursor, double offset, double extent, double viewport, double scale) =>
        (cursor + offset - Padding(extent, viewport)) / Math.Max(0.000001, scale);

    public static double AnchoredOffset(double imageCoordinate, double scale, double cursor, double viewport, double imageSize) =>
        Math.Clamp(imageCoordinate * scale - cursor + Padding(imageSize * scale, viewport),
            0, Math.Max(0, imageSize * scale - viewport));
}
