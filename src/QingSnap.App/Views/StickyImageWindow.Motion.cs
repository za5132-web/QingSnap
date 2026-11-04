using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QingSnap.App.Infrastructure;
using QingSnap.App.Services;

namespace QingSnap.App.Views;

public partial class StickyImageWindow
{
    private enum PinMotionKind { None, Zoom, Dock, Reveal }

    private Action<double>? _pinMotionFrame;
    private Action? _pinMotionCompleted;
    private PinMotionKind _pinMotionKind;
    private long _pinMotionStarted;
    private double _pinMotionDuration;
    private TimeSpan _lastPinRenderingTime = TimeSpan.MinValue;
    private double _zoomTargetScale;
    private double _zoomCenterX;
    private double _zoomCenterY;
    private NativeMethods.NativeRectangle? _lastZoomBounds;
    private int _pinMotionVersion;

    // Only active pins subscribe to this static event. No timer/render subscription remains idle.
    private void StartPinMotion(PinMotionKind kind, double milliseconds,
        Action<double> frame, Action? completed = null)
    {
        StopPinMotion();
        if (_isPinClosed) return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            FinishPinMotionSafely(frame, completed, applyFinalFrame: true);
            return;
        }

        _pinMotionKind = kind;
        _pinMotionFrame = frame;
        _pinMotionCompleted = completed;
        _pinMotionStarted = Stopwatch.GetTimestamp();
        _pinMotionDuration = Math.Max(1, milliseconds);
        _lastPinRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnPinMotionRendering;
    }

    private void OnPinMotionRendering(object? sender, EventArgs e)
    {
        // Rendering can be raised more than once for the same presentation frame.
        if (e is RenderingEventArgs rendering)
        {
            if (_lastPinRenderingTime == rendering.RenderingTime) return;
            _lastPinRenderingTime = rendering.RenderingTime;
        }
        if (_isPinClosed) { StopPinMotion(); return; }

        try
        {
            var progress = Math.Clamp(Stopwatch.GetElapsedTime(_pinMotionStarted).TotalMilliseconds /
                                      _pinMotionDuration, 0, 1);
            var version = _pinMotionVersion;
            _pinMotionFrame?.Invoke(progress);
            if (progress >= 1 && version == _pinMotionVersion) CompletePinMotion(applyFinalFrame: false);
        }
        catch (Exception exception)
        {
            var finalFrame = _pinMotionFrame;
            var completed = _pinMotionCompleted;
            StopPinMotion();
            DiagnosticLog.Error("PinMotion", exception, "贴图动效失败，已停止动画。");
            // A failed intermediate frame must not strand a half-sized dock.
            FinishPinMotionSafely(finalFrame, completed, applyFinalFrame: true);
        }
    }

    private void StopPinMotion()
    {
        CompositionTarget.Rendering -= OnPinMotionRendering;
        _pinMotionVersion++;
        _pinMotionKind = PinMotionKind.None;
        _pinMotionFrame = null;
        _pinMotionCompleted = null;
    }

    private void CompletePinMotion(bool applyFinalFrame = true)
    {
        var frame = _pinMotionFrame;
        var completed = _pinMotionCompleted;
        StopPinMotion();
        if (_isPinClosed) { _isDockTransitioning = false; return; }
        FinishPinMotionSafely(frame, completed, applyFinalFrame);
    }

    private void FinishPinMotionSafely(Action<double>? frame, Action? completed, bool applyFinalFrame)
    {
        try { if (applyFinalFrame) frame?.Invoke(1); }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinMotion", exception, "贴图动画终态恢复失败。");
        }
        // Completion owns the final visibility/reader state even if geometry failed.
        try { completed?.Invoke(); }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinMotion", exception, "贴图动画完成处理失败。");
        }
        finally
        {
            if (_pinMotionKind == PinMotionKind.None) _isDockTransitioning = false;
        }
    }

    private void OnPinMotionDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        // A HWND can change DPI inside SetWindowPos. Finish after that native message,
        // not re-entrantly; the saved destination remains in physical screen pixels.
        if (_pinMotionKind == PinMotionKind.None) return;
        var version = _pinMotionVersion;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isPinClosed && version == _pinMotionVersion)
            {
                try { CompletePinMotion(); }
                catch (Exception exception)
                {
                    StopPinMotion();
                    _isDockTransitioning = false;
                    DiagnosticLog.Error("PinMotion", exception, "切换显示器时结束贴图动画失败。");
                }
            }
        }, DispatcherPriority.Loaded);
    }

    private void QueuePinZoom(int wheelDelta)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var bounds)) return;
        var continuing = _pinMotionKind == PinMotionKind.Zoom ||
                         (_lastZoomBounds is { } last && last.Left == bounds.Left &&
                          last.Top == bounds.Top && last.Width == bounds.Width && last.Height == bounds.Height);
        if (!continuing)
        {
            _zoomCenterX = bounds.Left + bounds.Width / 2D;
            _zoomCenterY = bounds.Top + bounds.Height / 2D;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var borderX = Math.Max(1, (int)Math.Round(dpi.DpiScaleX));
        var borderY = Math.Max(1, (int)Math.Round(dpi.DpiScaleY));
        if (_pinMotionKind != PinMotionKind.Zoom)
        {
            _scale = StickyImageScaleCalculator.ResolveDisplayedScale(
                _imageWidthDip, _imageHeightDip, _scale, bounds.Width, bounds.Height,
                MinimumScale, MaximumScale, dpi.DpiScaleX, dpi.DpiScaleY, borderX, borderY);
            _zoomTargetScale = _scale;
        }
        var target = StickyImageScaleCalculator.Calculate(_imageWidthDip, _imageHeightDip,
            PinMotionMath.WheelTarget(_zoomTargetScale, wheelDelta), MinimumScale, MaximumScale,
            dpi.DpiScaleX, dpi.DpiScaleY, borderX, borderY);
        _zoomTargetScale = target.Scale;
        var from = _scale;
        var to = _zoomTargetScale;
        StartPinMotion(PinMotionKind.Zoom, 100,
            progress => ApplyScaleAtAnchor(PinMotionMath.Interpolate(from, to, progress),
                _zoomCenterX, _zoomCenterY, force: progress >= 1));
    }

    private bool IsCursorInCollapsedDockZone()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.GetCursorPos(out var cursor) ||
            !NativeMethods.GetWindowRect(handle, out var bounds)) return false;
        var tolerance = Math.Max(2, (int)Math.Round(3 * VisualTreeHelper.GetDpi(this).DpiScaleX));
        return PinMotionMath.IsInsideDockZone(cursor.X, cursor.Y,
            _collapsedDockWorkArea.Left, _collapsedDockWorkArea.Right,
            bounds.Top, bounds.Width, bounds.Height, _collapsedDockUsesLeftEdge, tolerance);
    }
}
