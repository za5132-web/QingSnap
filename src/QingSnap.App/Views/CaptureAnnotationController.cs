using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using QingSnap.App.Models;
using System.Windows.Markup;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using ShapeRectangle = System.Windows.Shapes.Rectangle;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using WpfCursors = System.Windows.Input.Cursors;
using WpfPanel = System.Windows.Controls.Panel;

namespace QingSnap.App.Views;

internal sealed class CaptureAnnotationController
{
    private readonly Canvas _layer;
    private readonly ScreenSnapshot _snapshot;
    private readonly List<FrameworkElement> _annotations = [];
    private WpfRect _selection;
    private double _surfaceWidth;
    private double _surfaceHeight;
    private WpfPoint _start;
    private FrameworkElement? _workingElement;
    private TextBox? _textEditor;
    private bool _isCommittingText;
    private Brush _annotationBrush;
    private readonly Brush _accentBrush = CreateBrush(Color.FromRgb(118, 223, 238));
    private double _strokeThickness;
    private double _fontSize;
    private int _numberSequence = 1;
    private FrameworkElement? _selectedElement;
    private WpfPoint _moveOrigin;
    private string? _copiedAnnotationXaml;
    private SelectionManipulationMode _selectionManipulation;
    private Ellipse? _startHandle;
    private Ellipse? _endHandle;
    private ShapeRectangle? _selectionFrame;
    private readonly Dictionary<SelectionManipulationMode, ShapeRectangle> _resizeHandles = [];
    private WpfRect _moveBounds;
    private WpfRect _resizeOriginalBounds;
    private PointCollection? _resizeOriginalPoints;
    private double _resizeOriginalFontSize;
    private double _resizeOriginalMaxWidth;
    private TextBlock? _editingTextBlock;

    public CaptureAnnotationController(Canvas layer, ScreenSnapshot snapshot, AppSettings settings)
    {
        _layer = layer;
        _snapshot = snapshot;
        _annotationBrush = CreateBrush(ParseColor(settings.AnnotationColor));
        _strokeThickness = settings.AnnotationThickness;
        _fontSize = settings.AnnotationFontSize;
    }

    public event EventHandler? Changed;

    public CaptureAnnotationTool ActiveTool { get; set; }

    public bool HasAnnotations => _annotations.Count > 0;

    public bool IsDrawing => _workingElement is not null && ActiveTool != CaptureAnnotationTool.Text;

    public bool IsAdjustingEndpoint =>
        ActiveTool == CaptureAnnotationTool.Select &&
        _workingElement is not null &&
        _selectionManipulation != SelectionManipulationMode.Move;

    public bool HasSelection => _selectedElement is not null;

    public bool CanEditSelectedText => _selectedElement is TextBlock;

    public Color CurrentColor => (_annotationBrush as SolidColorBrush)?.Color ?? Color.FromRgb(255, 78, 91);

    public double CurrentThickness => _strokeThickness;

    public double CurrentFontSize => _fontSize;

    public void SetColor(Color color) => _annotationBrush = CreateBrush(color);

    public System.Windows.Input.Cursor GetSelectionCursorAt(WpfPoint surfacePoint)
    {
        if (!_selection.Contains(surfacePoint))
        {
            return WpfCursors.Arrow;
        }

        var mode = HitSelectionHandle(ToLocal(surfacePoint));
        return mode switch
        {
            SelectionManipulationMode.ResizeTopLeft or SelectionManipulationMode.ResizeBottomRight => WpfCursors.SizeNWSE,
            SelectionManipulationMode.ResizeTopRight or SelectionManipulationMode.ResizeBottomLeft => WpfCursors.SizeNESW,
            SelectionManipulationMode.StartPoint or SelectionManipulationMode.EndPoint => WpfCursors.Cross,
            _ => WpfCursors.SizeAll
        };
    }

    public void SetBounds(WpfRect selection, double surfaceWidth, double surfaceHeight)
    {
        _selection = selection;
        _surfaceWidth = surfaceWidth;
        _surfaceHeight = surfaceHeight;
        if (selection.IsEmpty)
        {
            Canvas.SetLeft(_layer, 0);
            Canvas.SetTop(_layer, 0);
            _layer.Width = 0;
            _layer.Height = 0;
            _layer.Clip = null;
            _layer.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(_layer, selection.Left);
        Canvas.SetTop(_layer, selection.Top);
        _layer.Width = Math.Max(0, selection.Width);
        _layer.Height = Math.Max(0, selection.Height);
        _layer.Clip = new RectangleGeometry(new WpfRect(0, 0, _layer.Width, _layer.Height));
        _layer.Visibility = selection.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    public bool Begin(WpfPoint surfacePoint)
    {
        if (ActiveTool == CaptureAnnotationTool.None || !_selection.Contains(surfacePoint))
        {
            return false;
        }

        CommitText();
        if (ActiveTool != CaptureAnnotationTool.Select)
        {
            ClearSelection();
        }
        _start = ToLocal(surfacePoint);
        switch (ActiveTool)
        {
            case CaptureAnnotationTool.Pen:
                {
                    var polyline = new Polyline
                    {
                        Stroke = _annotationBrush,
                        StrokeThickness = _strokeThickness,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        IsHitTestVisible = false
                    };
                    polyline.Points.Add(_start);
                    _workingElement = polyline;
                    break;
                }
            case CaptureAnnotationTool.Arrow:
            case CaptureAnnotationTool.DoubleArrow:
                {
                    _workingElement = new Canvas
                    {
                        Width = _layer.Width,
                        Height = _layer.Height,
                        Tag = ActiveTool == CaptureAnnotationTool.DoubleArrow ? "DoubleArrow" : "Arrow",
                        IsHitTestVisible = false
                    };
                    break;
                }
            case CaptureAnnotationTool.Line:
                _workingElement = new Line
                {
                    Stroke = _annotationBrush,
                    StrokeThickness = _strokeThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                };
                break;
            case CaptureAnnotationTool.Rectangle:
                _workingElement = new ShapeRectangle
                {
                    Stroke = _annotationBrush,
                    StrokeThickness = _strokeThickness,
                    RadiusX = 2,
                    RadiusY = 2,
                    IsHitTestVisible = false
                };
                break;
            case CaptureAnnotationTool.Ellipse:
                _workingElement = new Ellipse
                {
                    Stroke = _annotationBrush,
                    StrokeThickness = _strokeThickness,
                    IsHitTestVisible = false
                };
                break;
            case CaptureAnnotationTool.Highlight:
                _workingElement = new ShapeRectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(92, ParseColor(_annotationBrush.ToString()).R, ParseColor(_annotationBrush.ToString()).G, ParseColor(_annotationBrush.ToString()).B)),
                    IsHitTestVisible = false
                };
                break;
            case CaptureAnnotationTool.Mosaic:
            case CaptureAnnotationTool.Blur:
                _workingElement = new ShapeRectangle
                {
                    Stroke = _accentBrush,
                    StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection([4, 3]),
                    Fill = new SolidColorBrush(Color.FromArgb(34, 118, 223, 238)),
                    IsHitTestVisible = false
                };
                break;
            case CaptureAnnotationTool.Text:
                BeginText(_start);
                return true;
            case CaptureAnnotationTool.Number:
                AddNumber(_start);
                return true;
            case CaptureAnnotationTool.Select:
                return BeginSelection(_start);
            default:
                return false;
        }

        _layer.Children.Add(_workingElement);
        Update(surfacePoint);
        return true;
    }

    public void Update(WpfPoint surfacePoint)
    {
        if (_workingElement is null)
        {
            return;
        }

        var current = ClampLocal(ToLocal(surfacePoint));
        switch (ActiveTool)
        {
            case CaptureAnnotationTool.Pen when _workingElement is Polyline polyline:
                if (polyline.Points.Count == 0 ||
                    (polyline.Points[^1] - current).Length >= 1.5)
                {
                    polyline.Points.Add(current);
                }
                break;
            case CaptureAnnotationTool.Arrow or CaptureAnnotationTool.DoubleArrow
                when _workingElement is Canvas arrow:
                UpdateArrow(arrow, _start, current);
                break;
            case CaptureAnnotationTool.Line when _workingElement is Line line:
                line.X1 = _start.X;
                line.Y1 = _start.Y;
                line.X2 = current.X;
                line.Y2 = current.Y;
                break;
            case CaptureAnnotationTool.Rectangle:
            case CaptureAnnotationTool.Ellipse:
            case CaptureAnnotationTool.Highlight:
            case CaptureAnnotationTool.Mosaic:
            case CaptureAnnotationTool.Blur:
                PositionRect(_workingElement, Normalize(_start, current));
                break;
            case CaptureAnnotationTool.Select:
                if (_selectionManipulation == SelectionManipulationMode.Move)
                {
                    var deltaX = current.X - _start.X;
                    var deltaY = current.Y - _start.Y;
                    deltaX = Math.Clamp(deltaX, -_moveBounds.Left, _layer.Width - _moveBounds.Right);
                    deltaY = Math.Clamp(deltaY, -_moveBounds.Top, _layer.Height - _moveBounds.Bottom);
                    Canvas.SetLeft(_workingElement, _moveOrigin.X + deltaX);
                    Canvas.SetTop(_workingElement, _moveOrigin.Y + deltaY);
                }
                else if (IsResizeMode(_selectionManipulation))
                {
                    ResizeSelected(current);
                }
                else
                {
                    UpdateSelectedEndpoint(
                        _workingElement,
                        current,
                        _selectionManipulation == SelectionManipulationMode.StartPoint);
                }

                UpdateSelectionAdorners();
                break;
        }
    }

    public void End(WpfPoint surfacePoint)
    {
        if (_workingElement is null)
        {
            return;
        }

        Update(surfacePoint);
        var completed = _workingElement;
        _workingElement = null;

        if (ActiveTool == CaptureAnnotationTool.Select)
        {
            _selectionManipulation = SelectionManipulationMode.Move;
            UpdateSelectionAdorners();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (ActiveTool is CaptureAnnotationTool.Mosaic or CaptureAnnotationTool.Blur)
        {
            var rect = GetElementRect(completed);
            _layer.Children.Remove(completed);
            if (rect.Width >= 4 && rect.Height >= 4)
            {
                var processed = ActiveTool == CaptureAnnotationTool.Mosaic
                    ? CreateMosaic(rect)
                    : CreateBlur(rect);
                _layer.Children.Add(processed);
                _annotations.Add(processed);
                RefreshAnnotationZOrder();
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (!IsMeaningful(completed))
        {
            _layer.Children.Remove(completed);
            return;
        }

        _annotations.Add(completed);
        RefreshAnnotationZOrder();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        CommitText();
        if (_annotations.Count == 0)
        {
            return;
        }

        var last = _annotations[^1];
        _annotations.RemoveAt(_annotations.Count - 1);
        _layer.Children.Remove(last);
        if (ReferenceEquals(last, _selectedElement))
        {
            ClearSelection();
        }
        RefreshAnnotationZOrder();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        CancelWorkingElement();
        CancelText();
        HideDirectionHandles();
        HideResizeAdorners();
        foreach (var annotation in _annotations)
        {
            _layer.Children.Remove(annotation);
        }

        _annotations.Clear();
        _selectedElement = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool CommitText()
    {
        if (_textEditor is null || _isCommittingText)
        {
            return false;
        }

        _isCommittingText = true;
        var editor = _textEditor;
        _textEditor = null;
        var text = editor.Text.Trim();
        var left = Canvas.GetLeft(editor);
        var top = Canvas.GetTop(editor);
        _layer.Children.Remove(editor);

        if (_editingTextBlock is { } existing)
        {
            if (string.IsNullOrEmpty(text))
            {
                _annotations.Remove(existing);
                _layer.Children.Remove(existing);
                _selectedElement = null;
            }
            else
            {
                existing.Text = text;
                existing.FontSize = editor.FontSize;
                existing.Foreground = editor.Foreground;
                existing.MaxWidth = editor.MaxWidth;
                Canvas.SetLeft(existing, left);
                Canvas.SetTop(existing, top);
                existing.Visibility = Visibility.Visible;
                _selectedElement = existing;
            }

            _editingTextBlock = null;
            UpdateSelectionAdorners();
        }
        else if (!string.IsNullOrEmpty(text))
        {
            var label = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = _fontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = _annotationBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = Math.Max(80, _layer.Width - left),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 2,
                    ShadowDepth = 1,
                    Opacity = 0.9
                },
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
            _layer.Children.Add(label);
            _annotations.Add(label);
            RefreshAnnotationZOrder();
        }

        _isCommittingText = false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void BeginText(WpfPoint point, TextBlock? existing = null)
    {
        var editorFontSize = existing?.FontSize ?? _fontSize;
        var editorBrush = existing?.Foreground ?? _annotationBrush;
        var editorMaxWidth = existing?.MaxWidth ?? Math.Max(140, _layer.Width - point.X);
        var editor = new TextBox
        {
            Text = existing?.Text ?? string.Empty,
            MinWidth = Math.Max(140, existing?.ActualWidth + 18 ?? 140),
            MaxWidth = Math.Max(140, editorMaxWidth),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = editorFontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = editorBrush,
            CaretBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(225, 13, 23, 30)),
            BorderBrush = _accentBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 3, 6, 3),
            AcceptsReturn = false
        };
        editor.PreviewKeyDown += OnTextEditorKeyDown;
        editor.LostKeyboardFocus += (_, _) => CommitText();
        InputMethod.SetIsInputMethodEnabled(editor, true);
        Canvas.SetLeft(editor, point.X);
        Canvas.SetTop(editor, point.Y);
        _layer.Children.Add(editor);
        _textEditor = editor;
        _editingTextBlock = existing;
        if (existing is not null)
        {
            existing.Visibility = Visibility.Collapsed;
            HideSelectionAdorners();
        }
        editor.Focus();
        Keyboard.Focus(editor);
        editor.SelectAll();
    }

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelText();
            e.Handled = true;
        }
    }

    private void CancelText()
    {
        if (_textEditor is null)
        {
            return;
        }

        _layer.Children.Remove(_textEditor);
        _textEditor = null;
        if (_editingTextBlock is not null)
        {
            _editingTextBlock.Visibility = Visibility.Visible;
            _editingTextBlock = null;
            UpdateSelectionAdorners();
        }
    }

    private void CancelWorkingElement()
    {
        if (_workingElement is null)
        {
            return;
        }

        _layer.Children.Remove(_workingElement);
        _workingElement = null;
        HideDirectionHandles();
    }

    private Image CreateMosaic(WpfRect localRect)
    {
        var scaleX = _snapshot.Image.PixelWidth / Math.Max(1, _surfaceWidth);
        var scaleY = _snapshot.Image.PixelHeight / Math.Max(1, _surfaceHeight);
        var x = Math.Clamp(
            (int)Math.Floor((_selection.Left + localRect.Left) * scaleX),
            0,
            _snapshot.Image.PixelWidth - 1);
        var y = Math.Clamp(
            (int)Math.Floor((_selection.Top + localRect.Top) * scaleY),
            0,
            _snapshot.Image.PixelHeight - 1);
        var right = Math.Clamp(
            (int)Math.Ceiling((_selection.Left + localRect.Right) * scaleX),
            x + 1,
            _snapshot.Image.PixelWidth);
        var bottom = Math.Clamp(
            (int)Math.Ceiling((_selection.Top + localRect.Bottom) * scaleY),
            y + 1,
            _snapshot.Image.PixelHeight);
        var crop = new CroppedBitmap(_snapshot.Image, new Int32Rect(x, y, right - x, bottom - y));
        var pixelated = Pixelate(crop, 12);
        var image = new Image
        {
            Source = pixelated,
            Width = localRect.Width,
            Height = localRect.Height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, localRect.Left);
        Canvas.SetTop(image, localRect.Top);
        return image;
    }

    private Image CreateBlur(WpfRect localRect)
    {
        var scaleX = _snapshot.Image.PixelWidth / Math.Max(1, _surfaceWidth);
        var scaleY = _snapshot.Image.PixelHeight / Math.Max(1, _surfaceHeight);
        var x = Math.Clamp((int)Math.Floor((_selection.Left + localRect.Left) * scaleX), 0, _snapshot.Image.PixelWidth - 1);
        var y = Math.Clamp((int)Math.Floor((_selection.Top + localRect.Top) * scaleY), 0, _snapshot.Image.PixelHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling((_selection.Left + localRect.Right) * scaleX), x + 1, _snapshot.Image.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((_selection.Top + localRect.Bottom) * scaleY), y + 1, _snapshot.Image.PixelHeight);
        var image = new Image
        {
            Source = new CroppedBitmap(_snapshot.Image, new Int32Rect(x, y, right - x, bottom - y)),
            Width = localRect.Width,
            Height = localRect.Height,
            Stretch = Stretch.Fill,
            Effect = new BlurEffect { Radius = 12, KernelType = KernelType.Gaussian },
            IsHitTestVisible = false
        };
        Canvas.SetLeft(image, localRect.Left);
        Canvas.SetTop(image, localRect.Top);
        return image;
    }

    private void AddNumber(WpfPoint point)
    {
        var badge = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = _annotationBrush,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1.5),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = (_numberSequence++).ToString(),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(badge, Math.Clamp(point.X - 14, 0, Math.Max(0, _layer.Width - 28)));
        Canvas.SetTop(badge, Math.Clamp(point.Y - 14, 0, Math.Max(0, _layer.Height - 28)));
        _layer.Children.Add(badge);
        _annotations.Add(badge);
        RefreshAnnotationZOrder();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsEndpointAt(WpfPoint surfacePoint)
    {
        if (!_selection.Contains(surfacePoint))
        {
            return false;
        }

        var point = ToLocal(surfacePoint);
        return _annotations.AsEnumerable().Reverse().Any(annotation =>
            TryGetAnnotationEndpoints(annotation, out var start, out var end) &&
            ((start - point).Length <= 11 || (end - point).Length <= 11));
    }

    private bool BeginSelection(WpfPoint point)
    {
        if (_selectedElement is not null)
        {
            var handleMode = HitSelectionHandle(point);
            if (handleMode != SelectionManipulationMode.Move)
            {
                _selectionManipulation = handleMode;
                PrepareSelectionManipulation();
                _workingElement = _selectedElement;
                return true;
            }
        }

        var hit = _annotations.LastOrDefault(annotation =>
        {
            var bounds = GetVisualBounds(annotation);
            var padding = TryGetAnnotationEndpoints(annotation, out _, out _) ? 12 : 7;
            bounds.Inflate(padding, padding);
            return bounds.Contains(point);
        });
        if (hit is null)
        {
            ClearSelection();
            return false;
        }

        _selectedElement = hit;
        _moveOrigin = new WpfPoint(
            SafeCanvasPosition(Canvas.GetLeft(_selectedElement)),
            SafeCanvasPosition(Canvas.GetTop(_selectedElement)));
        _moveBounds = GetVisualBounds(_selectedElement);
        _selectionManipulation = SelectionManipulationMode.Move;
        if (TryGetAnnotationEndpoints(_selectedElement, out var start, out var end))
        {
            var startDistance = (start - point).Length;
            var endDistance = (end - point).Length;
            if (Math.Min(startDistance, endDistance) <= 12)
            {
                _selectionManipulation = startDistance <= endDistance
                    ? SelectionManipulationMode.StartPoint
                    : SelectionManipulationMode.EndPoint;
            }

        }

        PrepareSelectionManipulation();
        UpdateSelectionAdorners();
        _workingElement = _selectedElement;
        return true;
    }

    public bool SelectAt(WpfPoint surfacePoint)
    {
        if (!_selection.Contains(surfacePoint))
        {
            return false;
        }

        var local = ToLocal(surfacePoint);
        var hit = _annotations.LastOrDefault(annotation =>
        {
            var bounds = GetVisualBounds(annotation);
            bounds.Inflate(TryGetAnnotationEndpoints(annotation, out _, out _) ? 12 : 7, 7);
            return bounds.Contains(local);
        });
        if (hit is null)
        {
            return false;
        }

        _selectedElement = hit;
        UpdateSelectionAdorners();
        return true;
    }

    public bool BeginEditSelectedText()
    {
        if (_selectedElement is not TextBlock text)
        {
            return false;
        }

        CommitText();
        var point = new WpfPoint(
            SafeCanvasPosition(Canvas.GetLeft(text)),
            SafeCanvasPosition(Canvas.GetTop(text)));
        BeginText(point, text);
        return true;
    }

    public bool BeginEditTextAt(WpfPoint surfacePoint)
    {
        if (!SelectAt(surfacePoint) || _selectedElement is not TextBlock)
        {
            return false;
        }

        return BeginEditSelectedText();
    }

    private void PrepareSelectionManipulation()
    {
        if (_selectedElement is null)
        {
            return;
        }

        _moveOrigin = new WpfPoint(
            SafeCanvasPosition(Canvas.GetLeft(_selectedElement)),
            SafeCanvasPosition(Canvas.GetTop(_selectedElement)));
        _moveBounds = GetVisualBounds(_selectedElement);
        _resizeOriginalBounds = GetResizeBounds(_selectedElement);
        _resizeOriginalPoints = _selectedElement is Polyline polyline
            ? new PointCollection(polyline.Points)
            : null;
        if (_selectedElement is TextBlock text)
        {
            _resizeOriginalFontSize = text.FontSize;
            _resizeOriginalMaxWidth = text.MaxWidth;
        }
    }

    private void ResizeSelected(WpfPoint current)
    {
        if (_selectedElement is null || _resizeOriginalBounds.IsEmpty)
        {
            return;
        }

        var opposite = _selectionManipulation switch
        {
            SelectionManipulationMode.ResizeTopLeft => _resizeOriginalBounds.BottomRight,
            SelectionManipulationMode.ResizeTopRight => _resizeOriginalBounds.BottomLeft,
            SelectionManipulationMode.ResizeBottomLeft => _resizeOriginalBounds.TopRight,
            _ => _resizeOriginalBounds.TopLeft
        };
        var rect = Normalize(opposite, current);
        if (rect.Width < 8 || rect.Height < 8)
        {
            return;
        }

        rect.Intersect(new WpfRect(0, 0, _layer.Width, _layer.Height));
        if (rect.Width < 8 || rect.Height < 8)
        {
            return;
        }

        switch (_selectedElement)
        {
            case Polyline polyline when _resizeOriginalPoints is not null:
                var scaleX = rect.Width / Math.Max(1, _resizeOriginalBounds.Width);
                var scaleY = rect.Height / Math.Max(1, _resizeOriginalBounds.Height);
                polyline.Points = new PointCollection(_resizeOriginalPoints.Select(point => new WpfPoint(
                    rect.Left + (point.X - _resizeOriginalBounds.Left) * scaleX,
                    rect.Top + (point.Y - _resizeOriginalBounds.Top) * scaleY)));
                break;
            case TextBlock text:
                var scale = Math.Max(
                    rect.Width / Math.Max(1, _resizeOriginalBounds.Width),
                    rect.Height / Math.Max(1, _resizeOriginalBounds.Height));
                text.FontSize = Math.Clamp(_resizeOriginalFontSize * scale, 8, 96);
                text.MaxWidth = Math.Max(60, _resizeOriginalMaxWidth * scale);
                Canvas.SetLeft(text, rect.Left);
                Canvas.SetTop(text, rect.Top);
                break;
            case Border border when border.Child is TextBlock:
                var size = Math.Max(18, Math.Min(rect.Width, rect.Height));
                var left = _selectionManipulation is SelectionManipulationMode.ResizeTopLeft or SelectionManipulationMode.ResizeBottomLeft
                    ? opposite.X - size
                    : opposite.X;
                var top = _selectionManipulation is SelectionManipulationMode.ResizeTopLeft or SelectionManipulationMode.ResizeTopRight
                    ? opposite.Y - size
                    : opposite.Y;
                PositionRect(border, new WpfRect(left, top, size, size));
                if (border.Child is TextBlock badgeText)
                {
                    badgeText.FontSize = Math.Clamp(size * 0.43, 9, 42);
                }
                break;
            default:
                PositionRect(_selectedElement, rect);
                break;
        }
    }

    private static bool TryGetAnnotationEndpoints(
        FrameworkElement annotation,
        out WpfPoint start,
        out WpfPoint end)
    {
        var line = annotation switch
        {
            Line directLine => directLine,
            Canvas arrowCanvas => arrowCanvas.Children.OfType<Line>().FirstOrDefault(),
            _ => null
        };
        if (line is null)
        {
            start = default;
            end = default;
            return false;
        }

        var offsetX = SafeCanvasPosition(Canvas.GetLeft(annotation));
        var offsetY = SafeCanvasPosition(Canvas.GetTop(annotation));
        start = new WpfPoint(line.X1 + offsetX, line.Y1 + offsetY);
        end = new WpfPoint(line.X2 + offsetX, line.Y2 + offsetY);
        return true;
    }

    private static void UpdateSelectedEndpoint(
        FrameworkElement annotation,
        WpfPoint layerPoint,
        bool updateStart)
    {
        var line = annotation switch
        {
            Line directLine => directLine,
            Canvas arrowElement => arrowElement.Children.OfType<Line>().FirstOrDefault(),
            _ => null
        };
        if (line is null)
        {
            return;
        }

        var localPoint = new WpfPoint(
            layerPoint.X - SafeCanvasPosition(Canvas.GetLeft(annotation)),
            layerPoint.Y - SafeCanvasPosition(Canvas.GetTop(annotation)));
        if (updateStart)
        {
            line.X1 = localPoint.X;
            line.Y1 = localPoint.Y;
        }
        else
        {
            line.X2 = localPoint.X;
            line.Y2 = localPoint.Y;
        }

        if (annotation is Canvas arrow && arrow.Children.OfType<Polygon>().FirstOrDefault() is { } body)
        {
            UpdateArrowBody(line, body, IsDoubleArrow(arrow));
        }
    }

    private void ShowDirectionHandles(FrameworkElement annotation)
    {
        HideDirectionHandles();
        _startHandle = CreateDirectionHandle();
        _endHandle = CreateDirectionHandle();
        _layer.Children.Add(_startHandle);
        _layer.Children.Add(_endHandle);
        System.Windows.Controls.Panel.SetZIndex(_startHandle, 2000);
        System.Windows.Controls.Panel.SetZIndex(_endHandle, 2000);
        UpdateDirectionHandles(annotation);
    }

    private void UpdateDirectionHandles(FrameworkElement annotation)
    {
        if (_startHandle is null || _endHandle is null ||
            !TryGetAnnotationEndpoints(annotation, out var start, out var end))
        {
            return;
        }

        PositionDirectionHandle(_startHandle, start);
        PositionDirectionHandle(_endHandle, end);
    }

    private static Ellipse CreateDirectionHandle() => new()
    {
        Width = 10,
        Height = 10,
        Fill = new SolidColorBrush(Color.FromRgb(14, 23, 31)),
        Stroke = new SolidColorBrush(Color.FromRgb(118, 223, 238)),
        StrokeThickness = 2,
        IsHitTestVisible = false
    };

    private static void PositionDirectionHandle(Ellipse handle, WpfPoint point)
    {
        Canvas.SetLeft(handle, point.X - handle.Width / 2);
        Canvas.SetTop(handle, point.Y - handle.Height / 2);
    }

    private void HideDirectionHandles()
    {
        if (_startHandle is not null)
        {
            _layer.Children.Remove(_startHandle);
            _startHandle = null;
        }

        if (_endHandle is not null)
        {
            _layer.Children.Remove(_endHandle);
            _endHandle = null;
        }
    }

    private void ShowResizeAdorners(FrameworkElement annotation)
    {
        HideResizeAdorners();
        _selectionFrame = new ShapeRectangle
        {
            Stroke = _accentBrush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection([4, 3]),
            Fill = Brushes.Transparent,
            IsHitTestVisible = false
        };
        _layer.Children.Add(_selectionFrame);
        WpfPanel.SetZIndex(_selectionFrame, 10000);

        foreach (var mode in new[]
                 {
                     SelectionManipulationMode.ResizeTopLeft,
                     SelectionManipulationMode.ResizeTopRight,
                     SelectionManipulationMode.ResizeBottomLeft,
                     SelectionManipulationMode.ResizeBottomRight
                 })
        {
            var handle = new ShapeRectangle
            {
                Width = 9,
                Height = 9,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Color.FromRgb(14, 23, 31)),
                Stroke = _accentBrush,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            _resizeHandles[mode] = handle;
            _layer.Children.Add(handle);
            WpfPanel.SetZIndex(handle, 10001);
        }

        UpdateResizeAdorners(annotation);
    }

    private void UpdateResizeAdorners(FrameworkElement annotation)
    {
        if (_selectionFrame is null)
        {
            return;
        }

        var bounds = GetVisualBounds(annotation);
        bounds.Inflate(3, 3);
        PositionRect(_selectionFrame, bounds);
        PositionResizeHandle(SelectionManipulationMode.ResizeTopLeft, bounds.TopLeft);
        PositionResizeHandle(SelectionManipulationMode.ResizeTopRight, bounds.TopRight);
        PositionResizeHandle(SelectionManipulationMode.ResizeBottomLeft, bounds.BottomLeft);
        PositionResizeHandle(SelectionManipulationMode.ResizeBottomRight, bounds.BottomRight);
    }

    private void PositionResizeHandle(SelectionManipulationMode mode, WpfPoint point)
    {
        if (!_resizeHandles.TryGetValue(mode, out var handle))
        {
            return;
        }

        Canvas.SetLeft(handle, point.X - handle.Width / 2);
        Canvas.SetTop(handle, point.Y - handle.Height / 2);
    }

    private void HideResizeAdorners()
    {
        if (_selectionFrame is not null)
        {
            _layer.Children.Remove(_selectionFrame);
            _selectionFrame = null;
        }

        foreach (var handle in _resizeHandles.Values)
        {
            _layer.Children.Remove(handle);
        }
        _resizeHandles.Clear();
    }

    private void HideSelectionAdorners()
    {
        HideDirectionHandles();
        HideResizeAdorners();
    }

    private void UpdateSelectionAdorners()
    {
        HideSelectionAdorners();
        if (_selectedElement is null || ActiveTool != CaptureAnnotationTool.Select || _textEditor is not null)
        {
            return;
        }

        if (TryGetAnnotationEndpoints(_selectedElement, out _, out _))
        {
            ShowDirectionHandles(_selectedElement);
        }
        else
        {
            ShowResizeAdorners(_selectedElement);
        }
    }

    private SelectionManipulationMode HitSelectionHandle(WpfPoint localPoint)
    {
        if (_selectedElement is null)
        {
            return SelectionManipulationMode.Move;
        }

        if (TryGetAnnotationEndpoints(_selectedElement, out var start, out var end))
        {
            var startDistance = (start - localPoint).Length;
            var endDistance = (end - localPoint).Length;
            if (Math.Min(startDistance, endDistance) <= 12)
            {
                return startDistance <= endDistance
                    ? SelectionManipulationMode.StartPoint
                    : SelectionManipulationMode.EndPoint;
            }
        }

        foreach (var (mode, handle) in _resizeHandles)
        {
            var center = new WpfPoint(
                SafeCanvasPosition(Canvas.GetLeft(handle)) + handle.Width / 2,
                SafeCanvasPosition(Canvas.GetTop(handle)) + handle.Height / 2);
            if ((center - localPoint).Length <= 12)
            {
                return mode;
            }
        }

        return SelectionManipulationMode.Move;
    }

    private static bool IsResizeMode(SelectionManipulationMode mode) => mode is
        SelectionManipulationMode.ResizeTopLeft or
        SelectionManipulationMode.ResizeTopRight or
        SelectionManipulationMode.ResizeBottomLeft or
        SelectionManipulationMode.ResizeBottomRight;

    public void ClearSelection()
    {
        if (_workingElement is not null && ActiveTool == CaptureAnnotationTool.Select)
        {
            _workingElement = null;
        }
        HideSelectionAdorners();
        _selectedElement = null;
        _selectionManipulation = SelectionManipulationMode.Move;
    }

    public void SetSelectionAdornersVisible(bool visible)
    {
        if (visible)
        {
            UpdateSelectionAdorners();
        }
        else
        {
            HideSelectionAdorners();
        }
    }

    public void DeleteSelected()
    {
        if (_selectedElement is null)
        {
            return;
        }

        _annotations.Remove(_selectedElement);
        _layer.Children.Remove(_selectedElement);
        ClearSelection();
        RefreshAnnotationZOrder();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CopySelected()
    {
        if (_selectedElement is null)
        {
            return;
        }

        try
        {
            _copiedAnnotationXaml = XamlWriter.Save(_selectedElement);
        }
        catch (InvalidOperationException)
        {
            _copiedAnnotationXaml = null;
        }
    }

    public void PasteSelected()
    {
        if (string.IsNullOrWhiteSpace(_copiedAnnotationXaml))
        {
            return;
        }

        try
        {
            if (XamlReader.Parse(_copiedAnnotationXaml) is not FrameworkElement clone)
            {
                return;
            }

            clone.IsHitTestVisible = false;
            Canvas.SetLeft(clone, SafeCanvasPosition(Canvas.GetLeft(clone)) + 12);
            Canvas.SetTop(clone, SafeCanvasPosition(Canvas.GetTop(clone)) + 12);
            _layer.Children.Add(clone);
            _annotations.Add(clone);
            _selectedElement = clone;
            RefreshAnnotationZOrder();
            UpdateSelectionAdorners();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Xml.XmlException)
        {
        }
    }

    public bool BringSelectedForward() => ReorderSelected(1, false);

    public bool SendSelectedBackward() => ReorderSelected(-1, false);

    public bool BringSelectedToFront() => ReorderSelected(1, true);

    public bool SendSelectedToBack() => ReorderSelected(-1, true);

    private bool ReorderSelected(int direction, bool toEdge)
    {
        if (_selectedElement is null)
        {
            return false;
        }

        var index = _annotations.IndexOf(_selectedElement);
        if (index < 0)
        {
            return false;
        }

        var target = toEdge
            ? direction > 0 ? _annotations.Count - 1 : 0
            : Math.Clamp(index + direction, 0, _annotations.Count - 1);
        if (target == index)
        {
            return false;
        }

        _annotations.RemoveAt(index);
        _annotations.Insert(target, _selectedElement);
        RefreshAnnotationZOrder();
        UpdateSelectionAdorners();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void RefreshAnnotationZOrder()
    {
        for (var index = 0; index < _annotations.Count; index++)
        {
            WpfPanel.SetZIndex(_annotations[index], index);
        }
    }

    public string? AdjustAnnotationAt(WpfPoint surfacePoint, int wheelDelta)
    {
        if (wheelDelta == 0 || !_selection.Contains(surfacePoint))
        {
            return null;
        }

        CommitText();
        var localPoint = ToLocal(surfacePoint);
        var target = _annotations.LastOrDefault(annotation =>
        {
            var bounds = GetVisualBounds(annotation);
            bounds.Inflate(8, 8);
            return bounds.Contains(localPoint);
        });
        if (target is null)
        {
            return null;
        }

        var direction = Math.Sign(wheelDelta);
        string? result = target switch
        {
            TextBlock text => AdjustTextSize(text, direction),
            Polyline polyline => AdjustStroke(polyline, direction),
            Line line => AdjustStroke(line, direction),
            ShapeRectangle rectangle when rectangle.Stroke is not null => AdjustStroke(rectangle, direction),
            Ellipse ellipse => AdjustStroke(ellipse, direction),
            Canvas arrow => AdjustArrow(arrow, direction),
            Border badge when badge.Child is TextBlock => AdjustNumberBadge(badge, direction),
            _ => null
        };
        if (result is not null)
        {
            if (ReferenceEquals(target, _selectedElement))
            {
                UpdateSelectionAdorners();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public Color CycleColor()
    {
        Color[] colors =
        [
            Color.FromRgb(255, 78, 91),
            Color.FromRgb(118, 223, 238),
            Color.FromRgb(255, 208, 84),
            Color.FromRgb(115, 232, 139),
            Colors.White,
            Color.FromRgb(25, 25, 25)
        ];
        var current = (_annotationBrush as SolidColorBrush)?.Color ?? colors[0];
        var index = Array.FindIndex(colors, color => color == current);
        var next = colors[(index + 1 + colors.Length) % colors.Length];
        _annotationBrush = CreateBrush(next);
        return next;
    }

    public double CycleThickness()
    {
        double[] values = [2, 3, 5, 8];
        var index = Array.FindIndex(values, value => Math.Abs(value - _strokeThickness) < 0.1);
        _strokeThickness = values[(index + 1 + values.Length) % values.Length];
        return _strokeThickness;
    }

    public double CycleFontSize()
    {
        double[] values = [14, 18, 24, 32, 42];
        var index = Array.FindIndex(values, value => Math.Abs(value - _fontSize) < 0.1);
        _fontSize = values[(index + 1 + values.Length) % values.Length];
        return _fontSize;
    }

    private static BitmapSource Pixelate(BitmapSource source, int blockSize)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);

        for (var top = 0; top < converted.PixelHeight; top += blockSize)
        {
            var bottom = Math.Min(converted.PixelHeight, top + blockSize);
            for (var left = 0; left < converted.PixelWidth; left += blockSize)
            {
                var right = Math.Min(converted.PixelWidth, left + blockSize);
                long blue = 0;
                long green = 0;
                long red = 0;
                var count = 0;
                for (var y = top; y < bottom; y++)
                {
                    for (var x = left; x < right; x++)
                    {
                        var index = y * stride + x * 4;
                        blue += pixels[index];
                        green += pixels[index + 1];
                        red += pixels[index + 2];
                        count++;
                    }
                }

                var averageBlue = (byte)(blue / Math.Max(1, count));
                var averageGreen = (byte)(green / Math.Max(1, count));
                var averageRed = (byte)(red / Math.Max(1, count));
                for (var y = top; y < bottom; y++)
                {
                    for (var x = left; x < right; x++)
                    {
                        var index = y * stride + x * 4;
                        pixels[index] = averageBlue;
                        pixels[index + 1] = averageGreen;
                        pixels[index + 2] = averageRed;
                        pixels[index + 3] = 255;
                    }
                }
            }
        }

        var bitmap = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void UpdateArrow(Canvas arrow, WpfPoint start, WpfPoint end)
    {
        arrow.Children.Clear();
        var directionLine = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = _annotationBrush,
            StrokeThickness = _strokeThickness,
            Opacity = 0,
            IsHitTestVisible = false
        };
        var body = new Polygon
        {
            Fill = _annotationBrush,
            IsHitTestVisible = false
        };
        arrow.Children.Add(directionLine);
        arrow.Children.Add(body);
        UpdateArrowBody(directionLine, body, IsDoubleArrow(arrow));
    }

    private static void UpdateArrowBody(Line directionLine, Polygon body, bool doubleHeaded)
    {
        var start = new WpfPoint(directionLine.X1, directionLine.Y1);
        var end = new WpfPoint(directionLine.X2, directionLine.Y2);

        var direction = end - start;
        var length = direction.Length;
        if (length < 1)
        {
            body.Points.Clear();
            return;
        }

        direction.Normalize();
        var perpendicular = new Vector(-direction.Y, direction.X);
        var thickness = directionLine.StrokeThickness;
        var shaftHalfWidth = Math.Max(1.4, thickness * 0.7);
        var headHalfWidth = Math.Max(7, thickness * 2.5);
        var headLength = Math.Min(Math.Max(16, thickness * 4.2), length * (doubleHeaded ? 0.32 : 0.48));
        body.Fill = directionLine.Stroke;

        if (doubleHeaded)
        {
            var startNeck = start + direction * headLength;
            var endNeck = end - direction * headLength;
            body.Points = new PointCollection
            {
                start,
                startNeck - perpendicular * headHalfWidth,
                startNeck - perpendicular * shaftHalfWidth,
                endNeck - perpendicular * shaftHalfWidth,
                endNeck - perpendicular * headHalfWidth,
                end,
                endNeck + perpendicular * headHalfWidth,
                endNeck + perpendicular * shaftHalfWidth,
                startNeck + perpendicular * shaftHalfWidth,
                startNeck + perpendicular * headHalfWidth
            };
            return;
        }

        var tailHalfWidth = Math.Max(0.6, thickness * 0.2);
        var neck = end - direction * headLength;
        body.Points = new PointCollection
        {
            start - perpendicular * tailHalfWidth,
            neck - perpendicular * shaftHalfWidth,
            neck - perpendicular * headHalfWidth,
            end,
            neck + perpendicular * headHalfWidth,
            neck + perpendicular * shaftHalfWidth,
            start + perpendicular * tailHalfWidth
        };
    }

    private static bool IsDoubleArrow(Canvas arrow) =>
        string.Equals(arrow.Tag as string, "DoubleArrow", StringComparison.Ordinal);

    private static string AdjustTextSize(TextBlock text, int direction)
    {
        text.FontSize = Math.Clamp(text.FontSize + direction * 2, 10, 72);
        return $"文字  {text.FontSize:0} px";
    }

    private static string AdjustStroke(Shape shape, int direction)
    {
        shape.StrokeThickness = Math.Clamp(shape.StrokeThickness + direction, 1, 16);
        return $"线宽  {shape.StrokeThickness:0} px";
    }

    private static string? AdjustArrow(Canvas arrow, int direction)
    {
        var directionLine = arrow.Children.OfType<Line>().FirstOrDefault();
        var body = arrow.Children.OfType<Polygon>().FirstOrDefault();
        if (directionLine is null || body is null)
        {
            return null;
        }

        directionLine.StrokeThickness = Math.Clamp(directionLine.StrokeThickness + direction, 1, 16);
        UpdateArrowBody(directionLine, body, IsDoubleArrow(arrow));
        return $"线宽  {directionLine.StrokeThickness:0} px";
    }

    private static string AdjustNumberBadge(Border badge, int direction)
    {
        var oldSize = double.IsNaN(badge.Width) ? 28 : badge.Width;
        var size = Math.Clamp(oldSize + direction * 2, 20, 52);
        var delta = size - oldSize;
        Canvas.SetLeft(badge, SafeCanvasPosition(Canvas.GetLeft(badge)) - delta / 2);
        Canvas.SetTop(badge, SafeCanvasPosition(Canvas.GetTop(badge)) - delta / 2);
        badge.Width = size;
        badge.Height = size;
        badge.CornerRadius = new CornerRadius(size / 2);
        if (badge.Child is TextBlock label)
        {
            label.FontSize = Math.Max(11, size * 0.5);
        }

        return $"序号  {size:0} px";
    }

    private static bool IsMeaningful(FrameworkElement element) => element switch
    {
        Polyline line => line.Points.Count >= 2,
        Line line => new Vector(line.X2 - line.X1, line.Y2 - line.Y1).Length >= 4,
        Canvas arrow when arrow.Children.OfType<Line>().FirstOrDefault() is { } line =>
            new Vector(line.X2 - line.X1, line.Y2 - line.Y1).Length >= 4,
        ShapeRectangle rectangle => rectangle.Width >= 4 && rectangle.Height >= 4,
        Ellipse ellipse => ellipse.Width >= 4 && ellipse.Height >= 4,
        _ => true
    };

    private static WpfRect GetElementRect(FrameworkElement element) =>
        new(Canvas.GetLeft(element), Canvas.GetTop(element), element.Width, element.Height);

    private static void PositionRect(FrameworkElement element, WpfRect rect)
    {
        Canvas.SetLeft(element, rect.Left);
        Canvas.SetTop(element, rect.Top);
        element.Width = rect.Width;
        element.Height = rect.Height;
    }

    private static WpfRect Normalize(WpfPoint first, WpfPoint second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(second.X - first.X),
        Math.Abs(second.Y - first.Y));

    private WpfPoint ToLocal(WpfPoint point) =>
        new(point.X - _selection.Left, point.Y - _selection.Top);

    private WpfPoint ClampLocal(WpfPoint point) => new(
        Math.Clamp(point.X, 0, _layer.Width),
        Math.Clamp(point.Y, 0, _layer.Height));

    private static Brush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color ParseColor(string value)
    {
        try
        {
            return (Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
        }
        catch (FormatException)
        {
            return Color.FromRgb(255, 78, 91);
        }
    }

    private static WpfRect GetVisualBounds(FrameworkElement element)
    {
        var bounds = VisualTreeHelper.GetDescendantBounds(element);
        if (bounds.IsEmpty)
        {
            bounds = new WpfRect(0, 0, Math.Max(1, element.ActualWidth), Math.Max(1, element.ActualHeight));
        }

        bounds.Offset(
            SafeCanvasPosition(Canvas.GetLeft(element)),
            SafeCanvasPosition(Canvas.GetTop(element)));
        return bounds;
    }

    private static WpfRect GetResizeBounds(FrameworkElement element)
    {
        if (element is Polyline)
        {
            return GetVisualBounds(element);
        }

        var width = double.IsNaN(element.Width) || element.Width <= 0
            ? Math.Max(1, element.ActualWidth)
            : element.Width;
        var height = double.IsNaN(element.Height) || element.Height <= 0
            ? Math.Max(1, element.ActualHeight)
            : element.Height;
        return new WpfRect(
            SafeCanvasPosition(Canvas.GetLeft(element)),
            SafeCanvasPosition(Canvas.GetTop(element)),
            width,
            height);
    }

    private static double SafeCanvasPosition(double value) => double.IsNaN(value) ? 0 : value;
}

internal enum CaptureAnnotationTool
{
    None,
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
    Select
}

internal enum SelectionManipulationMode
{
    Move,
    StartPoint,
    EndPoint,
    ResizeTopLeft,
    ResizeTopRight,
    ResizeBottomLeft,
    ResizeBottomRight
}
