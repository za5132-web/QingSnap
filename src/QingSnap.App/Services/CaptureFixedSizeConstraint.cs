using System.Drawing;

namespace QingSnap.App.Services;

internal readonly record struct CaptureSizePreset(string Name, int Width, int Height);

internal static class CaptureSizePresets
{
    public static IReadOnlyList<CaptureSizePreset> BuiltIn { get; } =
    [
        new("方形 800", 800, 800),
        new("Full HD", 1920, 1080),
        new("竖版 3:4", 750, 1000)
    ];
}

internal static class CaptureFixedSizeConstraint
{
    public static Size LimitSize(Size requested, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return Size.Empty;
        }

        return new Size(
            Math.Clamp(requested.Width, 1, bounds.Width),
            Math.Clamp(requested.Height, 1, bounds.Height));
    }

    public static Rectangle Place(Point location, Size requested, Rectangle bounds)
    {
        var size = LimitSize(requested, bounds);
        if (size.IsEmpty)
        {
            return Rectangle.Empty;
        }

        var left = Math.Clamp(location.X, bounds.Left, bounds.Right - size.Width);
        var top = Math.Clamp(location.Y, bounds.Top, bounds.Bottom - size.Height);
        return new Rectangle(left, top, size.Width, size.Height);
    }

    public static Rectangle Move(Rectangle current, int deltaX, int deltaY, Rectangle bounds) =>
        Place(
            new Point(current.Left + deltaX, current.Top + deltaY),
            current.Size,
            bounds);
}
