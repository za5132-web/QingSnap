using System.Windows;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Services;
using DrawingRectangle = System.Drawing.Rectangle;

namespace QingSnap.App.Views;

public partial class LongCaptureControlWindow : Window
{
    private const int StopHotkeyId = 2101;
    private const int CancelHotkeyId = 2102;
    private const int MaximumStabilitySamples = 24;
    private const double StableDifferenceThreshold = 1.5;

    private readonly DrawingRectangle _captureRegion;
    private readonly ScreenCaptureService _captureService;
    private readonly LongCaptureMode _mode;
    private readonly nint _targetWindow;
    private readonly LongCaptureAssembler _assembler;
    private readonly int _initialAutomaticWheelDelta;
    private readonly int _maximumUnreliableFrames;
    private readonly LongCaptureScrollPolicy _scrollPolicy;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private CancellationTokenSource? _operationCancellation;
    private bool _compactAutomaticControls;
    private LongCaptureVisualSample? _lastAcceptedSample;
    private long _lastRetainedBytes;
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
        // Calibrate conservatively once; subsequent steps use measured movement.
        _initialAutomaticWheelDelta = -Math.Min(360, settings.LongScrollWheelDelta);
        _maximumUnreliableFrames = settings.LongMatchRetryCount;
        _scrollPolicy = new LongCaptureScrollPolicy(settings.LongMinimumOverlapPercent);
        _lifetimeToken = _lifetimeCancellation.Token;
        InitializeComponent();

        if (_mode == LongCaptureMode.Automatic)
        {
            SetCompactAutomaticControls(true);
        }

        if (_mode == LongCaptureMode.Manual)
        {
            ModeText.Text = "手动长截图";
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
        // Show() after a manual sample must not start a second automatic loop.
        Loaded -= OnLoaded;
        try
        {
            ResourceDiagnostics.Sample("LongCaptureStarted", ("LongFrames", _assembler.FrameCount));
            if (_compactAutomaticControls && !IsControlOutsideCapture() &&
                (!_stopHotkeyRegistered || !_cancelHotkeyRegistered))
            {
                SetCompactAutomaticControls(false);
                ReleaseStopHotkey();
                RevealControlWindow();
                SetButtonsEnabled(true);
                SetErrorStatus("控制快捷键被占用，已改为手动模式；点击“截取下一屏”开始。");
                return;
            }

            var firstFrameAccepted = await CaptureNextAsync(_mode == LongCaptureMode.Manual);
            if (firstFrameAccepted && _mode == LongCaptureMode.Automatic && !_cancelRequested && !_isClosed && !_stopRequested)
            {
                await RunAutomaticCaptureAsync();
            }
            else if (_compactAutomaticControls && !_cancelRequested && !_isClosed)
            {
                SetCompactAutomaticControls(false);
                ReleaseStopHotkey();
                RevealControlWindow();
                SetButtonsEnabled(true);
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("LongCapture", exception, "长截图初始化失败。");
            if (!_isClosed)
            {
                SetCompactAutomaticControls(false);
                ReleaseStopHotkey();
                RevealControlWindow();
                SetErrorStatus($"启动失败：{exception.Message}");
            }
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
        SetNonActivating(true);
        PositionAutomaticControls();
        _stopHotkeyRegistered = NativeMethods.RegisterHotKey(handle, StopHotkeyId, 0, 0x0D);
        _cancelHotkeyRegistered = NativeMethods.RegisterHotKey(handle, CancelHotkeyId, 0, 0x1B);
        if (!_stopHotkeyRegistered || !_cancelHotkeyRegistered)
        {
            DiagnosticLog.Warning("LongCapture", "Enter/Esc 全局控制键注册不完整；请使用控制窗口操作。");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _stopRequested = true;
        _cancelRequested = true;
        _lifetimeCancellation.Cancel();
        _operationCancellation?.Cancel();
        _lifetimeCancellation.Dispose();
        ResourceDiagnostics.Sample(
            "LongCaptureClosed",
            ("LongFrames", _assembler.FrameCount),
            ("LongMB", (int)Math.Ceiling(_lastRetainedBytes / (1024D * 1024D))));

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
        // MA_NOACTIVATE: clicks on Stop/Cancel work without taking focus from
        // the scrolling application. No click-through style is used here.
        if (message == 0x0021 && _compactAutomaticControls)
        {
            handled = true;
            return new nint(3);
        }

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
        if (_isBusy || _isClosed)
        {
            return false;
        }

        _isBusy = true;
        SetButtonsEnabled(false);
        StatusText.Text = _assembler.FrameCount == 0 ? "正在记录首屏…" : "正在分析重叠区域…";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _operationCancellation = cancellation;

        try
        {
            await PrepareControlForCaptureAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var frame = _captureService.CaptureRegion(_captureRegion).Image;
            var result = await Task.Run(() => _assembler.AddFrame(frame));
            _lastRetainedBytes = _assembler.EstimatedRetainedBytes;
            cancellation.Token.ThrowIfCancellationRequested();
            LogFrameResult(result);
            if (result.Accepted)
            {
                _lastAcceptedSample = LongCaptureVisualSample.Create(frame, _assembler.ScrollRegion);
            }
            if (revealAfterCapture)
            {
                RevealControlWindow();
            }

            ApplyFrameResult(result);
            return result.Accepted;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("LongCapture", exception, "截取长截图分段失败。");
            RevealControlWindow();
            SetErrorStatus($"截取失败：{exception.Message}");
            return false;
        }
        finally
        {
            _isBusy = false;
            _operationCancellation = null;
            if (!_isClosed)
            {
                SetButtonsEnabled(true);
                if (_cancelRequested)
                {
                    CaptureCancelled?.Invoke(this, EventArgs.Empty);
                }
            }
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
        var useWholeDetents = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _operationCancellation = cancellation;
        var token = cancellation.Token;
        var scrollTarget = nint.Zero;

        try
        {
            await PrepareControlForCaptureAsync(token);
            if (!await WaitForInitialInputReleaseAsync(token))
            {
                stopReason = AutomaticStopReason.TargetChanged;
                return;
            }

            scrollTarget = ResolveScrollTarget();
            if (scrollTarget == nint.Zero ||
                NativeMethods.GetForegroundWindow() != scrollTarget && !NativeMethods.ActivateWindow(scrollTarget))
            {
                stopReason = AutomaticStopReason.InputFailed;
                return;
            }

            NativeMethods.SetCursorPos(
                _captureRegion.Left + _captureRegion.Width / 2,
                _captureRegion.Top + _captureRegion.Height / 2);

            while (!_stopRequested && !_cancelRequested)
            {
                StatusText.Text = "正在平滑滚动…";
                var segmentTimer = Stopwatch.StartNew();
                var motion = await LongCaptureScrollDriver.ScrollAsync(wheelDelta,
                    delta => CanSendScrollInput(scrollTarget) && NativeMethods.SendMouseWheelDelta(delta), token,
                    waitUntilReadyAsync: currentToken => WaitWhilePointerOverControlAsync(scrollTarget, currentToken),
                    useWholeDetents: useWholeDetents);
                var scrollMilliseconds = segmentTimer.Elapsed.TotalMilliseconds;
                if (motion.Stopped)
                {
                    break;
                }

                if (motion.InputFailed)
                {
                    stopReason = CanSendScrollInput(scrollTarget)
                        ? AutomaticStopReason.InputFailed
                        : AutomaticStopReason.TargetChanged;
                    break;
                }

                StatusText.Text = "正在等待画面停稳…";
                await PrepareControlForCaptureAsync(token);
                var settled = await CaptureStableFrameAsync(token, scrollTarget);
                var settleMilliseconds = segmentTimer.Elapsed.TotalMilliseconds - scrollMilliseconds;
                if (_stopRequested || _cancelRequested)
                {
                    break;
                }

                if (!settled.IsStable)
                {
                    stopReason = await RestoreAfterFailedScrollAsync(motion.SentDelta, scrollTarget, token, useWholeDetents)
                        ? AutomaticStopReason.PageUnstable
                        : AutomaticStopReason.RecoveryFailed;
                    break;
                }

                StatusText.Text = "正在对齐拼接…";
                if (!IsScrollTargetCurrent(scrollTarget))
                {
                    stopReason = AutomaticStopReason.TargetChanged;
                    break;
                }

                var result = await Task.Run(() => _assembler.AddFrame(settled.Image));
                _lastRetainedBytes = _assembler.EstimatedRetainedBytes;
                token.ThrowIfCancellationRequested();
                DiagnosticLog.Info("LongCaptureTiming",
                    $"frame={_assembler.FrameCount}; wheel={motion.SentDelta}; " +
                    $"scrollMs={scrollMilliseconds:0.0}; settleMs={settleMilliseconds:0.0}; " +
                    $"matchMs={segmentTimer.Elapsed.TotalMilliseconds - scrollMilliseconds - settleMilliseconds:0.0}; " +
                    $"totalMs={segmentTimer.Elapsed.TotalMilliseconds:0.0}");
                LogFrameResult(result);
                ApplyFrameResult(result);

                if (result.Accepted)
                {
                    duplicateFrames = 0;
                    unreliableFrames = 0;
                    _lastAcceptedSample = LongCaptureVisualSample.Create(settled.Image, _assembler.ScrollRegion);
                    wheelDelta = _scrollPolicy.NextDelta(wheelDelta, result.AppendedHeight,
                        _assembler.ScrollableHeight, result.MatchConfidence, result.UsedRobustFallback);
                    continue;
                }

                if (result.Failure == LongCaptureFrameFailure.SafetyLimit)
                {
                    stopReason = AutomaticStopReason.SafetyLimit;
                    break;
                }

                if (result.IsDuplicate)
                {
                    if (_assembler.FrameCount == 1 && !useWholeDetents)
                    {
                        // Some older controls ignore sub-detent inputs instead
                        // of accumulating them. Try spaced standard detents once
                        // before interpreting an unmoved first view as bottom.
                        useWholeDetents = true;
                        DiagnosticLog.Info("LongCapture", "首段没有位移，尝试间隔发送标准滚轮刻度。");
                        continue;
                    }

                    duplicateFrames++;
                    if (duplicateFrames >= 3)
                    {
                        stopReason = AutomaticStopReason.BottomReached;
                        break;
                    }

                    await Task.Delay(80, token);
                    continue;
                }

                unreliableFrames++;
                if (!await RestoreAfterFailedScrollAsync(motion.SentDelta, scrollTarget, token, useWholeDetents))
                {
                    stopReason = AutomaticStopReason.RecoveryFailed;
                    break;
                }

                wheelDelta = LongCaptureScrollPolicy.ReduceDelta(wheelDelta);
                if (unreliableFrames >= _maximumUnreliableFrames)
                {
                    stopReason = AutomaticStopReason.UnreliableMatch;
                    break;
                }

                await Task.Delay(60, token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Enter/Esc/window close cancel the active pulse or settle delay.
        }
        catch (ScrollTargetChangedException)
        {
            stopReason = AutomaticStopReason.TargetChanged;
            DiagnosticLog.Info("LongCapture", "滚动目标已变化，停止采集并保留成功拼接内容。");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("LongCapture", exception, "自动长截图已停止，保留已拼接内容。");
            stopReason = AutomaticStopReason.Failed;
            SetErrorStatus($"自动滚动已停止：{exception.Message}");
        }
        finally
        {
            if (originalCursorAvailable && NativeMethods.GetCursorPos(out var currentCursor) &&
                _captureRegion.Contains(currentCursor.X, currentCursor.Y) &&
                NativeMethods.GetForegroundWindow() == scrollTarget)
            {
                NativeMethods.SetCursorPos(originalCursor.X, originalCursor.Y);
            }

            _isAutomaticRunning = false;
            _isBusy = false;
            _operationCancellation = null;
            ReleaseStopHotkey();
            ScrollProgress.IsIndeterminate = false;
            ScrollProgress.Visibility = Visibility.Collapsed;

            if (_isClosed)
            {
                // Never show a closed window from an awaited continuation.
            }
            else if (_cancelRequested)
            {
                if (!_isClosed)
                {
                    CaptureCancelled?.Invoke(this, EventArgs.Empty);
                }
            }
            else
            {
                SetCompactAutomaticControls(false);
                RevealControlWindow(stopReason != AutomaticStopReason.TargetChanged);
                NextButton.Content = "截取下一屏";
                SetButtonsEnabled(true);

                if (stopReason != AutomaticStopReason.Failed)
                {
                    ApplyAutomaticStopReason(stopReason);
                }
            }
        }
    }

    private async Task<SettledFrame> CaptureStableFrameAsync(CancellationToken token, nint target)
    {
        await Task.Delay(70, token);
        await WaitWhilePointerOverControlAsync(target, token);
        if (!IsScrollTargetCurrent(target))
        {
            throw new ScrollTargetChangedException();
        }

        var image = _captureService.CaptureRegion(_captureRegion).Image;
        var previous = LongCaptureVisualSample.Create(image, _assembler.ScrollRegion);
        var stableSamples = 0;

        for (var sample = 0; sample < MaximumStabilitySamples; sample++)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(55, token);
            await WaitWhilePointerOverControlAsync(target, token);
            if (!IsScrollTargetCurrent(target))
            {
                throw new ScrollTargetChangedException();
            }

            image = _captureService.CaptureRegion(_captureRegion).Image;
            var current = LongCaptureVisualSample.Create(image, _assembler.ScrollRegion);
            var difference = previous.CompareTo(current);
            previous = current;

            if (difference <= StableDifferenceThreshold)
            {
                stableSamples++;
                if (stableSamples >= 2)
                {
                    return new SettledFrame(image, true);
                }
            }
            else
            {
                stableSamples = 0;
            }
        }

        DiagnosticLog.Warning("LongCapture", "页面在等待上限内未停稳，未将不稳定画面加入拼接。");
        return new SettledFrame(image, false);
    }

    private async Task<bool> RestoreAfterFailedScrollAsync(int wheelDelta, nint scrollTarget, CancellationToken token,
        bool useWholeDetents)
    {
        if (_lastAcceptedSample is null || !CanSendScrollInput(scrollTarget))
        {
            return false;
        }

        StatusText.Text = "正在找回上一段接缝…";
        var restored = await LongCaptureScrollDriver.ScrollAsync(-wheelDelta,
            delta => CanSendScrollInput(scrollTarget) && NativeMethods.SendMouseWheelDelta(delta), token,
            waitUntilReadyAsync: currentToken => WaitWhilePointerOverControlAsync(scrollTarget, currentToken),
            useWholeDetents: useWholeDetents);
        if (!restored.Completed)
        {
            return false;
        }

        var settled = await CaptureStableFrameAsync(token, scrollTarget);
        var matches = settled.IsStable && _lastAcceptedSample.MatchesAnchor(
            LongCaptureVisualSample.Create(settled.Image, _assembler.ScrollRegion));
        if (!matches)
        {
            DiagnosticLog.Warning("LongCapture", "反向滚动未通过上一段画面校验，停止自动重试以保留可靠接缝。");
        }

        return matches;
    }

    private sealed record SettledFrame(BitmapSource Image, bool IsStable);

    private sealed class ScrollTargetChangedException : Exception
    {
    }

    private void ApplyFrameResult(LongCaptureFrameResult result)
    {
        if (_isClosed)
        {
            return;
        }

        StatusText.Text = result.Message;
        UpdateStats();
        BackButton.IsEnabled = !_isAutomaticRunning && !_isBusy && _assembler.CanUndo;
        FinishButton.IsEnabled = !_isAutomaticRunning && _assembler.FrameCount > 0;

        if (!result.Accepted)
        {
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                result.IsDuplicate ? "ThemeWarningBrush" : "ThemeDangerBrush");
            return;
        }

        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeTextBrush");
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
                StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeAccentBrush");
                break;
            case AutomaticStopReason.UnreliableMatch:
                StatusText.Text = "匹配仍不稳定，已回到最后成功位置；请小幅下滚后补截。";
                StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeWarningBrush");
                break;
            case AutomaticStopReason.PageUnstable:
                StatusText.Text = "页面仍在变化，已回到上一段；待内容加载后可手动补截。";
                StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeWarningBrush");
                break;
            case AutomaticStopReason.RecoveryFailed:
                StatusText.Text = "已保留成功拼接内容；请回到上一段附近，小幅滚动后补截。";
                StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeWarningBrush");
                break;
            case AutomaticStopReason.TargetChanged:
                StatusText.Text = "鼠标或焦点已离开滚动页面，已停止；可继续手动补截。";
                StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeWarningBrush");
                break;
            case AutomaticStopReason.InputFailed:
                StatusText.Text = "无法发送滚动操作，已切换为手动补截。";
                StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeDangerBrush");
                break;
            case AutomaticStopReason.SafetyLimit:
                StatusText.Text = "已达到长截图安全上限，请完成并保存当前结果。";
                StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeWarningBrush");
                break;
            default:
                StatusText.Text = "自动滚动已停止，可以完成拼接或手动补截。";
                StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeTextBrush");
                break;
        }
    }

    private void SetErrorStatus(string message)
    {
        if (_isClosed)
        {
            return;
        }

        StatusText.Text = message;
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeDangerBrush");
    }

    private void LogFrameResult(LongCaptureFrameResult result)
    {
        DiagnosticLog.Info(
            "LongCapture",
            $"frame={_assembler.FrameCount}; accepted={result.Accepted}; duplicate={result.IsDuplicate}; " +
            $"displacement={result.AppendedHeight}; output={_assembler.OutputWidth}x{_assembler.OutputHeight}; " +
            $"confidence={result.MatchConfidence:0.000}; retained={_assembler.EstimatedRetainedBytes / (1024D * 1024D):0.0}MB");
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
            var image = await Task.Run(_assembler.BuildImageAndRelease);
            _lastRetainedBytes = 0;
            ResourceDiagnostics.Sample("LongCaptureFinished", ("LongFrames", _assembler.FrameCount), ("LongMB", 0));
            if (!_isClosed)
            {
                if (_cancelRequested)
                {
                    CaptureCancelled?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    CaptureCompleted?.Invoke(this, new LongCaptureCompletedEventArgs(image));
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("LongCapture", exception, "生成长截图失败。");
            SetErrorStatus($"生成失败：{exception.Message}");
            _isBusy = false;
            if (!_isClosed)
            {
                SetButtonsEnabled(true);
                if (_cancelRequested)
                {
                    CaptureCancelled?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _isAutomaticRunning || !_assembler.UndoLastFrame())
        {
            return;
        }

        _lastAcceptedSample = null;
        _lastRetainedBytes = _assembler.EstimatedRetainedBytes;
        UpdateStats();
        StatusText.Text = "已撤掉上一段；请向上滚回上一屏，再小幅下滚后截取。";
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeAccentBrush");
        SetButtonsEnabled(true);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => RequestCancel();

    private void RequestAutomaticStop()
    {
        if (!_isAutomaticRunning)
        {
            if (_compactAutomaticControls && _isBusy)
            {
                _stopRequested = true;
                _operationCancellation?.Cancel();
            }

            return;
        }

        _stopRequested = true;
        _operationCancellation?.Cancel();
        NextButton.IsEnabled = false;
        StatusText.Text = "正在停止自动滚动…";
    }

    private void RequestCancel()
    {
        _cancelRequested = true;
        _stopRequested = true;
        _operationCancellation?.Cancel();
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

    private async Task WaitForWindowToDisappearAsync(CancellationToken token)
    {
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render, token);
        await Task.Delay(130, token);
    }

    private void RevealControlWindow(bool activate = true)
    {
        if (_isClosed)
        {
            return;
        }

        if (_compactAutomaticControls)
        {
            // The automatic HUD is either safely outside the sampled rectangle,
            // or hidden for the entire run when the monitor has no free margin.
            return;
        }

        ShowActivated = activate;
        Show();
        if (activate)
        {
            Activate();
        }
    }

    private enum AutomaticStopReason
    {
        UserRequested,
        BottomReached,
        UnreliableMatch,
        SafetyLimit,
        InputFailed,
        TargetChanged,
        PageUnstable,
        RecoveryFailed,
        Failed
    }
}
