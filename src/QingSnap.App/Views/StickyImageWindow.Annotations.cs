using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Controls;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Wpf = System.Windows.Controls;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace QingSnap.App.Views;

public partial class StickyImageWindow
{
    private AppSettings _pinAnnotationSettings = new();
    private CaptureAnnotationController? _pinAnnotations;
    private readonly Wpf.Canvas _pinAnnotationLayer = new() { Background = Brushes.Transparent, Focusable = true };
    private readonly Dictionary<CaptureAnnotationTool, Wpf.Button> _pinToolButtons = [];
    private Popup? _pinAnnotationToolbar;
    private Wpf.Button? _pinUndoButton;
    private Wpf.Button? _pinRedoButton;
    private Wpf.StackPanel? _pinTextStyleRow;
    private Wpf.Button? _pinTextBoldButton;
    private Wpf.Button? _pinTextShadowButton;
    private bool _isEditingPin;
    private bool _isPinDrawing;
    private bool _isPinClosed;
    private System.Windows.Threading.DispatcherOperation? _pinToolbarReposition;
    private bool IsPinTextEditing => _isEditingPin && Keyboard.FocusedElement is Wpf.TextBox;

    private void InitializePinAnnotations(AppSettings settings)
    {
        _pinAnnotationSettings = settings;
        _pinAnnotationLayer.PreviewMouseLeftButtonDown += OnPinAnnotationDown;
        _pinAnnotationLayer.PreviewMouseMove += OnPinAnnotationMove;
        _pinAnnotationLayer.PreviewMouseLeftButtonUp += OnPinAnnotationUp;
        PreviewGotKeyboardFocus += (_, e) =>
        {
            if (!_isEditingPin) return;
            var text = e.NewFocus is Wpf.TextBox;
            NativeMethods.ImmAssociateContextEx(new WindowInteropHelper(this).Handle, nint.Zero,
                text ? NativeMethods.IaceDefault : 0);
        };
        LocationChanged += (_, _) => RepositionPinAnnotationToolbar();
        SizeChanged += (_, _) => RepositionPinAnnotationToolbar();
        Activated += (_, _) =>
        {
            if (_isEditingPin && _pinAnnotationToolbar is not null) _pinAnnotationToolbar.IsOpen = true;
        };
        Deactivated += (_, _) => ClosePinAnnotationToolbar();
    }

    private void OnAnnotateClick(object sender, RoutedEventArgs e)
    {
        if (_isEditingPin) FinishPinAnnotations();
        else BeginPinAnnotations();
    }

    private void BeginPinAnnotations()
    {
        if (_isCollapsed || _isDockTransitioning) return;
        CompletePinMotion();
        if (_pinAnnotations is null)
        {
            _pinAnnotations = new CaptureAnnotationController(_pinAnnotationLayer,
                new ScreenSnapshot(new System.Drawing.Rectangle(0, 0, _image.PixelWidth, _image.PixelHeight), _image),
                _pinAnnotationSettings);
            _pinAnnotations.SetBounds(new Rect(0, 0, _image.PixelWidth, _image.PixelHeight),
                _image.PixelWidth, _image.PixelHeight);
            _pinAnnotations.Changed += (_, _) =>
            {
                InvalidatePinRecognition();
                RefreshPinAnnotationButtons();
            };
        }
        _isEditingPin = true;
        InvalidatePinRecognition();
        SetPinAnnotationTool(CaptureAnnotationTool.Arrow);
        _pinAnnotations.SetSelectionAdornersVisible(true);
        AnnotateMenuItem.Header = "完成标注";
        AnnotateMenuItem.InputGestureText = "Esc";
        UpdateTextOverlayVisibility();
        EnsurePinAnnotationToolbar();
        _pinAnnotationToolbar!.IsOpen = true;
        Focus();
    }

    private void FinishPinAnnotations()
    {
        if (!_isEditingPin) return;
        EndPinAnnotationGesture();
        _pinAnnotations!.CommitText();
        _pinAnnotations.SetSelectionAdornersVisible(false);
        _isEditingPin = false;
        NativeMethods.ImmAssociateContextEx(new WindowInteropHelper(this).Handle, nint.Zero, 0);
        ClosePinAnnotationToolbar();
        AnnotateMenuItem.Header = "标注图片";
        AnnotateMenuItem.InputGestureText = "F2";
        UpdateTextOverlayVisibility();
        // Thumbnail is small; do not retain another full long-image bitmap.
        CollapsedThumbnail.Source = CreatePinOutput(thumbnail: true);
        ScheduleOcrPreload();
        Focus();
    }

    private void InvalidatePinRecognition()
    {
        _ocrCancellation?.Cancel();
        _ocrCancellation?.Dispose();
        _ocrCancellation = null;
        _ocrPreloadTask = null;
        _isOcrPreloadObserved = false;
        _ocrResult = null;
        _ocrWords = [];
        _selectedWordIndices.Clear();
        TextSelectionOverlay.Children.Clear();
        LongTextSelectionOverlay.Children.Clear();
        _qrCodeCancellation?.Cancel();
        QrCodeHotspotLayer.ClearResults();
        LongQrCodeHotspotLayer.ClearResults();
    }

    private void UpdatePinAnnotationHost()
    {
        var host = _isLongReaderMode ? LongPinAnnotationHost : PinAnnotationHost;
        var other = _isLongReaderMode ? PinAnnotationHost : LongPinAnnotationHost;
        other.Child = null;
        other.Visibility = Visibility.Collapsed;
        host.Visibility = _pinAnnotations is null ? Visibility.Collapsed : Visibility.Visible;
        host.Child = _pinAnnotationLayer;
        host.IsHitTestVisible = _isEditingPin;
        QrCodeHotspotLayer.IsHitTestVisible = !_isEditingPin;
        LongQrCodeHotspotLayer.IsHitTestVisible = !_isEditingPin;
    }

    private void SetPinAnnotationTool(CaptureAnnotationTool tool)
    {
        if (tool == CaptureAnnotationTool.Text &&
            (_pinAnnotations!.IsEditingText ||
             (_pinAnnotations.ActiveTool == CaptureAnnotationTool.Select && _pinAnnotations.HasSelectedText)))
        {
            RefreshPinAnnotationButtons();
            return;
        }
        EndPinAnnotationGesture();
        _pinAnnotations!.CommitText();
        _pinAnnotations.ActiveTool = tool;
        _pinAnnotationLayer.Cursor = tool == CaptureAnnotationTool.Text ? Cursors.IBeam : Cursors.Cross;
        RefreshPinAnnotationButtons();
    }

    private Point PinAnnotationPoint(MouseEventArgs e) => new(
        Math.Clamp(e.GetPosition(_pinAnnotationLayer).X, 0, _image.PixelWidth),
        Math.Clamp(e.GetPosition(_pinAnnotationLayer).Y, 0, _image.PixelHeight));

    private void OnPinAnnotationDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isEditingPin) return;
        for (var element = e.OriginalSource as DependencyObject;
             element is not null && element != _pinAnnotationLayer;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is Wpf.TextBox) return;
        }
        var point = PinAnnotationPoint(e);
        if (e.ClickCount == 2 && _pinAnnotations!.ActiveTool == CaptureAnnotationTool.Select &&
            _pinAnnotations.SelectAt(point))
        {
            EditSelectedPinAnnotation();
        }
        else if (_pinAnnotations!.Begin(point, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            if (_pinAnnotations.ActiveTool != CaptureAnnotationTool.Text)
            {
                _isPinDrawing = true;
                _pinAnnotationLayer.CaptureMouse();
                _pinAnnotationLayer.Focus();
            }
        }
        e.Handled = true;
        RefreshPinAnnotationButtons();
    }

    private void OnPinAnnotationMove(object sender, MouseEventArgs e)
    {
        if (!_isEditingPin) return;
        if (_isPinDrawing)
        {
            if (e.LeftButton == MouseButtonState.Pressed) _pinAnnotations!.Update(PinAnnotationPoint(e));
            else EndPinAnnotationGesture();
        }
        if (_pinAnnotations!.ActiveTool == CaptureAnnotationTool.Select)
            _pinAnnotationLayer.Cursor = _pinAnnotations.GetSelectionCursorAt(PinAnnotationPoint(e));
    }

    private void OnPinAnnotationUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPinDrawing) return;
        _pinAnnotations!.End(PinAnnotationPoint(e));
        _isPinDrawing = false;
        _pinAnnotationLayer.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void EndPinAnnotationGesture()
    {
        if (!_isPinDrawing) return;
        _pinAnnotations!.End(PinAnnotationPoint(new MouseEventArgs(Mouse.PrimaryDevice, 0)));
        _isPinDrawing = false;
        _pinAnnotationLayer.ReleaseMouseCapture();
    }

    private void EditSelectedPinAnnotation()
    {
        if (_pinAnnotations!.CanEditSelectedText) _pinAnnotations.BeginEditSelectedText();
        else if (_pinAnnotations.TryGetSelectedNumber(out var value))
        {
            var dialog = new NumberAnnotationEditWindow(value) { Owner = this };
            if (dialog.ShowDialog() == true) _pinAnnotations.SetSelectedNumber(dialog.Value);
        }
    }

    private bool HandlePinAnnotationKey(KeyEventArgs e)
    {
        var key = ResolveShortcutKey(e);
        if (!_isEditingPin)
        {
            if (key != Key.F2 || _isCollapsed || _isDockTransitioning) return false;
            BeginPinAnnotations();
        }
        else
        {
            var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (key)
            {
                case Key.Escape: case Key.Enter: FinishPinAnnotations(); break;
                case Key.F2:
                    if (_pinAnnotations!.ActiveTool == CaptureAnnotationTool.Select) EditSelectedPinAnnotation();
                    else SetPinAnnotationTool(CaptureAnnotationTool.Select);
                    break;
                case Key.Z when control && shift: _pinAnnotations!.Redo(); break;
                case Key.Z when control: _pinAnnotations!.Undo(); break;
                case Key.Y when control: _pinAnnotations!.Redo(); break;
                case Key.Delete: _pinAnnotations!.DeleteSelected(); break;
                case Key.C when control:
                    if (_pinAnnotations!.HasSelection) _pinAnnotations.CopySelected();
                    else CopyImage();
                    break;
                case Key.V when control: _pinAnnotations!.PasteSelected(); break;
                case Key.OemCloseBrackets when control:
                    if (shift) _pinAnnotations!.BringSelectedToFront(); else _pinAnnotations!.BringSelectedForward();
                    break;
                case Key.OemOpenBrackets when control:
                    if (shift) _pinAnnotations!.SendSelectedToBack(); else _pinAnnotations!.SendSelectedBackward();
                    break;
                case Key.W when !control: case Key.A when !control:
                case Key.S when !control: case Key.D when !control:
                    var step = shift ? 10 : 1;
                    _pinAnnotations!.NudgeSelection(key == Key.A ? -step : key == Key.D ? step : 0,
                        key == Key.W ? -step : key == Key.S ? step : 0);
                    break;
                case Key.M: FinishPinAnnotations(); ToggleCollapsed(); break;
                default: return false;
            }
        }
        e.Handled = true;
        return true;
    }

    private bool HandlePinAnnotationWheel(MouseWheelEventArgs e)
    {
        if (!_isEditingPin || IsPinTextEditing || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return false;
        var result = _pinAnnotations!.AdjustAnnotationAt(PinAnnotationPoint(e), e.Delta);
        if (result is null) return false;
        ShowFeedback(result);
        e.Handled = true;
        return true;
    }

    internal BitmapSource CreatePinOutput(bool thumbnail = false)
    {
        _pinAnnotations?.CommitText();
        if (_pinAnnotations?.HasAnnotations != true && !thumbnail) return _image;
        _pinAnnotations?.SetSelectionAdornersVisible(false);
        try
        {
            var ratio = thumbnail ? Math.Min(1, Math.Min(180D / _image.PixelWidth, 130D / _image.PixelHeight)) : 1;
            var width = Math.Max(1, (int)Math.Round(_image.PixelWidth * ratio));
            var height = Math.Max(1, (int)Math.Round(_image.PixelHeight * ratio));
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                var target = new Rect(0, 0, width, height);
                context.DrawImage(_image, target);
                if (_pinAnnotations?.HasAnnotations == true)
                {
                    _pinAnnotationLayer.Measure(new Size(_image.PixelWidth, _image.PixelHeight));
                    _pinAnnotationLayer.Arrange(new Rect(0, 0, _image.PixelWidth, _image.PixelHeight));
                    var brush = new VisualBrush(_pinAnnotationLayer)
                    {
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(0, 0, _image.PixelWidth, _image.PixelHeight),
                        Stretch = Stretch.Fill
                    };
                    context.DrawRectangle(brush, null, target);
                }
            }
            var output = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            output.Render(drawing);
            output.Freeze();
            return output;
        }
        finally { _pinAnnotations?.SetSelectionAdornersVisible(_isEditingPin); }
    }

    private async void OnSaveAnnotatedClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 图片|*.png", DefaultExt = ".png",
                FileName = $"QingSnap-标注-{DateTime.Now:yyyyMMdd-HHmmss}.png"
            };
            if (dialog.ShowDialog(this) != true) return;
            var image = CreatePinOutput();
            var path = dialog.FileName;
            await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
            });
            ShowFeedback("图片已另存");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinAnnotation", exception, "贴图另存失败。");
            ShowFeedback("另存失败，请重试");
        }
    }

    private void EnsurePinAnnotationToolbar()
    {
        if (_pinAnnotationToolbar is not null) return;
        var tools = new Wpf.WrapPanel { Width = 420 };
        foreach (var tool in Enum.GetValues<CaptureAnnotationTool>().Where(value => value != CaptureAnnotationTool.None))
        {
            var label = tool switch
            {
                CaptureAnnotationTool.Pen => "画笔", CaptureAnnotationTool.Line => "直线",
                CaptureAnnotationTool.Arrow => "单头箭头", CaptureAnnotationTool.DoubleArrow => "双头箭头",
                CaptureAnnotationTool.Rectangle => "矩形", CaptureAnnotationTool.Ellipse => "椭圆",
                CaptureAnnotationTool.Text => "文字", CaptureAnnotationTool.Number => "序号",
                CaptureAnnotationTool.Mosaic => "马赛克", CaptureAnnotationTool.Blur => "模糊",
                CaptureAnnotationTool.Highlight => "高亮", _ => "选择 / F2"
            };
            var button = PinToolButton(Enum.Parse<QingSnapIconKind>(tool.ToString()), label,
                () => SetPinAnnotationTool(tool));
            _pinToolButtons.Add(tool, button);
            tools.Children.Add(button);
        }
        var actions = new Wpf.WrapPanel { Width = 420 };
        foreach (var (hex, name) in new[] { ("#FFFF4E5B", "红色"), ("#FFFFC247", "黄色"),
                     ("#FF61D890", "绿色"), ("#FF76DFEE", "青色"), ("#FFFFFFFF", "白色"), ("#FF222222", "黑色") })
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var button = PinToolButton(QingSnapIconKind.Color, name, () => _pinAnnotations!.SetColor(color));
            var swatch = new Wpf.Border { Width = 18, Height = 12, CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(color), BorderThickness = new Thickness(1) };
            swatch.SetResourceReference(Wpf.Border.BorderBrushProperty, "ThemeBorderStrongBrush");
            button.Content = swatch;
            actions.Children.Add(button);
        }
        _pinUndoButton = PinToolButton(QingSnapIconKind.Undo, "撤销 Ctrl+Z", () => _pinAnnotations!.Undo());
        _pinRedoButton = PinToolButton(QingSnapIconKind.Undo, "重做 Ctrl+Y", () => _pinAnnotations!.Redo());
        _pinRedoButton.Content = new QingSnapIcon { Kind = QingSnapIconKind.Undo, Width = 17, Height = 17,
            LayoutTransform = new ScaleTransform(-1, 1) };
        actions.Children.Add(_pinUndoButton);
        actions.Children.Add(_pinRedoButton);
        actions.Children.Add(PinToolButton(QingSnapIconKind.Clear, "清空标注（可撤销）", () => _pinAnnotations!.Clear()));
        actions.Children.Add(PinToolButton(QingSnapIconKind.Copy, "复制完整标注图片", CopyImage));
        actions.Children.Add(PinToolButton(QingSnapIconKind.Save, "另存图片", () => OnSaveAnnotatedClick(this, new RoutedEventArgs())));
        actions.Children.Add(PinToolButton(QingSnapIconKind.Confirm, "完成标注 / Esc", FinishPinAnnotations));
        var content = new Wpf.StackPanel();
        content.Children.Add(tools);
        content.Children.Add(actions);
        _pinTextStyleRow = new Wpf.StackPanel { Orientation = Wpf.Orientation.Horizontal, Margin = new Thickness(3, 2, 0, 0) };
        _pinTextBoldButton = PinTextStyleButton("B", "文字加粗", () =>
            _pinAnnotations!.SetTextBold(!_pinAnnotations.CurrentTextBold));
        _pinTextShadowButton = PinTextStyleButton("阴影", "文字阴影", () =>
            _pinAnnotations!.SetTextShadow(!_pinAnnotations.CurrentTextShadow));
        _pinTextStyleRow.Children.Add(_pinTextBoldButton);
        _pinTextStyleRow.Children.Add(_pinTextShadowButton);
        content.Children.Add(_pinTextStyleRow);
        var hint = new Wpf.TextBlock { Text = "F2 选择/编辑 · 滚轮调整标注 · Ctrl+滚轮缩放 · Esc 完成",
            FontSize = 10, Margin = new Thickness(5) };
        hint.SetResourceReference(Wpf.TextBlock.ForegroundProperty, "ThemeMutedTextBrush");
        content.Children.Add(hint);
        var toolbarChrome = new Wpf.Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5), Child = content
        };
        toolbarChrome.SetResourceReference(Wpf.Border.BackgroundProperty, "ThemeOverlayPanelBrush");
        toolbarChrome.SetResourceReference(Wpf.Border.BorderBrushProperty, "ThemeBorderStrongBrush");
        _pinAnnotationToolbar = new Popup
        {
            PlacementTarget = Frame, Placement = PlacementMode.Bottom, StaysOpen = true,
            AllowsTransparency = true, VerticalOffset = 6,
            Child = toolbarChrome
        };
        RefreshPinAnnotationButtons();
    }

    private Wpf.Button PinToolButton(QingSnapIconKind icon, string hint, Action action)
    {
        var button = new Wpf.Button
        {
            Width = 34, Height = 30, Margin = new Thickness(0), ToolTip = hint,
            Focusable = false,
            Style = (Style)FindResource("PinAnnotationButton"),
            Content = new QingSnapIcon { Kind = icon, Width = 17, Height = 17 }
        };
        button.SetResourceReference(Wpf.Button.ForegroundProperty, "ThemeAccentBrush");
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception exception)
            {
                DiagnosticLog.Error("PinAnnotation", exception, "贴图标注操作失败。");
                ShowFeedback("标注操作失败，请重试");
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(button, hint);
        return button;
    }

    private Wpf.Button PinTextStyleButton(string label, string hint, Action action)
    {
        var button = PinToolButton(QingSnapIconKind.Text, hint, action);
        button.Width = label == "B" ? 54 : 72;
        button.Margin = new Thickness(0, 0, 5, 0);
        button.Content = new Wpf.TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold };
        button.IsTabStop = false;
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            try { action(); }
            catch (Exception exception)
            {
                DiagnosticLog.Error("PinAnnotation", exception, "贴图文字样式调整失败。");
                ShowFeedback("文字样式调整失败，请重试");
            }
        };
        return button;
    }

    private void RefreshPinAnnotationButtons()
    {
        foreach (var pair in _pinToolButtons)
        {
            if (pair.Key == _pinAnnotations!.ActiveTool)
                pair.Value.SetResourceReference(Wpf.Button.BackgroundProperty, "ThemeSelectedBrush");
            else pair.Value.ClearValue(Wpf.Button.BackgroundProperty);
        }
        if (_pinUndoButton is not null) _pinUndoButton.IsEnabled = _pinAnnotations!.CanUndo;
        if (_pinRedoButton is not null) _pinRedoButton.IsEnabled = _pinAnnotations!.CanRedo;
        if (_pinTextStyleRow is not null)
        {
            _pinTextStyleRow.Visibility = _pinAnnotations!.ActiveTool == CaptureAnnotationTool.Text ||
                _pinAnnotations.HasSelectedText || _pinAnnotations.IsEditingText ? Visibility.Visible : Visibility.Collapsed;
            SetPinTextStyleButtonState(_pinTextBoldButton!, _pinAnnotations.CurrentTextBold);
            SetPinTextStyleButtonState(_pinTextShadowButton!, _pinAnnotations.CurrentTextShadow);
            RepositionPinAnnotationToolbar();
        }
    }

    private static void SetPinTextStyleButtonState(Wpf.Button button, bool enabled)
    {
        if (enabled) button.SetResourceReference(Wpf.Button.BackgroundProperty, "ThemeSelectedBrush");
        else button.ClearValue(Wpf.Button.BackgroundProperty);
    }

    private void RepositionPinAnnotationToolbar()
    {
        if (_pinAnnotationToolbar?.IsOpen != true) return;
        if (_pinToolbarReposition?.Status == System.Windows.Threading.DispatcherOperationStatus.Pending) return;
        _pinToolbarReposition = Dispatcher.BeginInvoke(() =>
        {
            _pinToolbarReposition = null;
            if (_isPinClosed || _pinAnnotationToolbar?.IsOpen != true) return;
            _pinAnnotationToolbar.HorizontalOffset += 1;
            _pinAnnotationToolbar.HorizontalOffset -= 1;
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ClosePinAnnotationToolbar()
    {
        _pinToolbarReposition?.Abort();
        _pinToolbarReposition = null;
        if (_pinAnnotationToolbar is not null) _pinAnnotationToolbar.IsOpen = false;
    }
}
