using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QingSnap.App.Services;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace QingSnap.App.Views;

public partial class StickyImageWindow
{
    private double _readerHorizontalRatio;
    private double _ordinaryScale;
    private double _contentTargetScale;
    private double _contentLayoutScale;
    private double _contentLayoutOffsetX, _contentLayoutOffsetY;
    private double _contentOffsetX, _contentOffsetY;
    private double _detailScale, _detailX, _detailY;
    private bool _isContentPanning;
    private MouseButton _contentPanButton;
    private Point _contentPanStart;
    private double _contentPanX, _contentPanY;

    private void UpdateContentMenus()
    {
        FixedViewportMenuItem.IsChecked = _isLongReaderMode;
        ContentFitMenuItem.Visibility = _isLongReaderMode ? Visibility.Visible : Visibility.Collapsed;
        ContentActualMenuItem.Visibility = ContentFitMenuItem.Visibility;
        RestoreDefaultPinMenuItem.Visibility = ContentFitMenuItem.Visibility;
    }

    private void OnFixedViewportClick(object sender, RoutedEventArgs e)
    {
        if (_isSelectingText || _isPinDrawing || IsPinTextEditing) { UpdateContentMenus(); return; }
        if (_isLongReaderMode)
        {
            if (_isLongImage) EnterOverviewMode();
            else ExitFixedViewport();
        }
        else EnterFixedViewport();
    }

    private void OnContentFitClick(object sender, RoutedEventArgs e)
    {
        CompletePinMotion();
        if (!_isLongImage && _readerScale > ContentFitScale() * 1.01)
        {
            _detailScale = _readerScale;
            _detailX = LongImageScroller.HorizontalOffset;
            _detailY = LongImageScroller.VerticalOffset;
        }
        SetReaderScale(ContentFitScale(), false);
    }

    private void OnContentActualClick(object sender, RoutedEventArgs e)
    {
        // A physical image pixel maps to a physical display pixel, including images
        // carrying a non-96 DPI tag and per-monitor DPI changes.
        var dpi = VisualTreeHelper.GetDpi(this);
        SetReaderScale(_image.PixelWidth / (_imageWidthDip * dpi.DpiScaleX), false);
    }

    private double ContentFitScale() => PinContentViewportMath.Fit(_imageWidthDip, _imageHeightDip,
        Math.Max(1, LongImageScroller.ViewportWidth), Math.Max(1, LongImageScroller.ViewportHeight));

    private void OnRestoreDefaultPinClick(object sender, RoutedEventArgs e)
    {
        if (_isLongImage) EnterOverviewMode();
        else ExitFixedViewport();
    }

    private void EnterFixedViewport()
    {
        if (_isLongReaderMode || _isCollapsed || _isDockTransitioning || _isPinClosed) return;
        CompletePinMotion();
        CompleteZoomPresentation();
        if (_isLongImage) { EnterReaderMode(false); return; }

        UpdateLayout();
        _ordinaryScale = _scale;
        _readerViewportWidth = Math.Max(1, (Frame.ActualWidth > 0 ? Frame.ActualWidth : Width) - Frame.BorderThickness.Left - Frame.BorderThickness.Right);
        _readerViewportHeight = Math.Max(1, (Frame.ActualHeight > 0 ? Frame.ActualHeight : Height) - Frame.BorderThickness.Top - Frame.BorderThickness.Bottom);
        _readerBaseScale = PinContentViewportMath.Fit(_imageWidthDip, _imageHeightDip,
            _readerViewportWidth, _readerViewportHeight);
        _readerScale = _readerBaseScale;
        _readerOffsetRatio = _readerHorizontalRatio = 0;
        _detailScale = 0;
        _isLongReaderMode = true;
        ResetPinZoomAnchor();
        NormalPinSurface.Visibility = Visibility.Collapsed;
        LongImageScroller.Visibility = Visibility.Visible;
        ScrollRail.Visibility = Visibility.Visible;
        ApplyReaderImageSize();
        UpdateTextOverlayVisibility();
        UpdateContentMenus();
        UpdateLayout();
        LongImageScroller.ScrollToHorizontalOffset(0);
        LongImageScroller.ScrollToVerticalOffset(0);
        ShowFeedback("固定窗口阅读 · Ctrl+滚轮缩放");
    }

    private void ExitFixedViewport()
    {
        if (!_isLongReaderMode || _isLongImage) return;
        EndContentPan();
        CompletePinMotion();
        _isLongReaderMode = false;
        LongImageScroller.Visibility = Visibility.Collapsed;
        ScrollRail.Visibility = Visibility.Collapsed;
        NormalPinSurface.Visibility = Visibility.Visible;
        UpdateContentMenus();
        SetScaleImmediate(_ordinaryScale, force: true);
        UpdateTextOverlayVisibility();
        ShowFeedback("已恢复普通贴图 · 滚轮缩放窗口");
    }

    private void ToggleContentDetail()
    {
        CompletePinMotion();
        var fit = ContentFitScale();
        if (Math.Abs(_readerScale - fit) > fit * 0.01)
        {
            _detailScale = _readerScale;
            _detailX = LongImageScroller.HorizontalOffset;
            _detailY = LongImageScroller.VerticalOffset;
            SetReaderScale(fit, false);
        }
        else if (_detailScale > fit)
        {
            SetReaderScale(_detailScale, false);
            CompletePinMotion();
            LongImageScroller.ScrollToHorizontalOffset(_detailX);
            LongImageScroller.ScrollToVerticalOffset(_detailY);
        }
    }

    private void QueueContentZoom(double target, bool atCursor)
    {
        if (!_isLongReaderMode || _isCollapsed || _isDockTransitioning || _isPinClosed) return;
        var continuing = _pinMotionKind == PinMotionKind.ContentZoom;
        if (!continuing)
        {
            CompletePinMotion();
            _contentLayoutScale = _readerScale;
            _contentLayoutOffsetX = _contentOffsetX = LongImageScroller.HorizontalOffset;
            _contentLayoutOffsetY = _contentOffsetY = LongImageScroller.VerticalOffset;
        }
        var vw = Math.Max(1, LongImageScroller.ViewportWidth);
        var vh = Math.Max(1, LongImageScroller.ViewportHeight);
        var fit = ContentFitScale();
        // Preserve the existing long-reader width-fit floor. The explicit full-fit
        // command may go below it, allowing a whole long image to be seen in place.
        var minimum = atCursor
            ? (_isLongImage ? Math.Min(_readerBaseScale, _readerScale) : Math.Min(fit, _readerScale))
            : Math.Min(fit, Math.Max(0.000001, target));
        _contentTargetScale = Math.Clamp(target, minimum, Math.Max(minimum, MaximumScale));
        var cursor = atCursor ? Mouse.GetPosition(LongImageScroller) : new Point(vw / 2, vh / 2);
        cursor = new Point(Math.Clamp(cursor.X, 0, vw), Math.Clamp(cursor.Y, 0, vh));
        var imageX = PinContentViewportMath.ImageCoordinate(cursor.X, _contentOffsetX,
            _imageWidthDip * _readerScale, vw, _readerScale);
        var imageY = PinContentViewportMath.ImageCoordinate(cursor.Y, _contentOffsetY,
            _imageHeightDip * _readerScale, vh, _readerScale);
        var from = _readerScale;
        var to = _contentTargetScale;
        // Only a render transform runs during the animation. Extents/layout are
        // committed once on completion, not resized/redecoded on every wheel tick.
        StartPinMotion(PinMotionKind.ContentZoom, 115, progress =>
        {
            var eased = 1 - Math.Pow(1 - progress, 3);
            _readerScale = Math.Exp(Math.Log(from) + (Math.Log(to) - Math.Log(from)) * eased);
            _contentOffsetX = PinContentViewportMath.AnchoredOffset(imageX, _readerScale, cursor.X, vw, _imageWidthDip);
            _contentOffsetY = PinContentViewportMath.AnchoredOffset(imageY, _readerScale, cursor.Y, vh, _imageHeightDip);
            var originX = PinContentViewportMath.Padding(_imageWidthDip * _contentLayoutScale, vw) - _contentLayoutOffsetX;
            var originY = PinContentViewportMath.Padding(_imageHeightDip * _contentLayoutScale, vh) - _contentLayoutOffsetY;
            var x = PinContentViewportMath.Padding(_imageWidthDip * _readerScale, vw) - _contentOffsetX;
            var y = PinContentViewportMath.Padding(_imageHeightDip * _readerScale, vh) - _contentOffsetY;
            var matrix = new Matrix(_readerScale / _contentLayoutScale, 0, 0,
                _readerScale / _contentLayoutScale, x - originX, y - originY);
            LongImageSurface.RenderTransform = new MatrixTransform(matrix);
        }, CommitContentZoom);
        ShowFeedback($"内容 {Math.Round(to * 100)}% · 空格拖动");
    }

    private void CommitContentZoom()
    {
        LongImageSurface.RenderTransform = Transform.Identity;
        if (_isPinClosed || !_isLongReaderMode) return;
        ApplyReaderImageSize();
        UpdateLayout();
        LongImageScroller.ScrollToHorizontalOffset(_contentOffsetX);
        LongImageScroller.ScrollToVerticalOffset(_contentOffsetY);
        UpdateLayout();
        UpdateScrollProgress();
    }

    private void OnContentPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isLongReaderMode || _isCollapsed || _isDockTransitioning || IsPinTextEditing ||
            _isSelectingText || _isPinDrawing || !LongImageScroller.IsMouseOver) return;
        var pan = e.ChangedButton == MouseButton.Middle ||
                  (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space));
        if (!pan)
        {
            if (_pinMotionKind == PinMotionKind.ContentZoom) CompletePinMotion();
            // Letterbox space belongs to the pin too: dragging it moves the
            // window, without stealing text/QR/annotation hit testing inside.
            if (e.ChangedButton == MouseButton.Left && !LongImageSurface.IsMouseOver)
                OnImageMouseLeftButtonDown(sender, e);
            return;
        }
        CompletePinMotion();
        _contentPanButton = e.ChangedButton;
        _contentPanStart = e.GetPosition(LongImageScroller);
        _contentPanX = LongImageScroller.HorizontalOffset;
        _contentPanY = LongImageScroller.VerticalOffset;
        _isContentPanning = Mouse.Capture(LongImageScroller);
        if (_isContentPanning) { Mouse.OverrideCursor = Cursors.ScrollAll; e.Handled = true; }
    }

    private void OnContentPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isContentPanning) return;
        var pressed = _contentPanButton == MouseButton.Middle ? e.MiddleButton : e.LeftButton;
        if (pressed != MouseButtonState.Pressed) { EndContentPan(); return; }
        var point = e.GetPosition(LongImageScroller);
        LongImageScroller.ScrollToHorizontalOffset(_contentPanX + _contentPanStart.X - point.X);
        LongImageScroller.ScrollToVerticalOffset(_contentPanY + _contentPanStart.Y - point.Y);
        e.Handled = true;
    }

    private void OnContentPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isContentPanning || e.ChangedButton != _contentPanButton) return;
        EndContentPan();
        e.Handled = true;
    }

    private void OnContentLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isContentPanning && Mouse.Captured != LongImageScroller) EndContentPan();
    }

    private void EndContentPan()
    {
        if (!_isContentPanning) return;
        _isContentPanning = false;
        Mouse.OverrideCursor = null;
        if (Mouse.Captured == LongImageScroller) LongImageScroller.ReleaseMouseCapture();
    }
}
