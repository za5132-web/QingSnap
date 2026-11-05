namespace QingSnap.App.Services;

/// <summary>Physical surface limits, independent of any monitor's desktop/work area.</summary>
internal readonly record struct PinSurfaceBudget(int MaximumWidth, int MaximumHeight, long MaximumPixels)
{
    // About 64 MiB for one 32-bit surface, not a promise about total GPU/process memory.
    internal const long DefaultMaximumPixels = 16L * 1024 * 1024;
    internal const int MaximumDimension = 8192;
    internal const int SoftwareMaximumDimension = 4096;

    public static PinSurfaceBudget FromHardware(double width, double height, int renderingTier)
    {
        var fallback = renderingTier > 0 ? MaximumDimension : SoftwareMaximumDimension;
        static int Axis(double value, int fallback) => double.IsFinite(value) && value > 0
            ? Math.Max(1, Math.Min(fallback, (int)Math.Min(int.MaxValue, Math.Floor(value))))
            : fallback;
        return new PinSurfaceBudget(Axis(width, fallback), Axis(height, fallback), DefaultMaximumPixels);
    }

    public double MaximumScaleFor(double imageWidth, double imageHeight, double dpiX, double dpiY,
        double borderX, double borderY, double requestedMaximum)
    {
        var a = Math.Max(1, imageWidth) * Math.Max(0.01, dpiX);
        var b = Math.Max(1, imageHeight) * Math.Max(0.01, dpiY);
        var borderWidth = Math.Max(0, (int)Math.Round(borderX * 2));
        var borderHeight = Math.Max(0, (int)Math.Round(borderY * 2));
        // A fixed carrier must enclose fractional desktop edges with floor/ceil,
        // which can require up to two pixels for fractional size and origin.
        var byAxes = Math.Min(Math.Max(1, MaximumWidth - borderWidth - 2) / a,
            Math.Max(1, MaximumHeight - borderHeight - 2) / b);
        // Reserve both rounded desktop edges, not only rounded width/height.
        var x = borderWidth + 2D;
        var y = borderHeight + 2D;
        var available = Math.Max(1, MaximumPixels - x * y);
        var linear = a * y + b * x;
        var byArea = 2 * available / (Math.Sqrt(linear * linear + 4 * a * b * available) + linear);
        return Math.Max(double.Epsilon, Math.Min(requestedMaximum, Math.Min(byAxes, byArea)));
    }
}
