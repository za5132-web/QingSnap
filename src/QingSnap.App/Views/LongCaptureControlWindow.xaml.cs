using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using MediaColor = System.Windows.Media.Color;

namespace QingSnap.App.Views;

public partial class LongCaptureControlWindow : Window
{
    private const int StopHotkeyId = 2101;
    private const int CancelHotkeyId = 2102;
    private const int MaximumStabilitySamples = 12;
    private const double StableDifferenceThreshold = 1.5;

    private readonly DrawingRectangle _captureRegion;
    private readonly ScreenCaptureService _captureService;
    private readonly LongCaptureMode _mode;
    private readonly nint _targetWindow;
    private readonly LongCaptureAssembler _assembler;
    private readonly int _initialAutomaticWheelDelta;
    private readonly int _maximumUnreliableFrames;
    private HwndSource? _windowSource;
    private bool _stopHotkeyRegistered;
    private bool _cancelHotkeyRegistered;
    private bool _isBusy;
    private bool _isAutomaticRunning;
    private bool _stopRequested;
    private bool _cancelRequested;
    private bool _isClosed;

    public LongCaptureControlWindow(
        DrawingRectangle captureRegion,
        ScreenCaptureService captureService,
        LongCaptureMode mode,
        nint targetWindow,
        AppSettings settings)
    {
        _captureRegion = captureRegion;
        _captureService = captureService;
        _mode = mode;
        _targetWindow = targetWindow;
        _assembler = new LongCaptureAssembler(settings.LongMinimumOverlapPercent);
        _initialAutomaticWheelDelta = -settings.LongScrollWheelDelta;
        _maximumUnreliableFrames = settings.LongMatchRetryCount;
        InitializeComponent();

        if (_mode == LongCaptureMode.Manual)
        {
            ModeText.Text = "MANUAL CAPTURE";
            HintText.Text = "在页面内向下滚动，再点击“截取下一屏”；建议每次保留约 1/3 重叠。";
            ScrollProgress.Visibility = Visibility.Collapsed;
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public event EventHandler<LongCaptureCompletedEventArgs>? CaptureCompleted;

    public event EventHandler? CaptureCancelled;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var firstFrameAccepted = await CaptureNextAsync(_mode == LongCaptureMode.Manual);
        if (firstFrameAccepted && _mode == LongCaptureMode.Automatic && !_cancelRequested)
        {
            await RunAutomaticCaptureAsync();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_mode != LongCaptureMode.Automatic)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowProcedure);
        _stopHotkeyRegistered = NativeMethods.RegisterHotKey(handle, StopHotkeyId, 0, 0x0D);
        _cancelHotkeyRegistered = NativeMethods.RegisterHotKey(handle, CancelHotkeyId, 0, 0x1B);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _stopRequested = true;
        _cancelRequested = true;

        if (_windowSource is null)
        {
            return;
        }

        var handle = _windowSource.Handle;
        if (_stopHotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(handle, StopHotkeyId);
        }

        if (_cancelHotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(handle, CancelHotkeyId);
        }

        _windowSource.RemoveHook(WindowProcedure);
        _windowSource = null;
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != NativeMethods.WmHotkey)
        {
            return nint.Zero;
        }

        var hotkeyId = wParam.ToInt32();
        if (hotkeyId == StopHotkeyId)
        {
            RequestAutomaticStop();
            handled = true;
        }
        else if (hotkeyId == CancelHotkeyId)
        {
            RequestCancel();
            handled = true;
        }

        return nint.Zero;
    }

    private async Task<bool> CaptureNextAsync(bool revealAfterCapture = true)
    {
        if (_isBusy)
        {
            return false;
        }

        _isBusy = true;
        SetButtonsEnabled(false);
        StatusText.Text = _assembler.FrameCount == 0 ? "正在记录首屏…" : "正在分析重叠区域…";

        try
        {
            Hide();
            await WaitForWindowToDisappearAsync();
            var frame = _captureService.CaptureRegion(_captureRegion).Image;
            var result = await Task.Run(() => _assembler.AddFrame(frame));
            if (revealAfterCapture)
            {
                RevealControlWindow();
            }

            ApplyFrameResult(result);
            return result.Accepted;
        }
        catch (Exception exception)
        {
            RevealControlWindow();
            SetErrorStatus($"截取失败：{exception.Message}");
            return false;
        }
        finally
        {
            _isBusy = false;
            SetButtonsEnabled(true);
        }
    }

    private async Task RunAutomaticCaptureAsync()
    {
        _isBusy = true;
        _isAutomaticRunning = true;
        _stopRequested = false;
        NextButton.Content = "停止滚动";
        NextButton.IsEnabled = true;
        BackButton.IsEnabled = false;
        FinishButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = "自动滚动中，按 Enter 可随时停止…";
        ScrollProgress.Visibility = Visibility.Visible;
        ScrollProgress.IsIndeterminate = true;

        var originalCursorAvailable = NativeMethods.GetCursorPos(out var originalCursor);
        var stopReason = AutomaticStopReason.UserRequested;
        var duplicateFrames = 0;
        var unreliableFrames = 0;
        var wheelDelta = _initialAutomaticWheelDelta;

        try
        {
            RevealControlWindow();
            await Task.Delay(650);
            Hide();
            await WaitForWindowToDisappearAsync();

            while (!_stopRequested && !_cancelRequested)
            {
                var scrollTarget = ResolveScrollTarget();
                if (scrollTarget != nint.Zero)
                {
                    NativeMethods.ActivateWindow(scrollTarget);
                }

                NativeMethods.SetCursorPos(
                    _captureRegion.Left + _captureRegion.Width / 2,
                    _captureRegion.Top + _captureRegion.Height / 2);

                if (!NativeMethods.SendMouseWheel(wheelDelta))
                {
                    stopReason = AutomaticStopReason.InputFailed;
                    break;
                }

                var frame = await CaptureStableFrameAsync();
                if (_stopRequested || _cancelRequested)
                {
                    break;
                }

                var result = await Task.Run(() => _assembler.AddFrame(frame));
                ApplyFrameResult(result);

                if (result.Accepted)
                {
                    duplicateFrames = 0;
                    unreliableFrames = 0;
                    wheelDelta = AdjustWheelDelta(wheelDelta, result.AppendedHeight);
                    RevealControlWindow();
                    await Task.Delay(380);
                    Hide();
                    await WaitForWindowToDisappearAsync();
                    continue;
                }

                if (result.Failure == LongCaptureFrameFailure.SafetyLimit)
                {
                    stopReason = AutomaticStopReason.SafetyLimit;
                    break;
                }

                if (result.IsDuplicate)
                {
                    duplicateFrames++;
                    if (duplicateFrames >= 3)
                    {
                        stopReason = AutomaticStopReason.BottomReached;
                        break;
                    }

                    await Task.Delay(180);
                    continue;
                }

                unreliableFrames++;
                if (!await RestoreAfterFailedScrollAsync(wheelDelta))
                {
                    stopReason = AutomaticStopReason.InputFailed;
                    break;
                }

                wheelDelta = ReduceWheelDelta(wheelDelta);
                if (unreliableFrames >= _maximumUnreliableFrames)
                {
                    stopReason = AutomaticStopReason.UnreliableMatch;
                    break;
                }

                await Task.Delay(180);
            }
        }
        catch (Exception exception)
        {
            stopReason = AutomaticStopReason.Failed;
            SetErrorStatus($"自动滚动已停止：{exception.Message}");
        }
        finally
        {
            if (originalCursorAvailable)
            {
                NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            }

            _isAutomaticRunning = false;
            _isBusy = false;
            ReleaseStopHotkey();
            ScrollProgress.IsIndeterminate = false;
            ScrollProgress.Visibility = Visibility.Collapsed;

            if (_cancelRequested)
            {
                if (!_isClosed)
                {
                    CaptureCancelled?.Invoke(this, EventArgs.Empty);
                }
            }
            else
            {
                RevealControlWindow();
                NextButton.Content = "截取下一屏";
                SetButtonsEnabled(true);

                if (stopReason != AutomaticStopReason.Failed)
                {
                    ApplyAutomaticStopReason(stopReason);
                }
            }
        }
    }

    private async Task<BitmapSource> CaptureStableFrameAsync()
    {
        await Task.Delay(170);
        var previous = _captureService.CaptureRegion(_captureRegion).Image;
        var stableSamples = 0;

        for (var sample = 0; sample < MaximumStabilitySamples; sample++)
        {
            if (_stopRequested || _cancelRequested)
            {
                return previous;
            }

            await Task.Delay(105);
            var current = _captureService.CaptureRegion(_captureRegion).Image;
            var difference = await Task.Run(
                () => LongCaptureAssembler.MeasureVisualDifference(previous, current));
            previous = current;

            if (difference <= StableDifferenceThreshold)
            {
                stableSamples++;
                if (stableSamples >= 2)
                {
                    return current;
                }
            }
            else
            {
                stableSamples = 0;
            }
        }

        return previous;
    }

    private async Task<bool> RestoreAfterFailedScrollAsync(int wheelDelta)
    {
        var scrollTarget = ResolveScrollTarget();
        if (scrollTarget != nint.Zero)
        {
            NativeMethods.ActivateWindow(scrollTarget);
        }

        NativeMethods.SetCursorPos(
            _captureRegion.Left + _captureRegion.Width / 2,
            _captureRegion.Top + _captureRegion.Height / 2);

        if (!NativeMethods.SendMouseWheel(-wheelDelta))
        {
            return false;
        }

        // Wait for smooth scrolling and lazy-loaded content to settle before the
        // next, smaller attempt. The restored frame is intentionally not appended.
        await CaptureStableFrameAsync();
        return !_cancelRequested;
    }

    private void ApplyFrameResult(LongCaptureFrameResult result)
    {
        StatusText.Text = result.Message;
        StatsText.Text = $"{_assembler.FrameCount} 屏  ·  {_assembler.OutputWidth} × {_assembler.OutputHeight} px";
        BackButton.IsEnabled = !_isAutomaticRunning && !_isBusy && _assembler.CanUndo;
        FinishButton.IsEnabled = !_isAutomaticRunning && _assembler.FrameCount > 0;

        if (!result.Accepted)
        {
            StatusText.Foreground = new SolidColorBrush(
                result.IsDuplicate
                    ? MediaColor.FromRgb(255, 218, 143)
                    : MediaColor.FromRgb(255, 170, 157));
            return;
        }

        StatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(243, 247, 249));
    }

    private int AdjustWheelDelta(int currentDelta, int appendedHeight)
    {
        var displacementRatio = appendedHeight / (double)Math.Max(1, _captureRegion.Height);
        var magnitude = Math.Abs(currentDelta);
        if (displacementRatio < 0.25 && magnitude < 1200)
        {
            magnitude = Math.Min(1200, magnitude + 120);
        }
        else if (displacementRatio > 0.70 && magnitude > 360)
        {
            magnitude = Math.Max(360, magnitude - 120);
        }

        return -magnitude;
    }

    private static int ReduceWheelDelta(int currentDelta)
    {
        var reducedMagnitude = Math.Max(120, Math.Abs(currentDelta) / 2);
        reducedMagnitude = Math.Max(120, reducedMagnitude / 120 * 120);
        return -reducedMagnitude;
    }

    private nint ResolveScrollTarget()
    {
        var point = new NativeMethods.NativePoint(
            _captureRegion.Left + _captureRegion.Width / 2,
            _captureRegion.Top + _captureRegion.Height / 2);
        var windowAtPoint = NativeMethods.GetAncestor(
            NativeMethods.WindowFromPoint(point),
            NativeMethods.GetAncestorRoot);
        if (windowAtPoint != nint.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(windowAtPoint, out var processId);
            if (processId != Environment.ProcessId)
            {
                return windowAtPoint;
            }
        }

        return _targetWindow;
    }

    private void ApplyAutomaticStopReason(AutomaticStopReason reason)
    {
        switch (reason)
        {
            case AutomaticStopReason.BottomReached:
                StatusText.Text = "已检测到页面底部，可以完成拼接或手动补截。";
                StatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(118, 223, 238));
                break;
            case AutomaticStopReason.UnreliableMatch:
                StatusText.Text = "匹配仍不稳定，已回到最后成功位置；请小幅下滚后补截。";
                StatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 218, 143));
                break;
            case AutomaticStopReason.InputFailed:
                StatusText.Text = "无法发送滚动操作，已切换为手动补截。";
                StatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 170, 157));
                break;
            case AutomaticStopReason.SafetyLimit:
                StatusText.Text = "已达到长截图安全上限，请完成并保存当前结果。";
                StatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 218, 143));
                break;
            default:
                StatusText.Text = "自动滚动已停止，可以完成拼接或手动补截。";
                StatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(243, 247, 249));
                break;
        }
    }

    private void SetErrorStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 170, 157));
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_isAutomaticRunning)
        {
            RequestAutomaticStop();
            return;
        }

        await CaptureNextAsync();
    }

    private async void OnFinishClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _assembler.FrameCount == 0)
        {
            return;
        }

        _isBusy = true;
        SetButtonsEnabled(false);
        StatusText.Text = "正在生成长截图…";
        try
        {
            var image = await Task.Run(_assembler.BuildImage);
            CaptureCompleted?.Invoke(this, new LongCaptureCompletedEventArgs(image));
        }
        catch (Exception exception)
        {
            SetErrorStatus($"生成失败：{exception.Message}");
            _isBusy = false;
            SetButtonsEnabled(true);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _isAutomaticRunning || !_assembler.UndoLastFrame())
        {
            return;
        }

        StatsText.Text = $"{_assembler.FrameCount} 屏  ·  {_assembler.OutputWidth} × {_assembler.OutputHeight} px";
        StatusText.Text = "已撤掉上一段；请向上滚回上一屏，再小幅下滚后截取。";
        StatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(118, 223, 238));
        SetButtonsEnabled(true);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => RequestCancel();

    private void RequestAutomaticStop()
    {
        if (!_isAutomaticRunning)
        {
            return;
        }

        _stopRequested = true;
        NextButton.IsEnabled = false;
        StatusText.Text = "正在停止自动滚动…";
    }

    private void RequestCancel()
    {
        _cancelRequested = true;
        _stopRequested = true;
        if (_isAutomaticRunning)
        {
            StatusText.Text = "正在取消…";
            return;
        }

        if (!_isBusy)
        {
            CaptureCancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ReleaseStopHotkey()
    {
        if (!_stopHotkeyRegistered || _windowSource is null)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_windowSource.Handle, StopHotkeyId);
        _stopHotkeyRegistered = false;
    }

    private void OnDragAreaMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && !_isAutomaticRunning)
        {
            DragMove();
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            RequestCancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _isAutomaticRunning)
        {
            RequestAutomaticStop();
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !_isAutomaticRunning)
        {
            OnBackClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void SetButtonsEnabled(bool isEnabled)
    {
        NextButton.IsEnabled = isEnabled;
        BackButton.IsEnabled = isEnabled && _assembler.CanUndo;
        FinishButton.IsEnabled = isEnabled && _assembler.FrameCount > 0;
        CancelButton.IsEnabled = isEnabled;
    }

    private async Task WaitForWindowToDisappearAsync()
    {
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await Task.Delay(130);
    }

    private void RevealControlWindow()
    {
        if (_isClosed)
        {
            return;
        }

        Show();
        Activate();
    }

    private enum AutomaticStopReason
    {
        UserRequested,
        BottomReached,
        UnreliableMatch,
        SafetyLimit,
        InputFailed,
        Failed
    }
}
