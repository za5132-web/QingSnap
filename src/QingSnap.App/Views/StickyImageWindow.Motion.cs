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
    private double _zoomPositionLog;
    private double _zoomVelocityLog;
    private long _zoomAdvancedAt;
    private bool _isPinMotionRendering;

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
        // A programmatic HWND resize can synchronously enter MediaContext.Resize
        // and raise Rendering again. A newer timestamp is not a reentrancy lock.
        if (_isPinMotionRendering) return;
        _isPinMotionRendering = true;
        try { RenderPinMotionFrame(sender, e); }
        finally { _isPinMotionRendering = false; }
    }

    private void RenderPinMotionFrame(object? sender, EventArgs e)
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
        _rejectedZoomScaleLimit = null;
        ResetPinZoomAnchor();
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
        if (_isPinClosed || _isCollapsed || _isDockTransitioning || _isLongReaderMode) return;
        if (_isZoomHandoff || _completeZoomAfterReplacement) { _zoomDeferredWheel.Enqueue(wheelDelta); return; }
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero || !NativeMethods.GetWindowRect(handle, out var bounds)) return;
        if (_zoomPresentedBounds is { } presented) bounds = presented;
        var continuing = _pinMotionKind == PinMotionKind.Zoom ||
                         (_lastZoomBounds is { } last && last.Left == bounds.Left &&
                          last.Top == bounds.Top && last.Width == bounds.Width && last.Height == bounds.Height);
        if (!continuing)
        {
            (_zoomCenterX, _zoomCenterY) = GetPinZoomCenter(bounds);
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var borderX = Math.Max(1, (int)Math.Round(dpi.DpiScaleX));
        var borderY = Math.Max(1, (int)Math.Round(dpi.DpiScaleY));
        var maximumScale = MaximumSafeZoomScale(dpi.DpiScaleX, dpi.DpiScaleY, borderX, borderY);
        var wasZooming = _pinMotionKind == PinMotionKind.Zoom;
        if (!wasZooming)
        {
            _scale = StickyImageScaleCalculator.ResolveDisplayedScale(
                _imageWidthDip, _imageHeightDip, _scale, bounds.Width, bounds.Height,
                MinimumScale, maximumScale, dpi.DpiScaleX, dpi.DpiScaleY, borderX, borderY);
            _zoomTargetScale = _scale;
        }
        var target = StickyImageScaleCalculator.Calculate(_imageWidthDip, _imageHeightDip,
            PinMotionMath.WheelTarget(_zoomTargetScale, wheelDelta), MinimumScale, maximumScale,
            dpi.DpiScaleX, dpi.DpiScaleY, borderX, borderY);
        // Extra input beyond the same ceiling must not restart an animation
        // that is already approaching that ceiling.
        if (wasZooming && Math.Abs(target.Scale - _zoomTargetScale) < 0.000001)
            return;
        var now = Stopwatch.GetTimestamp();
        if (wasZooming)
        {
            if (_zoomReplacement is null) AdvanceZoomState(now);
        }
        else
        {
            _zoomPositionLog = Math.Log(_scale);
            _zoomVelocityLog = 0;
            _zoomAdvancedAt = now;
        }
        _zoomVelocityLog = PinMotionMath.ZoomRetargetVelocity(
            _zoomPositionLog, _zoomVelocityLog, Math.Log(target.Scale));
        _zoomTargetScale = target.Scale;
        if (Math.Abs(_scale - _zoomTargetScale) < 0.000001 && Math.Abs(_zoomVelocityLog) < 0.000001)
        {
            if (_pinMotionKind != PinMotionKind.None) StopPinMotion();
            CompleteZoomPresentation();
            return;
        }
        if (wasZooming)
        {
            // Extend only the settling deadline. Do not reset the render
            // subscription, de-duplication timestamp or current velocity.
            _pinMotionStarted = now;
            EnsureZoomPreviewCapacity();
            return;
        }
        TryBeginZoomPresentation();
        StartPinMotion(PinMotionKind.Zoom, 300, ApplyZoomFrame, CompleteZoomPresentation);
    }

    private void AdvanceZoomState(long now)
    {
        (_zoomPositionLog, _zoomVelocityLog) = PinMotionMath.AdvanceZoom(
            _zoomPositionLog, _zoomVelocityLog, Math.Log(_zoomTargetScale),
            Stopwatch.GetElapsedTime(_zoomAdvancedAt, now).TotalSeconds);
        _zoomAdvancedAt = now;
    }

    private void ApplyZoomFrame(double progress)
    {
        if (_zoomReplacement is not null)
        {
            if (progress >= 1) _completeZoomAfterReplacement = true;
            return;
        }
        if (_zoomPreview is { IsReady: false })
        {
            if (progress < 1) return;
            DisposeZoomPresentation();
        }
        if (progress >= 1)
        {
            _zoomPositionLog = Math.Log(_zoomTargetScale);
            _zoomVelocityLog = 0;
        }
        else AdvanceZoomState(Stopwatch.GetTimestamp());
        ApplyScaleAtAnchor(Math.Exp(_zoomPositionLog), _zoomCenterX, _zoomCenterY, force: progress >= 1);
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
