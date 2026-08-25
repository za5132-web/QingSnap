using System.Drawing;

namespace QingSnap.App.Services;

public static class PinDockLayoutCalculator
{
    public static PinDockLayout Calculate(
        Rectangle workArea,
        int width,
        int height,
        int topHint,
        bool useLeftEdge,
        double dpiScaleX,
        double dpiScaleY)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var peekWidth = Math.Min(width, Math.Max(14, (int)Math.Round(18 * dpiScaleX)));
        var verticalMargin = Math.Max(2, (int)Math.Round(4 * dpiScaleY));
        var top = Math.Clamp(
            topHint,
            workArea.Top + verticalMargin,
            Math.Max(workArea.Top + verticalMargin, workArea.Bottom - height - verticalMargin));
        var restingLeft = useLeftEdge
            ? workArea.Left - width + peekWidth
            : workArea.Right - peekWidth;
        var revealedLeft = useLeftEdge
            ? workArea.Left
            : workArea.Right - width;
        return new PinDockLayout(
            new Rectangle(restingLeft, top, width, height),
            new Rectangle(revealedLeft, top, width, height),
            useLeftEdge);
    }
}

public readonly record struct PinDockLayout(
    Rectangle RestingBounds,
    Rectangle RevealedBounds,
    bool UsesLeftEdge);
