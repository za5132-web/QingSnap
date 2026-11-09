using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using QingSnap.App.Infrastructure;
using QingSnap.App.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using FormsScreen = System.Windows.Forms.Screen;

namespace QingSnap.App.Views;

public partial class LongCaptureControlWindow
{
    private bool _hudPlacementChecked;

    private void SetCompactAutomaticControls(bool compact)
    {
        _compactAutomaticControls = compact;
        ShowActivated = !compact;
        Width = compact ? 400 : 530;
        Height = compact ? 112 : 194;
        ControlBody.Margin = compact ? new Thickness(12, 10, 12, 10) : new Thickness(16, 12, 14, 13);
        StatusText.Margin = new Thickness(0, compact ? 5 : 10, 0, 0);
        ScrollProgress.Margin = new Thickness(0, compact ? 5 : 8, 0, 0);
        ActionButtons.Margin = new Thickness(0, compact ? 6 : 11, 0, 0);
        NextButton.Height = CancelButton.Height = compact ? 30 : 34;
        HintText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        FinishButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ModeText.Text = compact ? "自动长截图" : "长截图 · 手动补截";
        UpdateStats();
        if (compact)
        {
            // WPF centering must not overwrite the physical outside-region HUD
            // placement in SourceInitialized on a negative-coordinate monitor.
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != nint.Zero)
        {
            SetNonActivating(compact);
            if (!compact)
            {
                PositionExpandedControls();
            }
        }
    }

    private void UpdateStats()
    {
        StatsText.Text = _compactAutomaticControls
            ? $"{_assembler.FrameCount} 段 · {_assembler.OutputHeight} px"
            : $"{_assembler.FrameCount} 屏  ·  {_assembler.OutputWidth} × {_assembler.OutputHeight} px";
    }

    private void SetNonActivating(bool value)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        style = value ? style | NativeMethods.WsExNoActivate : style & ~NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new nint(style));
    }

    private bool PositionAutomaticControls()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var primary = FormsScreen.FromRectangle(_captureRegion);
        var screens = FormsScreen.AllScreens.OrderBy(screen => screen.DeviceName != primary.DeviceName).ToArray();
        // Crossing a DPI boundary can change WPF's size. Re-evaluate against the
        // realized DPI, then verify the actual HWND again before every capture.
        for (var pass = 0; pass < 2; pass++)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var size = new DrawingSize((int)Math.Ceiling(Width * dpi.DpiScaleX),
                (int)Math.Ceiling(Height * dpi.DpiScaleY));
            DrawingRectangle? placement = null;
            foreach (var screen in screens)
            {
                placement = LongCaptureControlPlacement.FindOutside(_captureRegion, screen.WorkingArea, size,
                    Math.Max(8, (int)Math.Ceiling(8 * dpi.DpiScaleX)));
                if (placement is not null)
                {
                    break;
                }
            }

            if (placement is not { } bounds)
            {
                // Never briefly cover the sampled content when no margin exists.
                // The HWND is hidden once on Loaded; global Enter/Esc stay live.
                var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
                NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost,
                    desktop.Left, desktop.Bottom + 64, size.Width, size.Height, NativeMethods.SwpNoActivate);
                return false;
            }

            if (!NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height, NativeMethods.SwpNoActivate))
            {
                DiagnosticLog.Warning("LongCapture", "无法放置选区外控制浮条，采集期间隐藏浮条。");
                return false;
            }
        }

        return IsControlOutsideCapture();
    }

    private bool IsControlOutsideCapture()
    {
        if (!NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var bounds))
        {
            return false;
        }

        var rectangle = new DrawingRectangle(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        return rectangle.Width > 0 && rectangle.Height > 0 && !_captureRegion.IntersectsWith(rectangle) &&
               FormsScreen.AllScreens.Any(screen => screen.Bounds.Contains(rectangle));
    }

    private void PositionExpandedControls()
    {
        var screen = FormsScreen.FromRectangle(_captureRegion);
        var dpi = VisualTreeHelper.GetDpi(this);
        var size = new DrawingSize((int)Math.Ceiling(Width * dpi.DpiScaleX),
            (int)Math.Ceiling(Height * dpi.DpiScaleY));
        var bounds = LongCaptureControlPlacement.FindOutside(_captureRegion, screen.WorkingArea, size) ??
                     new DrawingRectangle(screen.WorkingArea.Left + Math.Max(0, (screen.WorkingArea.Width - size.Width) / 2),
                         screen.WorkingArea.Top + Math.Max(0, (screen.WorkingArea.Height - size.Height) / 2), size.Width, size.Height);
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HwndTopmost,
            bounds.X, bounds.Y, bounds.Width, bounds.Height, NativeMethods.SwpNoActivate);
    }

    private async Task PrepareControlForCaptureAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_compactAutomaticControls && !_hudPlacementChecked)
        {
            _hudPlacementChecked = true;
            PositionAutomaticControls();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render, token);
        }

        if (IsVisible && (!_compactAutomaticControls || !IsControlOutsideCapture()))
        {
            Hide();
            await WaitForWindowToDisappearAsync(token);
            if (_compactAutomaticControls)
            {
                DiagnosticLog.Info("LongCapture", "选区外没有安全浮条空间，保留蒙层；Enter 停止，Esc 取消。");
            }
        }
    }

    private bool CanSendScrollInput(nint target)
    {
        return !_isClosed && !_stopRequested && !_cancelRequested && IsScrollTargetCurrent(target) &&
               !IsPhysicalInputHeld();
    }

    private bool IsScrollTargetCurrent(nint target)
    {
        if (target == nint.Zero || NativeMethods.GetForegroundWindow() != target ||
            !NativeMethods.GetCursorPos(out var cursor) || !_captureRegion.Contains(cursor.X, cursor.Y))
        {
            return false;
        }

        return NativeMethods.FindScrollRootAtPoint(cursor, target) == target;
    }

    private static bool IsPhysicalInputHeld()
    {
        // This HWND deliberately does not own focus, so query physical state
        // rather than its WPF thread's possibly stale Keyboard/Mouse state.
        int[] keys = [0x01, 0x02, 0x04, 0x10, 0x11, 0x12, 0x5B, 0x5C];
        return keys.Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0);
    }

    private async Task<bool> WaitForInitialInputReleaseAsync(CancellationToken token)
    {
        for (var sample = 0; sample < 100 && IsPhysicalInputHeld(); sample++)
        {
            StatusText.Text = "松开快捷键或鼠标后开始滚动…";
            await Task.Delay(20, token);
        }

        return !IsPhysicalInputHeld();
    }

    private bool IsPointerOverControl()
    {
        return _compactAutomaticControls && IsVisible && NativeMethods.GetCursorPos(out var cursor) &&
               NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var bounds) &&
               cursor.X >= bounds.Left && cursor.X < bounds.Right && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
    }

    private async Task WaitWhilePointerOverControlAsync(nint target, CancellationToken token)
    {
        // Keep the exact same Stop/Cancel targets under the pointer. Never send
        // wheel input to a hovered HUD and never move the HUD before its click.
        while (IsPointerOverControl() && NativeMethods.GetForegroundWindow() == target)
        {
            StatusText.Text = "已暂停 · 移回选区继续，或点击停止";
            await Task.Delay(35, token);
        }

        token.ThrowIfCancellationRequested();
    }
}
