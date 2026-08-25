using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using QingSnap.App.Controls;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;
using MediaColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfRect = System.Windows.Rect;

namespace QingSnap.App.Views;

public partial class CaptureOverlayWindow : Window
{
    private const double HitPadding = 9;
    private const double MinimumSelectionSize = 2;

    private readonly ScreenSnapshot _snapshot;
    private readonly DrawingRectangle? _initialLocalRegion;
    private readonly DrawingRectangle? _recallLocalRegion;
    private readonly bool _showActionToolbar;
    private readonly AppSettings _settings;
    private readonly OcrService? _ocrService;
    private readonly ClipboardService? _clipboardService;
    private readonly Rectangle[] _cornerMarks;
    private readonly CaptureAnnotationController _annotationController;
    private readonly DispatcherTimer _adjustmentBadgeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(850)
    };
    private readonly DispatcherTimer _ocrPrefetchTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(180)
    };
    private CancellationTokenSource? _ocrPrefetchCancellation;
    private Task<OcrRecognitionResult>? _prefetchedOcrTask;
    private BitmapSource? _prefetchedOcrImage;
    private bool _creatingOcrPrefetch;
    private Point _dragStart;
    private WpfRect _selection;
    private WpfRect _selectionAtDragStart;
    private DragMode _dragMode;
    private bool _isAnnotating;
    private WpfRect _smartPreview;
    private WpfRect _pendingSmartSelection;
    private bool _dragMoved;
    private MediaColor _currentPixelColor;
    private CaptureAnnotationTool _lineTool = CaptureAnnotationTool.Line;
    private CaptureAnnotationTool _regionTool = CaptureAnnotationTool.Rectangle;

    public CaptureOverlayWindow(
        ScreenSnapshot snapshot,
        DrawingRectangle? initialLocalRegion = null,
        string? confirmationHint = null,
        bool showActionToolbar = true,
        DrawingRectangle? recallLocalRegion = null,
        AppSettings? settings = null,
        OcrService? ocrService = null,
        ClipboardService? clipboardService = null)
    {
        _snapshot = snapshot;
        _initialLocalRegion = initialLocalRegion;
        _recallLocalRegion = recallLocalRegion;
        _showActionToolbar = showActionToolbar;
        _settings = settings ?? new AppSettings();
        _ocrService = ocrService;
        _clipboardService = clipboardService;
        InitializeComponent();
        _annotationController = new CaptureAnnotationController(AnnotationLayer, snapshot, _settings);
        _annotationController.Changed += (_, _) =>
        {
            UpdateAnnotationButtons();
            InvalidateOcrPrefetch();
        };
        UpdateStyleButtons();
        _adjustmentBadgeTimer.Tick += (_, _) =>
        {
            _adjustmentBadgeTimer.Stop();
            AdjustmentBadge.Visibility = Visibility.Collapsed;
        };
        _ocrPrefetchTimer.Tick += OnOcrPrefetchTimerTick;

        BackgroundImage.Source = snapshot.Image;
        CloseCaptureButton.Visibility = UsesCloseButton && _showActionToolbar
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (UsesCloseButton && _showActionToolbar)
        {
            HintActionText.Text = "R 上次选区  ·  双击 / ENTER 复制  ·  ESC / × 关闭";
        }

        if (!string.IsNullOrWhiteSpace(confirmationHint))
        {
            HintActionText.Text = $"R 上次选区  ·  {confirmationHint}";
        }
        _cornerMarks =
        [
            TopLeftHorizontal,
            TopLeftVertical,
            TopRightHorizontal,
            TopRightVertical,
            BottomLeftHorizontal,
            BottomLeftVertical,
            BottomRightHorizontal,
            BottomRightVertical
        ];

        foreach (var mark in _cornerMarks)
        {
            mark.Visibility = Visibility.Collapsed;
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _adjustmentBadgeTimer.Stop();
            CancelOcrPrefetch();
        };
    }

    public event EventHandler<DrawingRectangle>? SelectionConfirmed;
    public event EventHandler? SelectionCancelled;
    public event EventHandler<CaptureOverlayActionEventArgs>? ActionRequested;
    public event EventHandler? PreviousSelectionRequested;

    public void CloseAfterPinPresented()
    {
        if (IsVisible)
        {
            IsHitTestVisible = false;
        }

        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ImmAssociateContextEx(handle, nint.Zero, 0);
        var bounds = _snapshot.Bounds;
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InputMethod.SetIsInputMethodEnabled(this, false);
        Activate();
        Focus();
        Keyboard.Focus(this);
        ConfigureNativeIme(false);
        UpdateHintPosition();
        ShowFullShade();

        if (_initialLocalRegion is { Width: > 0, Height: > 0 } initialRegion)
        {
            LoadLocalSelection(initialRegion);
        }
    }

    private void OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject focusTarget)
        {
            var enableIme = focusTarget is WpfTextBox;
            InputMethod.SetIsInputMethodEnabled(focusTarget, enableIme);
            ConfigureNativeIme(enableIme);
            Dispatcher.BeginInvoke(
                () => ConfigureNativeIme(Keyboard.FocusedElement is WpfTextBox),
                DispatcherPriority.Input);
        }
    }

    private void ConfigureNativeIme(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != nint.Zero)
        {
            NativeMethods.ImmAssociateContextEx(
                handle,
                nint.Zero,
                enabled ? NativeMethods.IaceDefault : 0);
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CaptureToolbar.IsMouseOver)
        {
            return;
        }

        var point = ClampPoint(e.GetPosition(Surface));
        if (_annotationController.ActiveTool != CaptureAnnotationTool.None &&
            _selection.Contains(point))
        {
            if (_annotationController.ActiveTool == CaptureAnnotationTool.Select &&
                e.ClickCount >= 2 &&
                _annotationController.BeginEditTextAt(point))
            {
                e.Handled = true;
                return;
            }

            _annotationController.Begin(point);
            _isAnnotating = _annotationController.IsDrawing;
            if (_isAnnotating)
            {
                Surface.CaptureMouse();
            }

            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2 && !_selection.IsEmpty && _selection.Contains(point))
        {
            ConfirmSelection();
            e.Handled = true;
            return;
        }

        _dragStart = point;
        _selectionAtDragStart = _selection;
        _dragMoved = false;
        if (_selection.IsEmpty && !_smartPreview.IsEmpty && _smartPreview.Contains(point))
        {
            _pendingSmartSelection = _smartPreview;
            // Adopt the preview immediately so a normal click cannot collapse into a
            // one-pixel drag if Windows delivers a synthetic move between down/up.
            // A deliberate drag still switches back to free-form selection below.
            SetSelection(_pendingSmartSelection);
            _dragMode = DragMode.Create;
            Surface.CaptureMouse();
            e.Handled = true;
            return;
        }

        _dragMode = HitTest(point);

        if (_dragMode == DragMode.Create)
        {
            SetSelection(new WpfRect(point, point));
        }

        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pointer = ClampPoint(e.GetPosition(Surface));
        UpdateMagnifier(pointer);
        if (_dragMode == DragMode.None && _selection.IsEmpty && _settings.SmartWindowSelection)
        {
            UpdateSmartPreview(pointer);
        }

        if (_isAnnotating)
        {
            _annotationController.Update(ClampPoint(e.GetPosition(Surface)));
            Surface.Cursor = _annotationController.ActiveTool == CaptureAnnotationTool.Select
                ? _annotationController.GetSelectionCursorAt(pointer)
                : CursorForAnnotationTool(_annotationController.ActiveTool);
            e.Handled = true;
            return;
        }

        if (CaptureToolbar.IsMouseOver && _dragMode == DragMode.None)
        {
            Surface.Cursor = WpfCursors.Arrow;
            return;
        }

        var point = pointer;
        if (_dragMode == DragMode.None)
        {
            Surface.Cursor = CursorForPointer(point);
            return;
        }

        if (_dragMode == DragMode.Create && !_pendingSmartSelection.IsEmpty)
        {
            if (e.LeftButton != MouseButtonState.Pressed || (point - _dragStart).Length < 8)
            {
                return;
            }

            _dragMoved = true;
            _pendingSmartSelection = WpfRect.Empty;
            SetSelection(new WpfRect(_dragStart, _dragStart));
        }

        ApplyDrag(point);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isAnnotating)
        {
            _annotationController.End(ClampPoint(e.GetPosition(Surface)));
            _isAnnotating = false;
            Surface.ReleaseMouseCapture();
            Surface.Cursor = CursorForPointer(ClampPoint(e.GetPosition(Surface)));
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.None)
        {
            return;
        }

        if (!_pendingSmartSelection.IsEmpty && !_dragMoved)
        {
            SetSelection(_pendingSmartSelection);
            _pendingSmartSelection = WpfRect.Empty;
            _dragMode = DragMode.None;
            Surface.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        ApplyDrag(ClampPoint(e.GetPosition(Surface)));
        _dragMode = DragMode.None;
        Surface.ReleaseMouseCapture();

        if (_selection.Width < MinimumSelectionSize || _selection.Height < MinimumSelectionSize)
        {
            _selection = WpfRect.Empty;
            ShowFullShade();
        }

        Surface.Cursor = CursorFor(HitTest(ClampPoint(e.GetPosition(Surface))));
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is WpfTextBox)
        {
            return;
        }

        var key = ResolveShortcutKey(e);
        if (Keyboard.Modifiers == ModifierKeys.None &&
            key is Key.W or Key.A or Key.S or Key.D)
        {
            var offset = key switch
            {
                Key.W => (X: 0, Y: -1),
                Key.A => (X: -1, Y: 0),
                Key.S => (X: 0, Y: 1),
                Key.D => (X: 1, Y: 0),
                _ => (X: 0, Y: 0)
            };
            NudgeCrosshair(offset.X, offset.Y);
            e.Handled = true;
            return;
        }

        if (_showActionToolbar &&
            key == Key.Z &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _annotationController.Undo();
            e.Handled = true;
            return;
        }

        if (_showActionToolbar && _annotationController.ActiveTool == CaptureAnnotationTool.Select)
        {
            if (key == Key.Delete)
            {
                _annotationController.DeleteSelected();
                e.Handled = true;
                return;
            }

            if (key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _annotationController.CopySelected();
                e.Handled = true;
                return;
            }

            if (key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _annotationController.PasteSelected();
                e.Handled = true;
                return;
            }

            if (key == Key.F2)
            {
                _annotationController.BeginEditSelectedText();
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                key is Key.OemOpenBrackets or Key.Oem6)
            {
                var toFrontOrBack = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                var moved = key == Key.Oem6
                    ? toFrontOrBack
                        ? _annotationController.BringSelectedToFront()
                        : _annotationController.BringSelectedForward()
                    : toFrontOrBack
                        ? _annotationController.SendSelectedToBack()
                        : _annotationController.SendSelectedBackward();
                if (moved)
                {
                    ShowAdjustmentBadge(Mouse.GetPosition(Root),
                        key == Key.Oem6 ? "图层已上移" : "图层已下移");
                }
                e.Handled = true;
                return;
            }
        }

        if (key == Key.R && Keyboard.Modifiers == ModifierKeys.None)
        {
            RecallPreviousSelection();
            e.Handled = true;
            return;
        }

        if (key == Key.I && Keyboard.Modifiers == ModifierKeys.None && _settings.ShowMagnifier)
        {
            var colorText = $"#{_currentPixelColor.R:X2}{_currentPixelColor.G:X2}{_currentPixelColor.B:X2}";
            _ = CopyColorAsync(colorText);

            e.Handled = true;
            return;
        }

        if (_showActionToolbar && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (key == Key.O)
            {
                RequestAction(CaptureOverlayAction.Ocr);
                e.Handled = true;
                return;
            }

            if (key == Key.C)
            {
                RequestAction(CaptureOverlayAction.Copy);
                e.Handled = true;
                return;
            }

            if (key == Key.S)
            {
                RequestAction(CaptureOverlayAction.Save);
                e.Handled = true;
                return;
            }
        }

        if (key == Key.Escape)
        {
            if (_annotationController.ActiveTool != CaptureAnnotationTool.None)
            {
                SetAnnotationTool(CaptureAnnotationTool.None);
                e.Handled = true;
                return;
            }

            CancelSelection();
            e.Handled = true;
            return;
        }

        if (key == Key.Enter && !_selection.IsEmpty)
        {
            ConfirmSelection();
            e.Handled = true;
        }
    }

    private async Task CopyColorAsync(string colorText)
    {
        try
        {
            if (_clipboardService is not null)
            {
                await _clipboardService.CopyTextAsync(colorText);
            }
            else
            {
                ClipboardService.CopyTextWithRetry(colorText);
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Clipboard", $"Color copy failed: {exception.Message}");
        }
    }

    private void NudgeCrosshair(int offsetX, int offsetY)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var x = Math.Clamp(
            cursor.X + offsetX,
            _snapshot.Bounds.Left,
            _snapshot.Bounds.Right - 1);
        var y = Math.Clamp(
            cursor.Y + offsetY,
            _snapshot.Bounds.Top,
            _snapshot.Bounds.Bottom - 1);
        NativeMethods.SetCursorPos(x, y);
    }

    private static Key ResolveShortcutKey(System.Windows.Input.KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key
    };

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_annotationController.ActiveTool == CaptureAnnotationTool.Select)
        {
            var point = ClampPoint(e.GetPosition(Surface));
            if (_annotationController.SelectAt(point) &&
                FindResource("AnnotationContextMenu") is ContextMenu menu)
            {
                if (menu.Items.OfType<MenuItem>().FirstOrDefault() is { } editItem)
                {
                    editItem.IsEnabled = _annotationController.CanEditSelectedText;
                }
                menu.PlacementTarget = Surface;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen = true;
                e.Handled = true;
                return;
            }
        }

        if (_annotationController.ActiveTool != CaptureAnnotationTool.None)
        {
            SetAnnotationTool(CaptureAnnotationTool.None);
            e.Handled = true;
            return;
        }

        CancelSelection();
        e.Handled = true;
    }

    private void RecallPreviousSelection()
    {
        if (_recallLocalRegion is not { Width: > 0, Height: > 0 } previousRegion)
        {
            PreviousSelectionRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        SetAnnotationTool(CaptureAnnotationTool.None);
        _annotationController.Clear();
        LoadLocalSelection(previousRegion);
    }

    private void LoadLocalSelection(DrawingRectangle region)
    {
        var scaleX = Surface.ActualWidth / Math.Max(1, _snapshot.Bounds.Width);
        var scaleY = Surface.ActualHeight / Math.Max(1, _snapshot.Bounds.Height);
        SetSelection(new WpfRect(
            region.X * scaleX,
            region.Y * scaleY,
            region.Width * scaleX,
            region.Height * scaleY));
    }

    private void ApplyDrag(Point point)
    {
        switch (_dragMode)
        {
            case DragMode.Create:
                SetSelection(new WpfRect(_dragStart, point));
                return;
            case DragMode.Move:
                MoveSelection(point);
                return;
            case DragMode.None:
                return;
        }

        ResizeSelection(point);
    }

    private void MoveSelection(Point point)
    {
        var deltaX = point.X - _dragStart.X;
        var deltaY = point.Y - _dragStart.Y;
        var left = Math.Clamp(
            _selectionAtDragStart.Left + deltaX,
            0,
            Math.Max(0, Surface.ActualWidth - _selectionAtDragStart.Width));
        var top = Math.Clamp(
            _selectionAtDragStart.Top + deltaY,
            0,
            Math.Max(0, Surface.ActualHeight - _selectionAtDragStart.Height));
        SetSelection(new WpfRect(left, top, _selectionAtDragStart.Width, _selectionAtDragStart.Height));
    }

    private void ResizeSelection(Point point)
    {
        var left = _selectionAtDragStart.Left;
        var top = _selectionAtDragStart.Top;
        var right = _selectionAtDragStart.Right;
        var bottom = _selectionAtDragStart.Bottom;

        if (_dragMode is DragMode.Left or DragMode.TopLeft or DragMode.BottomLeft)
        {
            left = Math.Clamp(point.X, 0, right - MinimumSelectionSize);
        }

        if (_dragMode is DragMode.Right or DragMode.TopRight or DragMode.BottomRight)
        {
            right = Math.Clamp(point.X, left + MinimumSelectionSize, Surface.ActualWidth);
        }

        if (_dragMode is DragMode.Top or DragMode.TopLeft or DragMode.TopRight)
        {
            top = Math.Clamp(point.Y, 0, bottom - MinimumSelectionSize);
        }

        if (_dragMode is DragMode.Bottom or DragMode.BottomLeft or DragMode.BottomRight)
        {
            bottom = Math.Clamp(point.Y, top + MinimumSelectionSize, Surface.ActualHeight);
        }

        SetSelection(new WpfRect(left, top, right - left, bottom - top));
    }

    private DragMode HitTest(Point point)
    {
        if (_selection.IsEmpty)
        {
            return DragMode.Create;
        }

        var nearLeft = Math.Abs(point.X - _selection.Left) <= HitPadding;
        var nearRight = Math.Abs(point.X - _selection.Right) <= HitPadding;
        var nearTop = Math.Abs(point.Y - _selection.Top) <= HitPadding;
        var nearBottom = Math.Abs(point.Y - _selection.Bottom) <= HitPadding;
        var withinX = point.X >= _selection.Left - HitPadding && point.X <= _selection.Right + HitPadding;
        var withinY = point.Y >= _selection.Top - HitPadding && point.Y <= _selection.Bottom + HitPadding;

        if (nearLeft && nearTop)
        {
            return DragMode.TopLeft;
        }

        if (nearRight && nearTop)
        {
            return DragMode.TopRight;
        }

        if (nearLeft && nearBottom)
        {
            return DragMode.BottomLeft;
        }

        if (nearRight && nearBottom)
        {
            return DragMode.BottomRight;
        }

        if (nearLeft && withinY)
        {
            return DragMode.Left;
        }

        if (nearRight && withinY)
        {
            return DragMode.Right;
        }

        if (nearTop && withinX)
        {
            return DragMode.Top;
        }

        if (nearBottom && withinX)
        {
            return DragMode.Bottom;
        }

        return _selection.Contains(point) ? DragMode.Move : DragMode.Create;
    }

    private void SetSelection(WpfRect rect)
    {
        _smartPreview = WpfRect.Empty;
        _selection = rect;
        _annotationController.SetBounds(rect, Surface.ActualWidth, Surface.ActualHeight);

        SelectionBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionBorder, rect.Left);
        Canvas.SetTop(SelectionBorder, rect.Top);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;

        UpdateShades(rect);
        UpdateCornerMarks(rect);
        UpdateSizeBadge(rect);
        UpdateToolbarPosition(rect);
        HintBadge.Visibility = Visibility.Collapsed;
        InvalidateOcrPrefetch();
    }

    private void UpdateSmartPreview(Point point)
    {
        var scaleX = _snapshot.Bounds.Width / Math.Max(1, Surface.ActualWidth);
        var scaleY = _snapshot.Bounds.Height / Math.Max(1, Surface.ActualHeight);
        var target = NativeMethods.FindWindowAtPointExcludingProcess(
            new NativeMethods.NativePoint(
                _snapshot.Bounds.X + (int)Math.Round(point.X * scaleX),
                _snapshot.Bounds.Y + (int)Math.Round(point.Y * scaleY)),
            (uint)Environment.ProcessId);

        if (target == nint.Zero || !NativeMethods.GetWindowRect(target, out var windowRect))
        {
            ClearSmartPreview();
            return;
        }

        NativeMethods.GetWindowThreadProcessId(target, out var processId);
        if (processId == Environment.ProcessId)
        {
            ClearSmartPreview();
            return;
        }

        var leftPx = Math.Max(windowRect.Left, _snapshot.Bounds.Left);
        var topPx = Math.Max(windowRect.Top, _snapshot.Bounds.Top);
        var rightPx = Math.Min(windowRect.Right, _snapshot.Bounds.Right);
        var bottomPx = Math.Min(windowRect.Bottom, _snapshot.Bounds.Bottom);
        if (rightPx - leftPx < 20 || bottomPx - topPx < 20)
        {
            ClearSmartPreview();
            return;
        }

        var toDipX = Surface.ActualWidth / Math.Max(1, _snapshot.Bounds.Width);
        var toDipY = Surface.ActualHeight / Math.Max(1, _snapshot.Bounds.Height);
        _smartPreview = new WpfRect(
            (leftPx - _snapshot.Bounds.X) * toDipX,
            (topPx - _snapshot.Bounds.Y) * toDipY,
            (rightPx - leftPx) * toDipX,
            (bottomPx - topPx) * toDipY);
        ShowSmartPreview(_smartPreview);
    }

    private void ShowSmartPreview(WpfRect rect)
    {
        SelectionBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionBorder, rect.Left);
        Canvas.SetTop(SelectionBorder, rect.Top);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        UpdateShades(rect);
        UpdateSizeBadge(rect);
        CaptureToolbar.Visibility = Visibility.Collapsed;
        HintBadge.Visibility = Visibility.Collapsed;
        foreach (var mark in _cornerMarks)
        {
            mark.Visibility = Visibility.Collapsed;
        }
    }

    private void ClearSmartPreview()
    {
        if (_smartPreview.IsEmpty || !_selection.IsEmpty)
        {
            return;
        }

        _smartPreview = WpfRect.Empty;
        SelectionBorder.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        HintBadge.Visibility = Visibility.Visible;
        SetRectangle(TopShade, 0, 0, Surface.ActualWidth, Surface.ActualHeight);
        SetRectangle(LeftShade, 0, 0, 0, 0);
        SetRectangle(RightShade, 0, 0, 0, 0);
        SetRectangle(BottomShade, 0, 0, 0, 0);
    }

    private void UpdateMagnifier(Point point)
    {
        if (!_settings.ShowMagnifier || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
        {
            MagnifierOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var scaleX = _snapshot.Image.PixelWidth / Surface.ActualWidth;
        var scaleY = _snapshot.Image.PixelHeight / Surface.ActualHeight;
        var pixelX = Math.Clamp((int)Math.Round(point.X * scaleX), 0, _snapshot.Image.PixelWidth - 1);
        var pixelY = Math.Clamp((int)Math.Round(point.Y * scaleY), 0, _snapshot.Image.PixelHeight - 1);
        const int cropWidth = 15;
        const int cropHeight = 11;
        var cropX = Math.Clamp(pixelX - cropWidth / 2, 0, Math.Max(0, _snapshot.Image.PixelWidth - cropWidth));
        var cropY = Math.Clamp(pixelY - cropHeight / 2, 0, Math.Max(0, _snapshot.Image.PixelHeight - cropHeight));
        var width = Math.Min(cropWidth, _snapshot.Image.PixelWidth - cropX);
        var height = Math.Min(cropHeight, _snapshot.Image.PixelHeight - cropY);
        MagnifierImage.Source = new CroppedBitmap(_snapshot.Image, new Int32Rect(cropX, cropY, width, height));

        BitmapSource source = _snapshot.Image.Format == PixelFormats.Bgra32
            ? _snapshot.Image
            : new FormatConvertedBitmap(_snapshot.Image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        source.CopyPixels(new Int32Rect(pixelX, pixelY, 1, 1), pixel, 4, 0);
        _currentPixelColor = MediaColor.FromRgb(pixel[2], pixel[1], pixel[0]);
        MagnifierCoordinateText.Text = $"{_snapshot.Bounds.X + pixelX}, {_snapshot.Bounds.Y + pixelY}";
        MagnifierColorText.Text = $"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}";

        var left = point.X + 18;
        var top = point.Y + 18;
        if (left + MagnifierOverlay.Width > Surface.ActualWidth)
        {
            left = point.X - MagnifierOverlay.Width - 18;
        }

        if (top + MagnifierOverlay.Height > Surface.ActualHeight)
        {
            top = point.Y - MagnifierOverlay.Height - 18;
        }

        MagnifierOverlay.Margin = new Thickness(Math.Max(0, left), Math.Max(0, top), 0, 0);
        MagnifierOverlay.Visibility = Visibility.Visible;
    }

    private void UpdateShades(WpfRect rect)
    {
        SetRectangle(TopShade, 0, 0, Surface.ActualWidth, rect.Top);
        SetRectangle(BottomShade, 0, rect.Bottom, Surface.ActualWidth, Surface.ActualHeight - rect.Bottom);
        SetRectangle(LeftShade, 0, rect.Top, rect.Left, rect.Height);
        SetRectangle(RightShade, rect.Right, rect.Top, Surface.ActualWidth - rect.Right, rect.Height);
    }

    private void ShowFullShade()
    {
        _annotationController.Clear();
        _annotationController.SetBounds(WpfRect.Empty, Surface.ActualWidth, Surface.ActualHeight);
        SelectionBorder.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        CaptureToolbar.Visibility = Visibility.Collapsed;
        HintBadge.Visibility = Visibility.Visible;
        SetRectangle(TopShade, 0, 0, Surface.ActualWidth, Surface.ActualHeight);
        SetRectangle(LeftShade, 0, 0, 0, 0);
        SetRectangle(RightShade, 0, 0, 0, 0);
        SetRectangle(BottomShade, 0, 0, 0, 0);

        foreach (var mark in _cornerMarks)
        {
            mark.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateCornerMarks(WpfRect rect)
    {
        foreach (var mark in _cornerMarks)
        {
            mark.Visibility = Visibility.Visible;
        }

        SetPosition(TopLeftHorizontal, rect.Left - 1, rect.Top - 1);
        SetPosition(TopLeftVertical, rect.Left - 1, rect.Top - 1);
        SetPosition(TopRightHorizontal, rect.Right - 15, rect.Top - 1);
        SetPosition(TopRightVertical, rect.Right - 2, rect.Top - 1);
        SetPosition(BottomLeftHorizontal, rect.Left - 1, rect.Bottom - 2);
        SetPosition(BottomLeftVertical, rect.Left - 1, rect.Bottom - 15);
        SetPosition(BottomRightHorizontal, rect.Right - 15, rect.Bottom - 2);
        SetPosition(BottomRightVertical, rect.Right - 2, rect.Bottom - 15);
    }

    private void UpdateSizeBadge(WpfRect rect)
    {
        var scaleX = _snapshot.Bounds.Width / Math.Max(1, Surface.ActualWidth);
        var scaleY = _snapshot.Bounds.Height / Math.Max(1, Surface.ActualHeight);
        var physicalWidth = Math.Max(1, (int)Math.Round(rect.Width * scaleX));
        var physicalHeight = Math.Max(1, (int)Math.Round(rect.Height * scaleY));
        SizeText.Text = $"{physicalWidth} × {physicalHeight} px";
        ToolbarSizeText.Text = $"{physicalWidth} × {physicalHeight}";
        SizeBadge.Visibility = Visibility.Visible;
        SizeBadge.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

        var badgeWidth = SizeBadge.DesiredSize.Width;
        var badgeHeight = SizeBadge.DesiredSize.Height;
        var left = Math.Min(rect.Right - badgeWidth, Surface.ActualWidth - badgeWidth - 8);
        left = Math.Max(8, left);
        var top = rect.Bottom + 9;
        if (top + badgeHeight > Surface.ActualHeight - 8)
        {
            top = Math.Max(8, rect.Bottom - badgeHeight - 9);
        }

        SetPosition(SizeBadge, left, top);
    }

    private void UpdateHintPosition()
    {
        HintBadge.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(HintBadge, Math.Max(12, (Surface.ActualWidth - HintBadge.DesiredSize.Width) / 2));
        Canvas.SetTop(HintBadge, 22);
    }

    private void UpdateToolbarPosition(WpfRect rect)
    {
        if (!_showActionToolbar)
        {
            CaptureToolbar.Visibility = Visibility.Collapsed;
            return;
        }

        CaptureToolbar.Visibility = Visibility.Visible;
        CaptureToolbar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarWidth = CaptureToolbar.DesiredSize.Width;
        var toolbarHeight = CaptureToolbar.DesiredSize.Height;
        var left = Math.Clamp(
            rect.Right - toolbarWidth,
            10,
            Math.Max(10, Surface.ActualWidth - toolbarWidth - 10));
        var top = rect.Bottom + 10;
        if (top + toolbarHeight > Surface.ActualHeight - 10)
        {
            top = rect.Top - toolbarHeight - 10;
        }

        top = Math.Clamp(top, 10, Math.Max(10, Surface.ActualHeight - toolbarHeight - 10));
        SetPosition(CaptureToolbar, left, top);
    }

    private void ConfirmSelection()
    {
        if (_selection.IsEmpty)
        {
            return;
        }

        if (_showActionToolbar)
        {
            RequestAction(CaptureOverlayAction.Confirm);
            return;
        }

        var scaleX = _snapshot.Bounds.Width / Math.Max(1, Surface.ActualWidth);
        var scaleY = _snapshot.Bounds.Height / Math.Max(1, Surface.ActualHeight);
        var left = Math.Clamp((int)Math.Floor(_selection.Left * scaleX), 0, _snapshot.Bounds.Width - 1);
        var top = Math.Clamp((int)Math.Floor(_selection.Top * scaleY), 0, _snapshot.Bounds.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(_selection.Right * scaleX), left + 1, _snapshot.Bounds.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(_selection.Bottom * scaleY), top + 1, _snapshot.Bounds.Height);

        SelectionConfirmed?.Invoke(this, new DrawingRectangle(left, top, right - left, bottom - top));
    }

    private void RequestAction(CaptureOverlayAction action)
    {
        var localRegion = GetLocalRegion();
        if (localRegion is null)
        {
            return;
        }

        var usePrefetch = action is CaptureOverlayAction.Ocr or
                                      CaptureOverlayAction.Pin or
                                      CaptureOverlayAction.Copy or
                                      CaptureOverlayAction.Confirm &&
                          _prefetchedOcrImage is not null &&
                          _prefetchedOcrTask is not null;
        var image = usePrefetch ? _prefetchedOcrImage! : CreateSelectedImage();
        var prefetchedOcr = usePrefetch ? _prefetchedOcrTask : null;
        if (usePrefetch)
        {
            _ocrPrefetchTimer.Stop();
            var handedOffCancellation = _ocrPrefetchCancellation;
            _ocrPrefetchCancellation = null;
            if (handedOffCancellation is not null && prefetchedOcr is not null)
            {
                _ = prefetchedOcr.ContinueWith(
                    _ => handedOffCancellation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        else
        {
            CancelOcrPrefetch();
        }

        ActionRequested?.Invoke(
            this,
            new CaptureOverlayActionEventArgs(
                action,
                localRegion.Value,
                image,
                prefetchedOcr));
    }

    private void InvalidateOcrPrefetch()
    {
        if (_creatingOcrPrefetch || _ocrService?.IsOcrAvailable != true || !_showActionToolbar)
        {
            return;
        }

        CancelOcrPrefetch();
        if (!_selection.IsEmpty)
        {
            _ocrPrefetchTimer.Start();
        }
    }

    private void CancelOcrPrefetch()
    {
        _ocrPrefetchTimer.Stop();
        _ocrPrefetchCancellation?.Cancel();
        _ocrPrefetchCancellation?.Dispose();
        _ocrPrefetchCancellation = null;
        _prefetchedOcrTask = null;
        _prefetchedOcrImage = null;
    }

    private void OnOcrPrefetchTimerTick(object? sender, EventArgs e)
    {
        _ocrPrefetchTimer.Stop();
        if (_ocrService?.IsOcrAvailable != true || _selection.IsEmpty || !IsVisible)
        {
            return;
        }

        try
        {
            _creatingOcrPrefetch = true;
            _prefetchedOcrImage = CreateSelectedImage();
            _ocrPrefetchCancellation = new CancellationTokenSource();
            _prefetchedOcrTask = _ocrService.RecognizeAsync(
                _prefetchedOcrImage,
                _ocrPrefetchCancellation.Token,
                progress: null,
                includeWordBoxes: true);
            _ = ObserveOcrPrefetchAsync(_prefetchedOcrTask);
        }
        finally
        {
            _creatingOcrPrefetch = false;
        }
    }

    private static async Task ObserveOcrPrefetchAsync(Task<OcrRecognitionResult> task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    public BitmapSource CreateSelectedImage()
    {
        var localRegion = GetLocalRegion() ??
                          throw new InvalidOperationException("还没有可输出的截图选区。");
        _annotationController.CommitText();

        UIElement[] hiddenElements =
        [
            TopShade,
            LeftShade,
            RightShade,
            BottomShade,
            SelectionBorder,
            SizeBadge,
            HintBadge,
            CaptureToolbar,
            AdjustmentBadge,
            MagnifierOverlay,
            GeometryEditor,
            .. _cornerMarks
        ];
        var visibility = hiddenElements.Select(element => element.Visibility).ToArray();
        _annotationController.SetSelectionAdornersVisible(false);
        try
        {
            foreach (var element in hiddenElements)
            {
                element.Visibility = Visibility.Collapsed;
            }

            Root.UpdateLayout();
            var scaleX = _snapshot.Image.PixelWidth / Math.Max(1, Root.ActualWidth);
            var scaleY = _snapshot.Image.PixelHeight / Math.Max(1, Root.ActualHeight);
            var rendered = new RenderTargetBitmap(
                _snapshot.Image.PixelWidth,
                _snapshot.Image.PixelHeight,
                96 * scaleX,
                96 * scaleY,
                PixelFormats.Pbgra32);
            rendered.Render(Root);

            var cropped = new CroppedBitmap(
                rendered,
                new Int32Rect(
                    localRegion.X,
                    localRegion.Y,
                    localRegion.Width,
                    localRegion.Height));
            cropped.Freeze();
            return cropped;
        }
        finally
        {
            for (var index = 0; index < hiddenElements.Length; index++)
            {
                hiddenElements[index].Visibility = visibility[index];
            }
            _annotationController.SetSelectionAdornersVisible(true);
        }
    }

    private DrawingRectangle? GetLocalRegion()
    {
        if (_selection.IsEmpty)
        {
            return null;
        }

        var scaleX = _snapshot.Bounds.Width / Math.Max(1, Surface.ActualWidth);
        var scaleY = _snapshot.Bounds.Height / Math.Max(1, Surface.ActualHeight);
        var left = Math.Clamp((int)Math.Floor(_selection.Left * scaleX), 0, _snapshot.Bounds.Width - 1);
        var top = Math.Clamp((int)Math.Floor(_selection.Top * scaleY), 0, _snapshot.Bounds.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(_selection.Right * scaleX), left + 1, _snapshot.Bounds.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(_selection.Bottom * scaleY), top + 1, _snapshot.Bounds.Height);
        return new DrawingRectangle(left, top, right - left, bottom - top);
    }

    private void OnAutomaticLongCaptureClick(object sender, RoutedEventArgs e) =>
        RequestAction(CaptureOverlayAction.AutomaticLongCapture);

    private void OnPenClick(object sender, RoutedEventArgs e) =>
        ActivateAnnotationTool(CaptureAnnotationTool.Pen, sender, supportsColor: true);

    private void OnLineClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationTool(_lineTool);
        ShowLineToolPopup();
    }

    private void OnRectangleClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationTool(_regionTool);
        ShowRegionToolPopup();
    }

    private void OnEllipseClick(object sender, RoutedEventArgs e) =>
        ActivateAnnotationTool(CaptureAnnotationTool.Ellipse, sender, supportsColor: true);

    private void OnTextClick(object sender, RoutedEventArgs e) =>
        ActivateAnnotationTool(CaptureAnnotationTool.Text, sender, supportsColor: true);

    private void OnNumberClick(object sender, RoutedEventArgs e) =>
        ActivateAnnotationTool(CaptureAnnotationTool.Number, sender, supportsColor: true);

    private void OnSelectClick(object sender, RoutedEventArgs e) =>
        ActivateAnnotationTool(CaptureAnnotationTool.Select, sender, supportsColor: false);

    private void ActivateAnnotationTool(CaptureAnnotationTool tool, object sender, bool supportsColor)
    {
        SetAnnotationTool(tool);
        RegionToolPopup.IsOpen = false;
        LineToolPopup.IsOpen = false;
        if (supportsColor && sender is WpfButton button)
        {
            ShowAnnotationPalette(button);
        }
        else
        {
            AnnotationPalettePopup.IsOpen = false;
        }
    }

    private void ShowAnnotationPalette(WpfButton placementTarget)
    {
        UpdatePaletteSelection();
        AnnotationPalettePopup.PlacementTarget = placementTarget;
        AnnotationPalettePopup.IsOpen = true;
    }

    private void ShowRegionToolPopup()
    {
        AnnotationPalettePopup.IsOpen = false;
        LineToolPopup.IsOpen = false;
        UpdateRegionToolPopup();
        RegionToolPopup.PlacementTarget = RectangleButton;
        RegionToolPopup.IsOpen = true;
    }

    private void ShowLineToolPopup()
    {
        AnnotationPalettePopup.IsOpen = false;
        RegionToolPopup.IsOpen = false;
        UpdateLineToolPopup();
        LineToolPopup.PlacementTarget = LineButton;
        LineToolPopup.IsOpen = true;
    }

    private void OnLineModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string toolName } ||
            !Enum.TryParse(toolName, out CaptureAnnotationTool tool) ||
            tool is not (CaptureAnnotationTool.Line or
                         CaptureAnnotationTool.Arrow or
                         CaptureAnnotationTool.DoubleArrow))
        {
            return;
        }

        _lineTool = tool;
        UpdateLineToolButton();
        SetAnnotationTool(tool);
        UpdateLineToolPopup();
    }

    private void UpdateLineToolButton()
    {
        LineToolIcon.Kind = _lineTool switch
        {
            CaptureAnnotationTool.Arrow => QingSnapIconKind.Arrow,
            CaptureAnnotationTool.DoubleArrow => QingSnapIconKind.DoubleArrow,
            _ => QingSnapIconKind.Line
        };
        LineButton.ToolTip = _lineTool switch
        {
            CaptureAnnotationTool.Arrow => "线型工具：单头箭头",
            CaptureAnnotationTool.DoubleArrow => "线型工具：双头箭头",
            _ => "线型工具：直线"
        };
    }

    private void UpdateLineToolPopup()
    {
        var inactive = new SolidColorBrush(Colors.Transparent);
        var active = new SolidColorBrush(MediaColor.FromRgb(49, 71, 84));
        foreach (var button in LineModePanel.Children.OfType<WpfButton>())
        {
            var selected = button.Tag is string value &&
                           Enum.TryParse(value, out CaptureAnnotationTool tool) &&
                           tool == _lineTool;
            button.Background = selected ? active : inactive;
        }

        UpdatePaletteSelection();
    }

    private void OnRegionModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string toolName } ||
            !Enum.TryParse(toolName, out CaptureAnnotationTool tool) ||
            tool is not (CaptureAnnotationTool.Rectangle or
                         CaptureAnnotationTool.Mosaic or
                         CaptureAnnotationTool.Highlight or
                         CaptureAnnotationTool.Blur))
        {
            return;
        }

        _regionTool = tool;
        UpdateRegionToolButton();
        SetAnnotationTool(tool);
        UpdateRegionToolPopup();
    }

    private void UpdateRegionToolButton()
    {
        RegionToolIcon.Kind = _regionTool switch
        {
            CaptureAnnotationTool.Mosaic => QingSnapIconKind.Mosaic,
            CaptureAnnotationTool.Highlight => QingSnapIconKind.Highlight,
            CaptureAnnotationTool.Blur => QingSnapIconKind.Blur,
            _ => QingSnapIconKind.Rectangle
        };
        RectangleButton.ToolTip = _regionTool switch
        {
            CaptureAnnotationTool.Mosaic => "区域工具：马赛克",
            CaptureAnnotationTool.Highlight => "区域工具：高亮",
            CaptureAnnotationTool.Blur => "区域工具：模糊",
            _ => "区域工具：矩形"
        };
    }

    private void UpdateRegionToolPopup()
    {
        var inactive = new SolidColorBrush(Colors.Transparent);
        var active = new SolidColorBrush(MediaColor.FromRgb(49, 71, 84));
        foreach (var button in RegionModePanel.Children.OfType<WpfButton>())
        {
            var selected = button.Tag is string value &&
                           Enum.TryParse(value, out CaptureAnnotationTool tool) &&
                           tool == _regionTool;
            button.Background = selected ? active : inactive;
        }

        var usesColor = _regionTool is CaptureAnnotationTool.Rectangle or CaptureAnnotationTool.Highlight;
        RegionColorDivider.Visibility = usesColor ? Visibility.Visible : Visibility.Collapsed;
        RegionColorPanel.Visibility = usesColor ? Visibility.Visible : Visibility.Collapsed;
        UpdatePaletteSelection();
    }

    private void OnPaletteColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string colorValue })
        {
            return;
        }

        var color = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(colorValue);
        _annotationController.SetColor(color);
        UpdatePaletteSelection();
        AnnotationPalettePopup.IsOpen = false;
        RegionToolPopup.IsOpen = false;
        LineToolPopup.IsOpen = false;
        Focus();
    }

    private void UpdatePaletteSelection()
    {
        foreach (var button in AnnotationPalettePanel.Children.OfType<WpfButton>())
        {
            var selected = button.Tag is string value &&
                           (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value) == _annotationController.CurrentColor;
            button.BorderBrush = new SolidColorBrush(
                selected ? Colors.White : MediaColor.FromArgb(102, 83, 106, 118));
            button.BorderThickness = new Thickness(selected ? 2 : 1);
        }

        foreach (var button in RegionColorPanel.Children.OfType<WpfButton>())
        {
            var selected = button.Tag is string value &&
                           (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value) == _annotationController.CurrentColor;
            button.BorderBrush = new SolidColorBrush(
                selected ? Colors.White : MediaColor.FromArgb(102, 83, 106, 118));
            button.BorderThickness = new Thickness(selected ? 2 : 1);
        }

        foreach (var button in LineColorPanel.Children.OfType<WpfButton>())
        {
            var selected = button.Tag is string value &&
                           (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value) == _annotationController.CurrentColor;
            button.BorderBrush = new SolidColorBrush(
                selected ? Colors.White : MediaColor.FromArgb(102, 83, 106, 118));
            button.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CaptureToolbar.IsMouseOver ||
            AnnotationPalettePopup.Child is UIElement { IsMouseOver: true } ||
            RegionToolPopup.Child is UIElement { IsMouseOver: true } ||
            LineToolPopup.Child is UIElement { IsMouseOver: true } ||
            GeometryEditor.IsMouseOver)
        {
            return;
        }

        var point = ClampPoint(e.GetPosition(Surface));
        var result = _annotationController.AdjustAnnotationAt(point, e.Delta);
        if (result is null)
        {
            return;
        }

        ShowAdjustmentBadge(point, result);
        e.Handled = true;
    }

    private void ShowAdjustmentBadge(Point point, string text)
    {
        AdjustmentText.Text = text;
        AdjustmentBadge.Visibility = Visibility.Visible;
        AdjustmentBadge.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = Math.Clamp(
            point.X + 16,
            8,
            Math.Max(8, Root.ActualWidth - AdjustmentBadge.DesiredSize.Width - 8));
        var top = Math.Clamp(
            point.Y - AdjustmentBadge.DesiredSize.Height - 12,
            8,
            Math.Max(8, Root.ActualHeight - AdjustmentBadge.DesiredSize.Height - 8));
        AdjustmentBadge.Margin = new Thickness(left, top, 0, 0);
        _adjustmentBadgeTimer.Stop();
        _adjustmentBadgeTimer.Start();
    }

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        _annotationController.CycleColor();
        UpdateStyleButtons();
    }

    private void OnEditAnnotationTextClick(object sender, RoutedEventArgs e)
    {
        _annotationController.BeginEditSelectedText();
    }

    private void OnBringAnnotationToFrontClick(object sender, RoutedEventArgs e) =>
        ApplyLayerCommand(_annotationController.BringSelectedToFront(), "已置于顶层");

    private void OnBringAnnotationForwardClick(object sender, RoutedEventArgs e) =>
        ApplyLayerCommand(_annotationController.BringSelectedForward(), "已上移一层");

    private void OnSendAnnotationBackwardClick(object sender, RoutedEventArgs e) =>
        ApplyLayerCommand(_annotationController.SendSelectedBackward(), "已下移一层");

    private void OnSendAnnotationToBackClick(object sender, RoutedEventArgs e) =>
        ApplyLayerCommand(_annotationController.SendSelectedToBack(), "已置于底层");

    private void OnCopyAnnotationClick(object sender, RoutedEventArgs e)
    {
        _annotationController.CopySelected();
        _annotationController.PasteSelected();
        ShowAdjustmentBadge(Mouse.GetPosition(Root), "已复制标注");
    }

    private void OnDeleteAnnotationClick(object sender, RoutedEventArgs e)
    {
        _annotationController.DeleteSelected();
        ShowAdjustmentBadge(Mouse.GetPosition(Root), "已删除标注");
    }

    private void ApplyLayerCommand(bool changed, string message)
    {
        if (changed)
        {
            ShowAdjustmentBadge(Mouse.GetPosition(Root), message);
        }
    }

    private void OnThicknessClick(object sender, RoutedEventArgs e)
    {
        _annotationController.CycleThickness();
        UpdateStyleButtons();
    }

    private void OnFontSizeClick(object sender, RoutedEventArgs e)
    {
        _annotationController.CycleFontSize();
        UpdateStyleButtons();
    }

    private void UpdateStyleButtons()
    {
        ColorButton.Foreground = new SolidColorBrush(_annotationController.CurrentColor);
        ColorButton.ToolTip = $"切换标注颜色  #{_annotationController.CurrentColor.R:X2}{_annotationController.CurrentColor.G:X2}{_annotationController.CurrentColor.B:X2}";
        ThicknessButton.ToolTip = $"切换线条粗细  {_annotationController.CurrentThickness:0.#} px";
        FontSizeButton.ToolTip = $"切换文字大小  {_annotationController.CurrentFontSize:0}";
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => _annotationController.Undo();

    private void OnClearClick(object sender, RoutedEventArgs e) => _annotationController.Clear();

    private void OnOcrClick(object sender, RoutedEventArgs e) => RequestAction(CaptureOverlayAction.Ocr);

    private void OnPinClick(object sender, RoutedEventArgs e) => RequestAction(CaptureOverlayAction.Pin);

    private void OnCopyClick(object sender, RoutedEventArgs e) => RequestAction(CaptureOverlayAction.Copy);

    private void OnSaveClick(object sender, RoutedEventArgs e) => RequestAction(CaptureOverlayAction.Save);

    private void OnConfirmClick(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void OnCloseCaptureClick(object sender, RoutedEventArgs e) => CancelSelection();

    private void OnToolbarSizeMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var region = GetLocalRegion();
        if (region is null)
        {
            return;
        }

        GeometryXBox.Text = region.Value.X.ToString();
        GeometryYBox.Text = region.Value.Y.ToString();
        GeometryWidthBox.Text = region.Value.Width.ToString();
        GeometryHeightBox.Text = region.Value.Height.ToString();
        GeometryEditor.Visibility = Visibility.Visible;
        MagnifierOverlay.Visibility = Visibility.Collapsed;
        GeometryXBox.Focus();
        e.Handled = true;
    }

    private void OnGeometryCancelClick(object sender, RoutedEventArgs e)
    {
        GeometryEditor.Visibility = Visibility.Collapsed;
        Focus();
    }

    private void OnGeometryApplyClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(GeometryXBox.Text, out var x) ||
            !int.TryParse(GeometryYBox.Text, out var y) ||
            !int.TryParse(GeometryWidthBox.Text, out var width) ||
            !int.TryParse(GeometryHeightBox.Text, out var height) ||
            width <= 0 || height <= 0)
        {
            return;
        }

        x = Math.Clamp(x, 0, _snapshot.Bounds.Width - 1);
        y = Math.Clamp(y, 0, _snapshot.Bounds.Height - 1);
        width = Math.Clamp(width, 1, _snapshot.Bounds.Width - x);
        height = Math.Clamp(height, 1, _snapshot.Bounds.Height - y);
        var scaleX = Surface.ActualWidth / Math.Max(1, _snapshot.Bounds.Width);
        var scaleY = Surface.ActualHeight / Math.Max(1, _snapshot.Bounds.Height);
        SetSelection(new WpfRect(x * scaleX, y * scaleY, width * scaleX, height * scaleY));
        GeometryEditor.Visibility = Visibility.Collapsed;
        Focus();
    }

    private void CancelSelection() => SelectionCancelled?.Invoke(this, EventArgs.Empty);

    private bool UsesCloseButton => string.Equals(
        _settings.CloseInteraction,
        "Button",
        StringComparison.OrdinalIgnoreCase);

    private void SetAnnotationTool(CaptureAnnotationTool tool)
    {
        _annotationController.CommitText();
        if (tool != CaptureAnnotationTool.Select)
        {
            _annotationController.ClearSelection();
        }
        _annotationController.ActiveTool = tool;
        if (tool == CaptureAnnotationTool.None)
        {
            AnnotationPalettePopup.IsOpen = false;
            RegionToolPopup.IsOpen = false;
            LineToolPopup.IsOpen = false;
        }
        Surface.Cursor = tool == CaptureAnnotationTool.None
            ? WpfCursors.Cross
            : CursorForAnnotationTool(tool);
        UpdateAnnotationButtons();
    }

    private void UpdateAnnotationButtons()
    {
        var inactive = new SolidColorBrush(Colors.Transparent);
        var active = new SolidColorBrush(MediaColor.FromRgb(69, 42, 48));
        (WpfButton Button, CaptureAnnotationTool Tool)[] buttons =
        [
            (PenButton, CaptureAnnotationTool.Pen),
            (EllipseButton, CaptureAnnotationTool.Ellipse),
            (TextButton, CaptureAnnotationTool.Text),
            (NumberButton, CaptureAnnotationTool.Number),
            (SelectButton, CaptureAnnotationTool.Select)
        ];
        foreach (var (button, tool) in buttons)
        {
            button.Background = _annotationController.ActiveTool == tool ? active : inactive;
        }

        LineButton.Background = _annotationController.ActiveTool is
            CaptureAnnotationTool.Line or CaptureAnnotationTool.Arrow or CaptureAnnotationTool.DoubleArrow
            ? active
            : inactive;
        RectangleButton.Background = _annotationController.ActiveTool is
            CaptureAnnotationTool.Rectangle or CaptureAnnotationTool.Mosaic or
            CaptureAnnotationTool.Highlight or CaptureAnnotationTool.Blur
            ? active
            : inactive;

        UndoButton.IsEnabled = _annotationController.HasAnnotations;
        ClearButton.IsEnabled = _annotationController.HasAnnotations;
    }

    private Point ClampPoint(Point point) => new(
        Math.Clamp(point.X, 0, Surface.ActualWidth),
        Math.Clamp(point.Y, 0, Surface.ActualHeight));

    private System.Windows.Input.Cursor CursorForPointer(Point point)
    {
        if (_annotationController.ActiveTool != CaptureAnnotationTool.None && _selection.Contains(point))
        {
            if (_annotationController.ActiveTool == CaptureAnnotationTool.Select)
            {
                return _annotationController.GetSelectionCursorAt(point);
            }

            return CursorForAnnotationTool(_annotationController.ActiveTool);
        }

        return CursorFor(HitTest(point));
    }

    private static System.Windows.Input.Cursor CursorForAnnotationTool(CaptureAnnotationTool tool) =>
        tool == CaptureAnnotationTool.Select ? WpfCursors.SizeAll : WpfCursors.Cross;

    private static System.Windows.Input.Cursor CursorFor(DragMode mode) => mode switch
    {
        DragMode.Move => WpfCursors.SizeAll,
        DragMode.Left or DragMode.Right => WpfCursors.SizeWE,
        DragMode.Top or DragMode.Bottom => WpfCursors.SizeNS,
        DragMode.TopLeft or DragMode.BottomRight => WpfCursors.SizeNWSE,
        DragMode.TopRight or DragMode.BottomLeft => WpfCursors.SizeNESW,
        _ => WpfCursors.Cross
    };

    private static void SetRectangle(Rectangle rectangle, double left, double top, double width, double height)
    {
        SetPosition(rectangle, left, top);
        rectangle.Width = Math.Max(0, width);
        rectangle.Height = Math.Max(0, height);
    }

    private static void SetPosition(FrameworkElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
    }

    private enum DragMode
    {
        None,
        Create,
        Move,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }
}
