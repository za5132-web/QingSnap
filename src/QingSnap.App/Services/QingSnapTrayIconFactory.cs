using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QingSnap.App.Services;

internal static class QingSnapTrayIconFactory
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var background = new SolidBrush(Color.FromArgb(245, 12, 23, 31));
        using var framePen = new Pen(Color.FromArgb(255, 118, 223, 238), 2.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var pixelBrush = new SolidBrush(Color.FromArgb(255, 255, 92, 105));
        using var backgroundPath = RoundedRectangle(new RectangleF(2.5f, 2.5f, 27, 27), 7);
        graphics.FillPath(background, backgroundPath);

        DrawCorner(graphics, framePen, 8, 13, 8, 8, 13, 8);
        DrawCorner(graphics, framePen, 19, 8, 24, 8, 24, 13);
        DrawCorner(graphics, framePen, 8, 19, 8, 24, 13, 24);
        DrawCorner(graphics, framePen, 19, 24, 24, 24, 24, 19);
        graphics.FillRectangle(pixelBrush, 14, 14, 4, 4);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawCorner(
        Graphics graphics,
        Pen pen,
        float x1,
        float y1,
        float x2,
        float y2,
        float x3,
        float y3)
    {
        graphics.DrawLine(pen, x1, y1, x2, y2);
        graphics.DrawLine(pen, x2, y2, x3, y3);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
