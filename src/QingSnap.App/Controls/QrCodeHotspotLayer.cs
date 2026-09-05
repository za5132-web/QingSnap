using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QingSnap.App.Models;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace QingSnap.App.Controls;

public sealed class QrCodeHotspotLayer : Canvas
{
    private IReadOnlyList<QrCodeResult> _results = [];
    private double _sourceWidth = 1;
    private double _sourceHeight = 1;
    private Stretch _stretch = Stretch.Fill;

    public QrCodeHotspotLayer()
    {
        Background = null;
        ClipToBounds = true;
        Visibility = Visibility.Collapsed;
        SizeChanged += (_, _) => UpdateMarkerPositions();
    }

    public event Action<QrCodeResult>? ResultInvoked;

    public bool HasResults => _results.Count > 0;

    public void ShowResults(
        IReadOnlyList<QrCodeResult> results,
        double sourceWidth,
        double sourceHeight,
        Stretch stretch = Stretch.Fill)
    {
        ArgumentNullException.ThrowIfNull(results);
        _results = results;
        _sourceWidth = Math.Max(1, sourceWidth);
        _sourceHeight = Math.Max(1, sourceHeight);
        _stretch = stretch;
        Children.Clear();

        foreach (var result in results)
        {
            var marker = new QrCodeHotspotMarker(result);
            marker.Invoked += selected => ResultInvoked?.Invoke(selected);
            Children.Add(marker);
        }

        Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMarkerPositions();
    }

    public void ClearResults()
    {
        _results = [];
        Children.Clear();
        Visibility = Visibility.Collapsed;
    }

    private void UpdateMarkerPositions()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || Children.Count == 0)
        {
            return;
        }

        var scaleX = ActualWidth / _sourceWidth;
        var scaleY = ActualHeight / _sourceHeight;
        var offsetX = 0D;
        var offsetY = 0D;
        if (_stretch == Stretch.Uniform)
        {
            scaleX = scaleY = Math.Min(scaleX, scaleY);
            offsetX = (ActualWidth - (_sourceWidth * scaleX)) / 2D;
            offsetY = (ActualHeight - (_sourceHeight * scaleY)) / 2D;
        }
        else if (_stretch == Stretch.UniformToFill)
        {
            scaleX = scaleY = Math.Max(scaleX, scaleY);
            offsetX = (ActualWidth - (_sourceWidth * scaleX)) / 2D;
            offsetY = (ActualHeight - (_sourceHeight * scaleY)) / 2D;
        }

        for (var index = 0; index < Children.Count && index < _results.Count; index++)
        {
            if (Children[index] is not FrameworkElement marker)
            {
                continue;
            }

            var centerX = offsetX + (_results[index].CenterX * scaleX);
            var centerY = offsetY + (_results[index].CenterY * scaleY);
            var left = Math.Clamp(
                centerX - (marker.Width / 2D),
                2,
                Math.Max(2, ActualWidth - marker.Width - 2));
            var top = Math.Clamp(
                centerY - (marker.Height / 2D),
                2,
                Math.Max(2, ActualHeight - marker.Height - 2));
            SetLeft(marker, left);
            SetTop(marker, top);
        }
    }
}

public sealed class QrCodeHotspotAdorner : Adorner
{
    private readonly VisualCollection _visuals;

    public QrCodeHotspotAdorner(
        UIElement adornedElement,
        IReadOnlyList<QrCodeResult> results,
        double sourceWidth,
        double sourceHeight,
        Stretch stretch = Stretch.Uniform)
        : base(adornedElement)
    {
        Layer = new QrCodeHotspotLayer();
        Layer.ShowResults(results, sourceWidth, sourceHeight, stretch);
        _visuals = new VisualCollection(this) { Layer };
        IsHitTestVisible = true;
    }

    public QrCodeHotspotLayer Layer { get; }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override WpfSize MeasureOverride(WpfSize constraint)
    {
        Layer.Measure(constraint);
        return constraint;
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        Layer.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

internal sealed class QrCodeHotspotMarker : FrameworkElement
{
    private const double MarkerSize = 32;
    private readonly ScaleTransform _scale = new(0.72, 0.72);
    private bool _isPressed;

    public QrCodeHotspotMarker(QrCodeResult result)
    {
        Result = result;
        Width = MarkerSize;
        Height = MarkerSize;
        Cursor = WpfCursors.Hand;
        Focusable = false;
        SnapsToDevicePixels = true;
        RenderTransformOrigin = new WpfPoint(0.5, 0.5);
        RenderTransform = _scale;
        ToolTip = CreateToolTip(result);
        ToolTipService.SetInitialShowDelay(this, 120);
        ToolTipService.SetBetweenShowDelay(this, 40);
        ToolTipService.SetShowDuration(this, 30000);

        Opacity = 0;
        BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
        AnimateScale(1, 180);
    }

    public event Action<QrCodeResult>? Invoked;

    public QrCodeResult Result { get; }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var center = new WpfPoint(MarkerSize / 2D, MarkerSize / 2D);
        drawingContext.DrawEllipse(
            new SolidColorBrush(WpfColor.FromArgb(238, 72, 205, 226)),
            new WpfPen(new SolidColorBrush(WpfColor.FromArgb(235, 239, 253, 255)), 1.5),
            center,
            15,
            15);
        drawingContext.DrawEllipse(
            new SolidColorBrush(WpfColor.FromRgb(12, 29, 38)),
            null,
            center,
            9,
            9);

        var iconPen = new WpfPen(WpfBrushes.White, 1.8)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        if (Result.IsUrl)
        {
            drawingContext.DrawLine(iconPen, new WpfPoint(12, 20), new WpfPoint(20, 12));
            drawingContext.DrawLine(iconPen, new WpfPoint(15.5, 12), new WpfPoint(20, 12));
            drawingContext.DrawLine(iconPen, new WpfPoint(20, 12), new WpfPoint(20, 16.5));
        }
        else
        {
            drawingContext.DrawRoundedRectangle(null, iconPen, new Rect(12, 12, 8, 8), 1, 1);
            drawingContext.DrawRoundedRectangle(null, iconPen, new Rect(9.5, 9.5, 8, 8), 1, 1);
        }
    }

    protected override void OnMouseEnter(WpfMouseEventArgs e)
    {
        base.OnMouseEnter(e);
        AnimateScale(1.14, 110);
    }

    protected override void OnMouseLeave(WpfMouseEventArgs e)
    {
        base.OnMouseLeave(e);
        AnimateScale(1, 110);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _isPressed = true;
        CaptureMouse();
        AnimateScale(1.05, 70);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var shouldInvoke = _isPressed && IsMouseOver;
        _isPressed = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        AnimateScale(IsMouseOver ? 1.14 : 1, 90);
        if (shouldInvoke)
        {
            Invoked?.Invoke(Result);
        }

        e.Handled = true;
    }

    protected override void OnLostMouseCapture(WpfMouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _isPressed = false;
    }

    private void AnimateScale(double target, int milliseconds)
    {
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static WpfToolTip CreateToolTip(QrCodeResult result)
    {
        var display = result.DisplayText;
        if (display.Length > 260)
        {
            display = $"{display[..260]}…";
        }

        var panel = new StackPanel { MaxWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = result.IsUrl ? "单击打开链接" : "单击复制内容",
            FontFamily = new WpfFontFamily("Microsoft YaHei UI"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(118, 223, 238))
        });
        panel.Children.Add(new TextBlock
        {
            Text = display,
            Margin = new Thickness(0, 5, 0, 0),
            FontFamily = new WpfFontFamily(result.IsUrl ? "Segoe UI" : "Microsoft YaHei UI"),
            FontSize = 12,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(243, 247, 249)),
            TextWrapping = TextWrapping.Wrap
        });

        return new WpfToolTip
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
            Background = WpfBrushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Content = new Border
            {
                Padding = new Thickness(11, 9, 11, 9),
                Background = new SolidColorBrush(WpfColor.FromArgb(248, 13, 27, 35)),
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(150, 118, 223, 238)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = panel
            }
        };
    }
}
