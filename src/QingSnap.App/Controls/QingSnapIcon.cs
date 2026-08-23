using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace QingSnap.App.Controls;

public sealed class QingSnapIcon : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(QingSnapIconKind),
        typeof(QingSnapIcon),
        new FrameworkPropertyMetadata(QingSnapIconKind.Capture, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconStrokeThicknessProperty = DependencyProperty.Register(
        nameof(IconStrokeThickness),
        typeof(double),
        typeof(QingSnapIcon),
        new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));

    public QingSnapIcon()
    {
        Width = 20;
        Height = 20;
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public QingSnapIconKind Kind
    {
        get => (QingSnapIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double IconStrokeThickness
    {
        get => (double)GetValue(IconStrokeThicknessProperty);
        set => SetValue(IconStrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var brush = Foreground ?? Brushes.White;
        var pen = new Pen(brush, IconStrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        drawingContext.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        DrawIcon(drawingContext, pen, brush);
        drawingContext.Pop();
    }

    private void DrawIcon(DrawingContext dc, Pen pen, Brush brush)
    {
        switch (Kind)
        {
            case QingSnapIconKind.Pen:
                Line(dc, pen, 5, 18.5, 17.8, 5.7);
                Line(dc, pen, 14.8, 4.8, 18.8, 8.8);
                Line(dc, pen, 4.4, 19.6, 8.2, 18.8);
                break;
            case QingSnapIconKind.Arrow:
                dc.DrawGeometry(brush, null, Geometry.Parse("M3.5,19.5 L15.2,8.4 L12.7,6.5 L20.5,3.5 L17.3,11.2 L15.4,8.8 Z"));
                break;
            case QingSnapIconKind.DoubleArrow:
                dc.DrawGeometry(brush, null, Geometry.Parse("M3.5,19.5 L6.6,12.1 L8.5,14 L15.5,7 L13.6,5.1 L20.5,3.5 L17.5,10.5 L15.9,8.9 L8.9,15.9 L10.5,17.5 Z"));
                break;
            case QingSnapIconKind.Line:
                Line(dc, pen, 4, 18, 20, 6);
                break;
            case QingSnapIconKind.Rectangle:
                dc.DrawRoundedRectangle(null, pen, new Rect(4, 5, 16, 14), 1.5, 1.5);
                break;
            case QingSnapIconKind.Ellipse:
                dc.DrawEllipse(null, pen, new Point(12, 12), 8, 6.5);
                break;
            case QingSnapIconKind.Text:
                Line(dc, pen, 5, 6, 19, 6);
                Line(dc, pen, 12, 6, 12, 19);
                Line(dc, pen, 9, 19, 15, 19);
                break;
            case QingSnapIconKind.Mosaic:
                FillRect(dc, brush, 4, 4, 6, 6, 1);
                FillRect(dc, brush, 14, 4, 6, 6, 1);
                FillRect(dc, brush, 9, 9, 6, 6, 1);
                FillRect(dc, brush, 4, 14, 6, 6, 1);
                FillRect(dc, brush, 14, 14, 6, 6, 1);
                break;
            case QingSnapIconKind.Highlight:
                dc.DrawRoundedRectangle(null, pen, new Rect(7, 3.5, 10, 13), 2, 2);
                Line(dc, pen, 8.2, 15.7, 5.2, 19.5);
                Line(dc, pen, 15.8, 15.7, 18.8, 19.5);
                Line(dc, pen, 6, 20, 18, 20);
                break;
            case QingSnapIconKind.Blur:
                Wave(dc, pen, 4, 7);
                Wave(dc, pen, 4, 12);
                Wave(dc, pen, 4, 17);
                break;
            case QingSnapIconKind.Number:
                dc.DrawEllipse(null, pen, new Point(12, 12), 8, 8);
                Line(dc, pen, 10, 9, 12, 7.5);
                Line(dc, pen, 12, 7.5, 12, 16.5);
                Line(dc, pen, 9.5, 16.5, 14.5, 16.5);
                break;
            case QingSnapIconKind.Select:
                dc.DrawGeometry(null, pen, Geometry.Parse("M5,3 L18,13 L12.2,14.2 L9.5,20 L5,3 Z"));
                break;
            case QingSnapIconKind.Color:
                dc.DrawGeometry(null, pen, Geometry.Parse("M12,3 C12,3 6.5,9.8 6.5,14 A5.5,5.5 0 0 0 17.5,14 C17.5,9.8 12,3 12,3 Z"));
                dc.DrawEllipse(brush, null, new Point(12, 15.3), 1.4, 1.4);
                break;
            case QingSnapIconKind.Thickness:
                Line(dc, new Pen(brush, 1), 4, 6, 20, 6);
                Line(dc, new Pen(brush, 2), 4, 12, 20, 12);
                Line(dc, new Pen(brush, 3.4), 4, 18, 20, 18);
                break;
            case QingSnapIconKind.FontSize:
                Line(dc, pen, 5, 19, 10.5, 5);
                Line(dc, pen, 10.5, 5, 16, 19);
                Line(dc, pen, 7, 14, 14, 14);
                Line(dc, pen, 16.5, 9, 20, 9);
                Line(dc, pen, 18.25, 7.25, 18.25, 10.75);
                break;
            case QingSnapIconKind.Undo:
                dc.DrawGeometry(null, pen, Geometry.Parse("M7,8 L3.5,11.5 L7,15 M4,11.5 H13.5 A6,6 0 0 1 19.5,17.5"));
                break;
            case QingSnapIconKind.Clear:
                Line(dc, pen, 7, 7, 17, 7);
                Line(dc, pen, 9, 4.5, 15, 4.5);
                dc.DrawRoundedRectangle(null, pen, new Rect(8, 7, 8, 12), 1, 1);
                Line(dc, pen, 11, 10, 11, 16);
                Line(dc, pen, 13.5, 10, 13.5, 16);
                break;
            case QingSnapIconKind.LongCapture:
                dc.DrawRoundedRectangle(null, pen, new Rect(5, 3, 14, 11), 1.5, 1.5);
                Line(dc, pen, 12, 8, 12, 20);
                Line(dc, pen, 8.5, 16.5, 12, 20);
                Line(dc, pen, 15.5, 16.5, 12, 20);
                break;
            case QingSnapIconKind.Ocr:
                Corner(dc, pen, 4, 8, 4, 4, 8, 4);
                Corner(dc, pen, 16, 4, 20, 4, 20, 8);
                Corner(dc, pen, 4, 16, 4, 20, 8, 20);
                Corner(dc, pen, 16, 20, 20, 20, 20, 16);
                Line(dc, pen, 8, 9, 16, 9);
                Line(dc, pen, 8, 13, 16, 13);
                Line(dc, pen, 8, 17, 13.5, 17);
                break;
            case QingSnapIconKind.Pin:
                dc.DrawGeometry(null, pen, Geometry.Parse("M8,4 H16 L15,9 L18,12 H13 L12,20 L11,12 H6 L9,9 Z"));
                break;
            case QingSnapIconKind.Copy:
                dc.DrawRoundedRectangle(null, pen, new Rect(8, 8, 11, 11), 1.5, 1.5);
                dc.DrawRoundedRectangle(null, pen, new Rect(5, 5, 11, 11), 1.5, 1.5);
                break;
            case QingSnapIconKind.Save:
                dc.DrawRoundedRectangle(null, pen, new Rect(5, 4, 14, 16), 1.5, 1.5);
                dc.DrawRectangle(null, pen, new Rect(8, 4, 7, 5));
                dc.DrawRoundedRectangle(null, pen, new Rect(8, 13, 8, 5), 1, 1);
                break;
            case QingSnapIconKind.Confirm:
                Line(dc, pen, 4.5, 12.5, 9.5, 17.5);
                Line(dc, pen, 9.5, 17.5, 20, 6.5);
                break;
            case QingSnapIconKind.Close:
                Line(dc, pen, 6, 6, 18, 18);
                Line(dc, pen, 18, 6, 6, 18);
                break;
            case QingSnapIconKind.Geometry:
                Corner(dc, pen, 4, 9, 4, 4, 9, 4);
                Corner(dc, pen, 15, 4, 20, 4, 20, 9);
                Corner(dc, pen, 4, 15, 4, 20, 9, 20);
                Corner(dc, pen, 15, 20, 20, 20, 20, 15);
                break;
            case QingSnapIconKind.Minimize:
                Line(dc, pen, 6, 17, 18, 17);
                break;
            case QingSnapIconKind.Maximize:
                dc.DrawRectangle(null, pen, new Rect(6, 6, 12, 12));
                break;
            case QingSnapIconKind.Restore:
                dc.DrawRectangle(null, pen, new Rect(5, 8, 11, 11));
                Line(dc, pen, 8, 8, 8, 5);
                Line(dc, pen, 8, 5, 19, 5);
                Line(dc, pen, 19, 5, 19, 16);
                Line(dc, pen, 16, 16, 19, 16);
                break;
            default:
                Corner(dc, pen, 4, 9, 4, 4, 9, 4);
                Corner(dc, pen, 15, 4, 20, 4, 20, 9);
                Corner(dc, pen, 4, 15, 4, 20, 9, 20);
                Corner(dc, pen, 15, 20, 20, 20, 20, 15);
                dc.DrawEllipse(brush, null, new Point(12, 12), 2, 2);
                break;
        }
    }

    private static void Line(DrawingContext dc, Pen pen, double x1, double y1, double x2, double y2) =>
        dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));

    private static void FillRect(DrawingContext dc, Brush brush, double x, double y, double width, double height, double radius) =>
        dc.DrawRoundedRectangle(brush, null, new Rect(x, y, width, height), radius, radius);

    private static void Corner(
        DrawingContext dc,
        Pen pen,
        double x1,
        double y1,
        double x2,
        double y2,
        double x3,
        double y3)
    {
        Line(dc, pen, x1, y1, x2, y2);
        Line(dc, pen, x2, y2, x3, y3);
    }

    private static void Wave(DrawingContext dc, Pen pen, double x, double y)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(x, y), false, false);
            context.BezierTo(new Point(x + 3, y - 2), new Point(x + 5, y + 2), new Point(x + 8, y), true, false);
            context.BezierTo(new Point(x + 11, y - 2), new Point(x + 13, y + 2), new Point(x + 16, y), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}

public enum QingSnapIconKind
{
    Capture,
    Pen,
    Arrow,
    DoubleArrow,
    Line,
    Rectangle,
    Ellipse,
    Text,
    Mosaic,
    Highlight,
    Blur,
    Number,
    Select,
    Color,
    Thickness,
    FontSize,
    Undo,
    Clear,
    LongCapture,
    Ocr,
    Pin,
    Copy,
    Save,
    Confirm,
    Close,
    Geometry,
    Minimize,
    Maximize,
    Restore
}
