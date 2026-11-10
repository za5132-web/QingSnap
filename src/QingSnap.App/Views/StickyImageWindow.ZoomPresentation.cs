using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QingSnap.App.Infrastructure;
using QingSnap.App.Services;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace QingSnap.App.Views;

public partial class StickyImageWindow
{
    private PinZoomPreviewWindow? _zoomPreview;
    private PinZoomPreviewWindow? _zoomReplacement;
    private double _zoomPreviewCapacity;
    private double _zoomReplacementCapacity;
    private bool _completeZoomAfterReplacement;
    private bool _isZoomHandoff;
    private bool _zoomOwnerHidden;
    private NativeMethods.NativeRectangle? _zoomPresentedBounds;
    private EventHandler? _zoomHandoffRendering;
    private DispatcherOperation? _zoomHandoffOperation;
    private readonly Queue<Action> _zoomDeferredActions = new();
    private readonly Queue<int> _zoomDeferredWheel = new();

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int GetZoomCloakState(nint window, int attribute, out int value, int bytes);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostPinPointerMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private bool TryBeginZoomPresentation()
    {
        if (_zoomPreview is not null || !IsVisible || !IsLoaded || Opacity <= 0 ||
            !SystemParameters.ClientAreaAnimation || _isLongReaderMode || _isCollapsed) return false;
        var handle = new WindowInteropHelper(this).Handle;
        // Hidden automated HWND tests and the initial screenshot handoff must not
        // accidentally open a visible preview over the user's desktop.
        if (handle == 0 || (GetZoomCloakState(handle, 14, out var cloak, sizeof(int)) >= 0 && cloak != 0))
            return false;
        try
        {
            SetPinZoomContentSize(_scale);
            UpdateLayout(); // One setup layout, never a per-frame forced layout.
            var preview = CreateZoomPreview(out _zoomPreviewCapacity);
            _zoomPreview = preview;
            ClosePinAnnotationToolbar();
            preview.Show();
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinZoomPresentation", exception, "准备固定缩放画布失败，回退到原窗口。");
            DisposeZoomPresentation();
            return false;
        }
    }

    private PinZoomPreviewWindow CreateZoomPreview(out double capacity)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var borderX = Math.Max(1, Math.Round(dpi.DpiScaleX));
        var borderY = Math.Max(1, Math.Round(dpi.DpiScaleY));
        var maximum = MaximumSafeZoomScale(dpi.DpiScaleX, dpi.DpiScaleY, borderX, borderY);
        // Reserve modest growth headroom, not a 4x/64MiB surface for a tiny pin.
        // If more is needed, replace the carrier while the old picture is frozen;
        // never resize a visible carrier during an animation.
        capacity = Math.Min(maximum, Math.Max(_scale, _zoomTargetScale) * 1.35);
        var halfWidth = _imageWidthDip * capacity * dpi.DpiScaleX / 2 + borderX;
        var halfHeight = _imageHeightDip * capacity * dpi.DpiScaleY / 2 + borderY;
        var bounds = DrawingRectangle.FromLTRB((int)Math.Floor(_zoomCenterX - halfWidth),
            (int)Math.Floor(_zoomCenterY - halfHeight), (int)Math.Ceiling(_zoomCenterX + halfWidth),
            (int)Math.Ceiling(_zoomCenterY + halfHeight));
        var preview = new PinZoomPreviewWindow(this, NormalPinSurface, bounds, borderX, borderY);
        preview.Ready += OnZoomPreviewReady;
        preview.WheelRequested += OnZoomPreviewWheel;
        preview.PointerPressed += OnZoomPreviewPointer;
        preview.Closed += OnZoomPreviewClosed;
        preview.Present(_zoomCenterX, _zoomCenterY, _imageWidthDip * _scale * dpi.DpiScaleX,
            _imageHeightDip * _scale * dpi.DpiScaleY);
        return preview;
    }

    private void OnZoomPreviewClosed(object? sender, EventArgs e)
    {
        if (_isPinClosed) return;
        try
        {
            DiagnosticLog.Warning("PinZoomPresentation", "缩放预览提前关闭，已恢复原贴图窗口。");
            if (ReferenceEquals(sender, _zoomReplacement))
            {
                ReleaseZoomPreview(_zoomReplacement);
                _zoomReplacement = null;
                _completeZoomAfterReplacement = false;
                _zoomTargetScale = _scale;
                CompletePinMotion();
                CompleteZoomPresentation();
            }
            else if (ReferenceEquals(sender, _zoomPreview))
            {
                StopPinMotion();
                DisposeZoomPresentation();
                ApplyScaleAtAnchor(_scale, _zoomCenterX, _zoomCenterY, true);
                DrainZoomInteractions();
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinZoomPresentation", exception, "预览窗口异常关闭后的恢复失败。");
            DisposeZoomPresentation();
        }
    }

    private void EnsureZoomPreviewCapacity()
    {
        if (_zoomPreview is null || !_zoomPreview.IsReady || _zoomReplacement is not null ||
            _isZoomHandoff || _zoomTargetScale <= _zoomPreviewCapacity + 0.000001) return;
        try
        {
            _zoomPositionLog = Math.Log(_scale);
            _zoomAdvancedAt = Stopwatch.GetTimestamp();
            _zoomReplacement = CreateZoomPreview(out _zoomReplacementCapacity);
            _zoomReplacement.Show();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinZoomPresentation", exception, "扩大固定缩放画布失败，保留当前显示尺寸。");
            ReleaseZoomPreview(_zoomReplacement);
            _zoomReplacement = null;
            _zoomTargetScale = _scale;
            CompletePinMotion();
        }
    }

    private void OnZoomPreviewReady(object? sender, EventArgs e)
    {
        if (_zoomReplacement is not null && ReferenceEquals(sender, _zoomReplacement))
        {
            var old = _zoomPreview;
            _zoomPreview = _zoomReplacement;
            _zoomPreviewCapacity = _zoomReplacementCapacity;
            _zoomReplacement = null;
            ReleaseZoomPreview(old);
            _zoomPositionLog = Math.Log(_scale);
            _zoomAdvancedAt = Stopwatch.GetTimestamp();
            _pinMotionStarted = _zoomAdvancedAt;
            EnsureZoomPreviewCapacity();
            if (_completeZoomAfterReplacement && _zoomReplacement is null)
            {
                _completeZoomAfterReplacement = false;
                ApplyScaleAtAnchor(_zoomTargetScale, _zoomCenterX, _zoomCenterY, true);
                CompleteZoomPresentation();
            }
            return;
        }
        if (_zoomPreview is null || !ReferenceEquals(sender, _zoomPreview) || _isPinClosed || _isZoomHandoff) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            _zoomOwnerHidden = handle != 0 && NativeMethods.SetWindowCloaked(handle, true);
            if (!_zoomOwnerHidden) throw new InvalidOperationException("无法暂藏原贴图窗口。");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinZoomPresentation", exception, "无法暂藏原贴图窗口，已取消临时画布。");
            DisposeZoomPresentation();
            CompletePinMotion();
            return;
        }
        // Inputs can accumulate during first presentation; start their motion
        // from the still-visible original scale, without a startup time jump.
        _zoomPositionLog = Math.Log(_scale);
        _zoomVelocityLog = 0;
        _zoomAdvancedAt = Stopwatch.GetTimestamp();
        _pinMotionStarted = _zoomAdvancedAt;
        EnsureZoomPreviewCapacity();
    }

    private bool PresentZoomSurface(StickyImageScaleResult size, double centerX, double centerY,
        double dpiX, double dpiY)
    {
        if (_zoomPreview is not { IsReady: true } preview || _isZoomHandoff) return false;
        var left = PinMotionMath.AnchoredOrigin(centerX, size.Width);
        var top = PinMotionMath.AnchoredOrigin(centerY, size.Height);
        preview.Present(centerX, centerY, _imageWidthDip * size.Scale * dpiX, _imageHeightDip * size.Scale * dpiY);
        _zoomPresentedBounds = new NativeMethods.NativeRectangle
            { Left = left, Top = top, Right = left + size.Width, Bottom = top + size.Height };
        _lastZoomBounds = _zoomPresentedBounds;
        return true;
    }

    private void CompleteZoomPresentation()
    {
        if (_zoomReplacement is not null) { _completeZoomAfterReplacement = true; return; }
        if (_zoomPreview is null || _isZoomHandoff) return;
        if (!_zoomPreview.IsReady || !_zoomOwnerHidden)
        {
            DisposeZoomPresentation();
            ApplyScaleAtAnchor(_zoomTargetScale, _zoomCenterX, _zoomCenterY, true);
            return;
        }
        try
        {
            _zoomPreview.FreezeContent();
            _isZoomHandoff = true;
            // Only this final resize touches the original HWND. Its old surface
            // remains cloaked, covered by an immutable preview until presented.
            ApplyScaleAtAnchor(_scale, _zoomCenterX, _zoomCenterY, true);
            UpdateLayout();
            var frames = 0;
            var previous = TimeSpan.MinValue;
            _zoomHandoffRendering = (_, args) =>
            {
                if (args is RenderingEventArgs render)
                {
                    if (render.RenderingTime == previous) return;
                    previous = render.RenderingTime;
                }
                if (++frames < 2) return;
                CompositionTarget.Rendering -= _zoomHandoffRendering;
                _zoomHandoffRendering = null;
                _zoomHandoffOperation = Dispatcher.BeginInvoke(FinishZoomHandoff, DispatcherPriority.ContextIdle);
            };
            CompositionTarget.Rendering += _zoomHandoffRendering;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinZoomPresentation", exception, "恢复缩放后的贴图窗口失败。");
            FinishZoomHandoff();
        }
    }

    private void FinishZoomHandoff()
    {
        _zoomHandoffOperation = null;
        if (_isPinClosed) { DisposeZoomPresentation(); return; }
        try
        {
            NativeMethods.DwmFlush();
            RestoreZoomOwner();
            NativeMethods.DwmFlush();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("PinZoomPresentation", exception, "交接缩放画布失败，恢复原贴图窗口。");
        }
        finally { DisposeZoomPresentation(); }
        if (_isEditingPin && IsActive && _pinAnnotationToolbar is not null) _pinAnnotationToolbar.IsOpen = true;
        DrainZoomInteractions();
    }

    private bool DeferZoomInteraction(Action action)
    {
        if (_zoomPreview is null) return false;
        _zoomDeferredActions.Enqueue(action);
        CompletePinMotion();
        CompleteZoomPresentation();
        if (_zoomPreview is null) DrainZoomInteractions();
        return true;
    }

    private void DrainZoomInteractions()
    {
        while (!_isPinClosed && _zoomDeferredActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception exception) { DiagnosticLog.Error("PinZoomPresentation", exception, "缩放后继续贴图交互失败。"); }
        }
        while (!_isPinClosed && _zoomDeferredWheel.TryDequeue(out var delta))
        {
            try { QueuePinZoom(delta); }
            catch (Exception exception) { DiagnosticLog.Error("PinZoomPresentation", exception, "恢复待处理的滚轮输入失败。"); }
        }
    }

    private void OnZoomPreviewWheel(int delta, int modifiers, DrawingPoint point)
    {
        if (_isPinClosed) return;
        if (((ModifierKeys)modifiers).HasFlag(ModifierKeys.Control))
        {
            DeferZoomInteraction(() =>
            {
                EnterFixedViewport();
                SetReaderScale(PinMotionMath.WheelTarget(_readerScale, delta), true);
            });
            return;
        }
        if (_isEditingPin && !((ModifierKeys)modifiers).HasFlag(ModifierKeys.Control))
        {
            DeferZoomInteraction(() => ForwardZoomWheel(delta, modifiers, point));
            return;
        }
        QueuePinZoom(delta);
    }

    private void ForwardZoomWheel(int delta, int modifiers, DrawingPoint point)
    {
        var flags = (((ModifierKeys)modifiers).HasFlag(ModifierKeys.Control) ? 8 : 0) |
                    (((ModifierKeys)modifiers).HasFlag(ModifierKeys.Shift) ? 4 : 0);
        PostPinPointerMessage(new WindowInteropHelper(this).Handle, 0x020A,
            (nint)((delta << 16) | flags), PackZoomPoint(point.X, point.Y));
    }

    private void OnZoomPreviewPointer(MouseButton button, DrawingPoint point) =>
        DeferZoomInteraction(() =>
        {
            Activate();
            Focus();
            var local = PointFromScreen(new System.Windows.Point(point.X, point.Y));
            var dpi = VisualTreeHelper.GetDpi(this);
            var packed = PackZoomPoint((int)Math.Round(local.X * dpi.DpiScaleX), (int)Math.Round(local.Y * dpi.DpiScaleY));
            var (down, up, flag, key) = button switch
            {
                MouseButton.Right => (0x0204U, 0x0205U, 2, 2),
                MouseButton.Middle => (0x0207U, 0x0208U, 16, 4),
                _ => (0x0201U, 0x0202U, 1, 1)
            };
            var handle = new WindowInteropHelper(this).Handle;
            PostPinPointerMessage(handle, down, flag, packed);
            if (GetAsyncKeyState(key) >= 0) PostPinPointerMessage(handle, up, 0, packed);
        });

    private static nint PackZoomPoint(int x, int y) => unchecked((nint)((y & 0xFFFF) << 16 | (x & 0xFFFF)));

    private void RestoreZoomOwner()
    {
        if (!_zoomOwnerHidden) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (!_isPinClosed && handle != 0)
        {
            if (!NativeMethods.SetWindowCloaked(handle, false))
                DiagnosticLog.Warning("PinZoomPresentation", "恢复贴图窗口显示失败。");
        }
        _zoomOwnerHidden = false;
    }

    private void DisposeZoomPresentation()
    {
        if (_zoomHandoffRendering is not null) CompositionTarget.Rendering -= _zoomHandoffRendering;
        _zoomHandoffRendering = null;
        _zoomHandoffOperation?.Abort();
        _zoomHandoffOperation = null;
        RestoreZoomOwner();
        var preview = _zoomPreview;
        _zoomPreview = null;
        ReleaseZoomPreview(preview);
        ReleaseZoomPreview(_zoomReplacement);
        _zoomReplacement = null;
        _zoomPreviewCapacity = 0;
        _completeZoomAfterReplacement = false;
        _isZoomHandoff = false;
        _zoomPresentedBounds = null;
        if (_isPinClosed) { _zoomDeferredActions.Clear(); _zoomDeferredWheel.Clear(); }
    }

    private void ReleaseZoomPreview(PinZoomPreviewWindow? preview)
    {
        if (preview is not null)
        {
            preview.Ready -= OnZoomPreviewReady;
            preview.WheelRequested -= OnZoomPreviewWheel;
            preview.PointerPressed -= OnZoomPreviewPointer;
            preview.Closed -= OnZoomPreviewClosed;
            preview.Dispose();
        }
    }
}
