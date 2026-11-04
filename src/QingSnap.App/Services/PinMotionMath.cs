namespace QingSnap.App.Services;

/// <summary>Small allocation-free helpers shared by the pin's short-lived motions.</summary>
internal static class PinMotionMath
{
    public static double EaseOut(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        return 1 - Math.Pow(1 - progress, 3);
    }

    public static double Interpolate(double from, double to, double progress) =>
        from + (to - from) * EaseOut(progress);

    public static double WheelTarget(double target, int delta, double step = 1.1) =>
        target * Math.Pow(step, Math.Clamp(delta / 120D, -4, 4));

    public static int AnchoredOrigin(double physicalCenter, int physicalSize) =>
        (int)Math.Round(physicalCenter - physicalSize / 2D);

    public static bool IsInsideDockZone(
        int cursorX, int cursorY, int workLeft, int workRight,
        int dockTop, int dockWidth, int dockHeight, bool leftEdge, int tolerance)
    {
        var left = leftEdge ? workLeft - tolerance : workRight - dockWidth - tolerance;
        var right = leftEdge ? workLeft + dockWidth + tolerance : workRight + tolerance;
        return cursorX >= left && cursorX <= right &&
               cursorY >= dockTop - tolerance && cursorY <= dockTop + dockHeight + tolerance;
    }
}
