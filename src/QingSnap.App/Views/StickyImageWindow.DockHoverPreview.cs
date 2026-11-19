using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Size = System.Windows.Size;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;

namespace QingSnap.App.Views;

public partial class StickyImageWindow
{
    private bool _dockHoverPreviewEnabled;
    private double _dockHoverOpacity = 0.8;
    private AppSettingsService? _dockHoverSettingsService;
    private PinDockHoverPreviewWindow? _dockHoverPreview;
    private bool _dockHoverWanted;
    private bool _dockHoverCommitted;
    private bool _dockHoverReturning;
    private Rect _dockHoverSourceRect;
    private bool _dockHoverHandingOff;
    private EventHandler? _dockHoverHandoffRendering;
    private DispatcherOperation? _dockHoverHandoffOperation;

    private void InitializeDockHoverPreview(AppSettings settings, AppSettingsService? service)
    {
        _dockHoverSettingsService = service;
        ApplyDockHoverSettings(settings);
        if (service is not null) service.SettingsChanged += OnDockHoverSettingsChanged;
    }

    private void OnDockHoverSettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnDockHoverSettingsChanged(sender, e)); return; }
        if (!_isPinClosed && _dockHoverSettingsService is not null) ApplyDockHoverSettings(_dockHoverSettingsService.Current);
    }

    private void ApplyDockHoverSettings(AppSettings settings)
    {
        _dockHoverPreviewEnabled = settings.PinDockHoverPreviewEnabled;
        _dockHoverOpacity = Math.Clamp(settings.PinDockHoverPreviewOpacity, 20, 100) / 100D;
        if (!_dockHoverPreviewEnabled)
        {
            if (_dockHoverCommitted) CompletePinMotion();
            DisposeDockHoverPreview();
        }
        else if (!_dockHoverCommitted) _dockHoverPreview?.SetPreviewOpacity(_dockHoverOpacity);
    }

    // Capture only lightweight viewport metadata on collapse. Pixel data is
    // produced lazily on hover, including when the option was enabled later.
    private void RememberDockHoverViewport()
    {
        _dockHoverSourceRect = new Rect(0, 0, _image.PixelWidth, _image.PixelHeight);
        if (!_isLongReaderMode || LongPinnedImage.ActualWidth <= 0 || LongPinnedImage.ActualHeight <= 0) return;
        var x = Math.Max(0, LongImageScroller.HorizontalOffset / LongPinnedImage.ActualWidth * _image.PixelWidth);
        var y = Math.Max(0, LongImageScroller.VerticalOffset / LongPinnedImage.ActualHeight * _image.PixelHeight);
        var width = Math.Min(_image.PixelWidth - x, LongImageScroller.ViewportWidth / LongPinnedImage.ActualWidth * _image.PixelWidth);
        var height = Math.Min(_image.PixelHeight - y, LongImageScroller.ViewportHeight / LongPinnedImage.ActualHeight * _image.PixelHeight);
        if (width > 0 && height > 0) _dockHoverSourceRect = new Rect(x, y, width, height);
    }

    private BitmapSource CreateDockHoverSnapshot(NativeMethods.NativeRectangle expanded)
    {
        // At most 2048 on either axis (16 MiB); never composite a full long image.
        var ratio = Math.Min(1, 2048D / Math.Max(expanded.Width, expanded.Height));
        var width = Math.Max(1, (int)Math.Round(expanded.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(expanded.Height * ratio));
        var source = _dockHoverSourceRect.IsEmpty || _dockHoverSourceRect.Width <= 0
            ? new Rect(0, 0, _image.PixelWidth, _image.PixelHeight) : _dockHoverSourceRect;
        var fit = Math.Min(width / source.Width, height / source.Height);
        var target = new Rect((width - source.Width * fit) / 2, (height - source.Height * fit) / 2,
            source.Width * fit, source.Height * fit);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle((Brush?)TryFindResource("ThemeImageBackdropBrush") ?? Brushes.Transparent, null, new Rect(0, 0, width, height));
            context.PushClip(new RectangleGeometry(target));
            context.DrawImage(_image, new Rect(target.Left - source.Left * fit, target.Top - source.Top * fit,
                _image.PixelWidth * fit, _image.PixelHeight * fit));
            if (_pinAnnotations?.HasAnnotations == true)
            {
                _pinAnnotationLayer.Measure(new Size(_image.PixelWidth, _image.PixelHeight));
                _pinAnnotationLayer.Arrange(new Rect(0, 0, _image.PixelWidth, _image.PixelHeight));
                var brush = new VisualBrush(_pinAnnotationLayer)
                { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = source, Stretch = Stretch.Fill, AutoLayoutContent = false };
                context.DrawRectangle(brush, null, target);
            }
            context.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        return bitmap;
    }

    private void BeginDockHoverPreview(bool showWindow = true)
    {
        if (!_dockHoverPreviewEnabled || !_isCollapsed || _isPinClosed || _isDraggingCollapsedDock ||
            _isDockTransitioning || _expandedBounds is not { } expanded || expanded.Width <= 0 || expanded.Height <= 0) return;
        if (_dockHoverWanted || _dockHoverCommitted) return;
        if (_pinMotionKind == PinMotionKind.Reveal) CompletePinMotion();
        _dockHoverWanted = true;
        try
        {
            if (_dockHoverPreview is null)
            {
                if (!NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var dock)) { _dockHoverWanted = false; return; }
                var preview = new PinDockHoverPreviewWindow(CreateDockHoverSnapshot(expanded), dock, _dockHoverOpacity);
                _dockHoverPreview = preview;
                preview.Ready += OnDockHoverPreviewReady;
                preview.Closed += OnDockHoverPreviewClosed;
                if (showWindow) preview.Show();
            }
            else if (_dockHoverPreview.IsReady) AnimateDockHoverPreview(true);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinDockPreview", exception, "准备收纳预览失败，缩略图与点击恢复仍可使用。");
            DisposeDockHoverPreview();
        }
    }

    private void OnDockHoverPreviewReady(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _dockHoverPreview)) return;
        if (!_dockHoverWanted) { DisposeDockHoverPreview(); return; }
        AnimateDockHoverPreview(true);
    }

    private void OnDockHoverPreviewClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _dockHoverPreview)) return;
        var committed = _dockHoverCommitted;
        DisposeDockHoverPreview();
        if (committed && !_isPinClosed) RestoreFromDock();
    }

    private void AnimateDockHoverPreview(bool expanded)
    {
        if (_dockHoverPreview is not { IsReady: true } preview || _expandedBounds is not { } expandedBounds) return;
        var handle = new WindowInteropHelper(preview).Handle;
        if (!NativeMethods.GetWindowRect(handle, out var from) ||
            !NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var dock)) return;
        _dockHoverReturning = !expanded;
        AnimateWindowBounds(handle, from, expanded ? expandedBounds : dock, () =>
        {
            if (_dockHoverCommitted) PromoteDockHoverPreview();
            else if (!_dockHoverWanted)
            {
                DisposeDockHoverPreview();
                SetCollapsedThumbnailRevealed(false);
            }
        }, hoverPreview: true);
    }

    private void HideDockHoverPreview()
    {
        if (_dockHoverCommitted) return;
        if (!_dockHoverWanted)
        {
            if (_dockHoverPreview is null) SetCollapsedThumbnailRevealed(false);
            return;
        }
        _dockHoverWanted = false;
        if (_dockHoverPreview is { IsReady: true }) AnimateDockHoverPreview(false);
        else
        {
            DisposeDockHoverPreview();
            SetCollapsedThumbnailRevealed(false);
        }
    }

    private bool TryCommitDockHoverPreview()
    {
        if (_dockHoverPreview is null || !_isCollapsed) return false;
        _dockHoverCommitted = _dockHoverWanted = true;
        _collapsedDockHideTimer.Stop();
        _dockHoverPreview.SetPreviewOpacity(1);
        if (_dockHoverPreview.IsReady && _dockHoverReturning) AnimateDockHoverPreview(true);
        else if (_dockHoverPreview.IsReady && _pinMotionKind != PinMotionKind.DockHoverPreview) PromoteDockHoverPreview();
        return true; // In flight: finish the existing outward animation, never restart it.
    }

    private void PromoteDockHoverPreview()
    {
        if (_expandedBounds is null || _isPinClosed || _dockHoverHandingOff) return;
        // Restore original visual/reader state using its existing completion,
        // but bypass motion because the preview is already at the destination.
        _dockHoverHandingOff = true;
        try
        {
            RestoreFromDockCore(animate: false);
            UpdateLayout();
            var frames = 0;
            var previous = TimeSpan.MinValue;
            _dockHoverHandoffRendering = (_, args) =>
            {
                if (args is RenderingEventArgs render)
                {
                    if (render.RenderingTime == previous) return;
                    previous = render.RenderingTime;
                }
                if (++frames < 2) return;
                CompositionTarget.Rendering -= _dockHoverHandoffRendering;
                _dockHoverHandoffRendering = null;
                _dockHoverHandoffOperation = Dispatcher.BeginInvoke(FinishDockHoverHandoff, DispatcherPriority.ContextIdle);
            };
            CompositionTarget.Rendering += _dockHoverHandoffRendering;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinDockPreview", exception, "交接收纳预览失败，保留正式贴图。");
            DisposeDockHoverPreview();
        }
    }

    private void FinishDockHoverHandoff()
    {
        _dockHoverHandoffOperation = null;
        try { if (!_isPinClosed) NativeMethods.DwmFlush(); }
        catch (Exception exception) { DiagnosticLog.Error("PinDockPreview", exception, "等待贴图画面交接失败。"); }
        finally { DisposeDockHoverPreview(); }
    }

    private void DisposeDockHoverPreview()
    {
        if (_pinMotionKind == PinMotionKind.DockHoverPreview) StopPinMotion();
        if (_dockHoverHandoffRendering is not null) CompositionTarget.Rendering -= _dockHoverHandoffRendering;
        _dockHoverHandoffRendering = null;
        _dockHoverHandoffOperation?.Abort();
        _dockHoverHandoffOperation = null;
        _dockHoverHandingOff = false;
        var preview = _dockHoverPreview;
        _dockHoverPreview = null;
        _dockHoverWanted = _dockHoverCommitted = _dockHoverReturning = false;
        if (preview is null) return;
        preview.Ready -= OnDockHoverPreviewReady;
        preview.Closed -= OnDockHoverPreviewClosed;
        preview.Close();
    }
}
