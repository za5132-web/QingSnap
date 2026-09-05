using System.Drawing;

namespace QingSnap.App.Services;

internal enum CaptureAspectRatioMode
{
    Free,
    Square,
    FourThree,
    ThreeTwo,
    SixteenNine,
    NineSixteen,
    Current
}

internal enum CaptureResizeHandle
{
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

internal static class CaptureAspectRatioConstraint
{
    public static double? RatioFor(CaptureAspectRatioMode mode, Rectangle current) => mode switch
    {
        CaptureAspectRatioMode.Square => 1D,
        CaptureAspectRatioMode.FourThree => 4D / 3D,
        CaptureAspectRatioMode.ThreeTwo => 3D / 2D,
        CaptureAspectRatioMode.SixteenNine => 16D / 9D,
        CaptureAspectRatioMode.NineSixteen => 9D / 16D,
        CaptureAspectRatioMode.Current when current.Width > 0 && current.Height > 0 =>
            (double)current.Width / current.Height,
        _ => null
    };

    public static Rectangle Create(
        Point anchor,
        Point pointer,
        double ratio,
        int boundsWidth,
        int boundsHeight)
    {
        if (!IsValidRatio(ratio) || boundsWidth <= 0 || boundsHeight <= 0)
        {
            return Rectangle.Empty;
        }

        anchor = ClampPoint(anchor, boundsWidth, boundsHeight);
        pointer = ClampPoint(pointer, boundsWidth, boundsHeight);
        var toLeft = pointer.X < anchor.X;
        var toTop = pointer.Y < anchor.Y;
        var rawWidth = Math.Abs(pointer.X - anchor.X);
        var rawHeight = Math.Abs(pointer.Y - anchor.Y);
        var maxWidth = toLeft ? anchor.X : boundsWidth - anchor.X;
        var maxHeight = toTop ? anchor.Y : boundsHeight - anchor.Y;
        var size = ConstrainCornerSize(rawWidth, rawHeight, ratio, maxWidth, maxHeight);
        return new Rectangle(
            toLeft ? anchor.X - size.Width : anchor.X,
            toTop ? anchor.Y - size.Height : anchor.Y,
            size.Width,
            size.Height);
    }

    public static Rectangle Resize(
        Rectangle start,
        Point pointer,
        CaptureResizeHandle handle,
        double ratio,
        int boundsWidth,
        int boundsHeight)
    {
        if (!IsValidRatio(ratio) || start.Width <= 0 || start.Height <= 0 ||
            boundsWidth <= 0 || boundsHeight <= 0)
        {
            return start;
        }

        pointer = ClampPoint(pointer, boundsWidth, boundsHeight);
        return handle switch
        {
            CaptureResizeHandle.TopLeft => ResizeCorner(
                new Point(start.Right, start.Bottom), pointer, true, true, ratio, boundsWidth, boundsHeight),
            CaptureResizeHandle.TopRight => ResizeCorner(
                new Point(start.Left, start.Bottom), pointer, false, true, ratio, boundsWidth, boundsHeight),
            CaptureResizeHandle.BottomLeft => ResizeCorner(
                new Point(start.Right, start.Top), pointer, true, false, ratio, boundsWidth, boundsHeight),
            CaptureResizeHandle.BottomRight => ResizeCorner(
                new Point(start.Left, start.Top), pointer, false, false, ratio, boundsWidth, boundsHeight),
            CaptureResizeHandle.Left => ResizeHorizontalEdge(start, start.Right - pointer.X, true, ratio, boundsWidth, boundsHeight),
            CaptureResizeHandle.Right => ResizeHorizontalEdge(start, pointer.X - start.Left, false, ratio, boundsWidth, boundsHeight),
            CaptureResizeHandle.Top => ResizeVerticalEdge(start, start.Bottom - pointer.Y, true, ratio, boundsWidth, boundsHeight),
            CaptureResizeHandle.Bottom => ResizeVerticalEdge(start, pointer.Y - start.Top, false, ratio, boundsWidth, boundsHeight),
            _ => start
        };
    }

    public static Rectangle ResizeFromCenter(
        Rectangle start,
        Point pointer,
        CaptureResizeHandle handle,
        double? ratio,
        Rectangle bounds)
    {
        if (start.Width <= 0 || start.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0 ||
            handle is not (CaptureResizeHandle.TopLeft or CaptureResizeHandle.TopRight or
                CaptureResizeHandle.BottomLeft or CaptureResizeHandle.BottomRight))
        {
            return start;
        }

        pointer = new Point(
            Math.Clamp(pointer.X, bounds.Left, bounds.Right),
            Math.Clamp(pointer.Y, bounds.Top, bounds.Bottom));
        var centerX = start.Left + start.Width / 2D;
        var centerY = start.Top + start.Height / 2D;
        var movesLeft = handle is CaptureResizeHandle.TopLeft or CaptureResizeHandle.BottomLeft;
        var movesTop = handle is CaptureResizeHandle.TopLeft or CaptureResizeHandle.TopRight;
        var horizontalDistance = movesLeft ? centerX - pointer.X : pointer.X - centerX;
        var verticalDistance = movesTop ? centerY - pointer.Y : pointer.Y - centerY;
        var requestedWidth = Math.Max(
            1,
            (int)Math.Round(horizontalDistance * 2, MidpointRounding.AwayFromZero));
        var requestedHeight = Math.Max(
            1,
            (int)Math.Round(verticalDistance * 2, MidpointRounding.AwayFromZero));
        var maxWidth = Math.Max(
            1,
            (int)Math.Floor(2 * Math.Min(centerX - bounds.Left, bounds.Right - centerX)));
        var maxHeight = Math.Max(
            1,
            (int)Math.Floor(2 * Math.Min(centerY - bounds.Top, bounds.Bottom - centerY)));
        var size = ratio is { } lockedRatio && IsValidRatio(lockedRatio)
            ? ConstrainCornerSize(requestedWidth, requestedHeight, lockedRatio, maxWidth, maxHeight)
            : new Size(
                Math.Clamp(requestedWidth, 1, maxWidth),
                Math.Clamp(requestedHeight, 1, maxHeight));
        var left = Math.Clamp(
            (int)Math.Round(centerX - size.Width / 2D, MidpointRounding.AwayFromZero),
            bounds.Left,
            bounds.Right - size.Width);
        var top = Math.Clamp(
            (int)Math.Round(centerY - size.Height / 2D, MidpointRounding.AwayFromZero),
            bounds.Top,
            bounds.Bottom - size.Height);
        return new Rectangle(left, top, size.Width, size.Height);
    }

    public static Size ConstrainSize(
        int requestedWidth,
        int requestedHeight,
        double ratio,
        bool widthIsPrimary,
        int maxWidth,
        int maxHeight)
    {
        if (!IsValidRatio(ratio) || maxWidth <= 0 || maxHeight <= 0)
        {
            return Size.Empty;
        }

        return widthIsPrimary
            ? SizeFromWidth(requestedWidth, ratio, maxWidth, maxHeight)
            : SizeFromHeight(requestedHeight, ratio, maxWidth, maxHeight);
    }

    public static Rectangle FitCentered(
        Rectangle current,
        double ratio,
        int boundsWidth,
        int boundsHeight)
    {
        if (!IsValidRatio(ratio) || current.Width <= 0 || current.Height <= 0)
        {
            return current;
        }

        var size = ConstrainSize(
            current.Width,
            current.Height,
            ratio,
            widthIsPrimary: true,
            boundsWidth,
            boundsHeight);
        var centerX = current.Left + current.Width / 2D;
        var centerY = current.Top + current.Height / 2D;
        var left = Math.Clamp((int)Math.Round(centerX - size.Width / 2D), 0, boundsWidth - size.Width);
        var top = Math.Clamp((int)Math.Round(centerY - size.Height / 2D), 0, boundsHeight - size.Height);
        return new Rectangle(left, top, size.Width, size.Height);
    }

    private static Rectangle ResizeCorner(
        Point anchor,
        Point pointer,
        bool toLeft,
        bool toTop,
        double ratio,
        int boundsWidth,
        int boundsHeight)
    {
        var rawWidth = toLeft ? anchor.X - pointer.X : pointer.X - anchor.X;
        var rawHeight = toTop ? anchor.Y - pointer.Y : pointer.Y - anchor.Y;
        var maxWidth = toLeft ? anchor.X : boundsWidth - anchor.X;
        var maxHeight = toTop ? anchor.Y : boundsHeight - anchor.Y;
        var size = ConstrainCornerSize(rawWidth, rawHeight, ratio, maxWidth, maxHeight);
        return new Rectangle(
            toLeft ? anchor.X - size.Width : anchor.X,
            toTop ? anchor.Y - size.Height : anchor.Y,
            size.Width,
            size.Height);
    }

    private static Rectangle ResizeHorizontalEdge(
        Rectangle start,
        int requestedWidth,
        bool movesLeft,
        double ratio,
        int boundsWidth,
        int boundsHeight)
    {
        var fixedX = movesLeft ? start.Right : start.Left;
        var maxWidth = movesLeft ? fixedX : boundsWidth - fixedX;
        var size = SizeFromWidth(requestedWidth, ratio, maxWidth, boundsHeight);
        var centerY = start.Top + start.Height / 2D;
        var top = Math.Clamp((int)Math.Round(centerY - size.Height / 2D), 0, boundsHeight - size.Height);
        return new Rectangle(movesLeft ? fixedX - size.Width : fixedX, top, size.Width, size.Height);
    }

    private static Rectangle ResizeVerticalEdge(
        Rectangle start,
        int requestedHeight,
        bool movesTop,
        double ratio,
        int boundsWidth,
        int boundsHeight)
    {
        var fixedY = movesTop ? start.Bottom : start.Top;
        var maxHeight = movesTop ? fixedY : boundsHeight - fixedY;
        var size = SizeFromHeight(requestedHeight, ratio, boundsWidth, maxHeight);
        var centerX = start.Left + start.Width / 2D;
        var left = Math.Clamp((int)Math.Round(centerX - size.Width / 2D), 0, boundsWidth - size.Width);
        return new Rectangle(left, movesTop ? fixedY - size.Height : fixedY, size.Width, size.Height);
    }

    private static Size ConstrainCornerSize(
        int rawWidth,
        int rawHeight,
        double ratio,
        int maxWidth,
        int maxHeight)
    {
        rawWidth = Math.Max(1, rawWidth);
        rawHeight = Math.Max(1, rawHeight);
        var widthIsPrimary = rawWidth / (double)rawHeight >= ratio;
        return ConstrainSize(rawWidth, rawHeight, ratio, widthIsPrimary, maxWidth, maxHeight);
    }

    private static Size SizeFromWidth(int requestedWidth, double ratio, int maxWidth, int maxHeight)
    {
        var width = Math.Clamp(requestedWidth, 1, Math.Max(1, maxWidth));
        var height = Math.Max(1, (int)Math.Round(width / ratio, MidpointRounding.AwayFromZero));
        if (height > maxHeight)
        {
            height = maxHeight;
            width = Math.Max(1, (int)Math.Round(height * ratio, MidpointRounding.AwayFromZero));
        }

        width = Math.Min(width, maxWidth);
        return new Size(width, Math.Min(height, maxHeight));
    }

    private static Size SizeFromHeight(int requestedHeight, double ratio, int maxWidth, int maxHeight)
    {
        var height = Math.Clamp(requestedHeight, 1, Math.Max(1, maxHeight));
        var width = Math.Max(1, (int)Math.Round(height * ratio, MidpointRounding.AwayFromZero));
        if (width > maxWidth)
        {
            width = maxWidth;
            height = Math.Max(1, (int)Math.Round(width / ratio, MidpointRounding.AwayFromZero));
        }

        height = Math.Min(height, maxHeight);
        return new Size(Math.Min(width, maxWidth), height);
    }

    private static Point ClampPoint(Point point, int boundsWidth, int boundsHeight) => new(
        Math.Clamp(point.X, 0, boundsWidth),
        Math.Clamp(point.Y, 0, boundsHeight));

    private static bool IsValidRatio(double ratio) => double.IsFinite(ratio) && ratio > 0;
}
