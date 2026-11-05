namespace QingSnap.App.Services;

/// <summary>Small allocation-free helpers shared by the pin's short-lived motions.</summary>
internal static class PinMotionMath
{
    private const double ZoomResponse = 50;
    public static double EaseOut(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        return 1 - Math.Pow(1 - progress, 3);
    }

    public static double Interpolate(double from, double to, double progress) =>
        from + (to - from) * EaseOut(progress);

    public static double WheelTarget(double target, int delta, double step = 1.1) =>
        target * Math.Pow(step, Math.Clamp(delta / 120D, -4, 4));

    // Exact critically damped integration in log-scale coordinates. Retargeting
    // keeps velocity instead of restarting a separate ease-out for every notch.
    // The analytic solution is independent of refresh rate and needs no substeps.
    public static (double Position, double Velocity) AdvanceZoom(
        double position, double velocity, double target, double seconds)
    {
        if (seconds <= 0) return (position, velocity);
        if (Math.Abs(position - target) < 1e-12 && Math.Abs(velocity) < 1e-10)
            return (target, 0);
        var offset = position - target;
        var coefficient = velocity + ZoomResponse * offset;
        var decay = Math.Exp(-ZoomResponse * seconds);
        return (target + (offset + coefficient * seconds) * decay,
            (velocity - ZoomResponse * coefficient * seconds) * decay);
    }

    public static double ZoomRetargetVelocity(double position, double velocity, double target)
    {
        var distance = target - position;
        // When the user tightens/reverses the target, excessive inherited
        // velocity could pass it and spring backwards. This is the exact
        // monotonic bound for the critically damped solution. An ordinary
        // same-direction continuation stays inside it without changing speed.
        return distance >= 0
            ? Math.Clamp(velocity, 0, ZoomResponse * distance)
            : Math.Clamp(velocity, ZoomResponse * distance, 0);
    }

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
