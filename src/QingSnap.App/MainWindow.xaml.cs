using System.Windows;
using QingSnap.App.Models;
using QingSnap.App.Services;
using QingSnap.App.Views;

namespace QingSnap.App;

public partial class MainWindow : Window
{
    private readonly CaptureCoordinator _captureCoordinator;
    private readonly AppSettingsService _settingsService;
    private readonly OcrService _ocrService;
    private readonly QrCodeService _qrCodeService;
    private readonly CaptureHistoryService _historyService;
    private readonly HistoryOcrIndexingService _historyOcrIndexer;
    private readonly ClipboardService _clipboardService;
    private readonly UpdateService _updateService;
    private GlobalHotkeyService? _hotkeys;
    private IReadOnlyList<HotkeyRegistrationFailure> _hotkeyRegistrationFailures = [];
    private bool _hotkeysSuspended;
    private TrayIconService? _tray;
    private SettingsWindow? _settingsWindow;
    private FirstRunTutorialWindow? _tutorialWindow;

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new AppSettingsService();
        _updateService = new UpdateService(_settingsService.DataDirectory);
        var captureService = new ScreenCaptureService();
        _clipboardService = new ClipboardService();
        var stateStore = new AppStateStore();
        _historyService = new CaptureHistoryService(_settingsService);
        _ocrService = new OcrService(_settingsService);
        _qrCodeService = new QrCodeService();
        _historyOcrIndexer = new HistoryOcrIndexingService(_historyService, _ocrService);
        _captureCoordinator = new CaptureCoordinator(
            captureService,
            _clipboardService,
            stateStore,
            _historyService,
            _historyOcrIndexer,
            _ocrService,
            _qrCodeService,
            _settingsService);

        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) =>
        {
            Hide();
            if (!_settingsService.Current.HasCompletedFirstRunTutorial)
            {
                Dispatcher.BeginInvoke(() => OpenTutorialWindow(markCompletedOnClose: true));
            }

            Dispatcher.BeginInvoke(
                () => _ = _ocrService.WarmUpAsync(),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Dispatcher.BeginInvoke(
                () => _ = CheckForUpdatesInBackgroundAsync(),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        Closed += OnClosed;
    }

    public void OpenHistoryWindow() => _captureCoordinator.OpenHistoryWindow();

    public void StartLongCapture() => _captureCoordinator.StartLongCapture();

    public void StartManualLongCapture() => _captureCoordinator.StartManualLongCapture();

    public void RepeatLastCapture() => _captureCoordinator.RepeatLastCapture();

    public void PinLatestCapture() => _captureCoordinator.PinLatestCapture();

    public void PinClipboardImage() => _captureCoordinator.PinClipboardImage();

    public void RecognizeLatestCapture() => _captureCoordinator.RecognizeLatestCapture();

    public void OpenSettingsWindow()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _settingsService,
            _ocrService,
            _updateService,
            () => OpenTutorialWindow(markCompletedOnClose: false),
            () => _hotkeyRegistrationFailures,
            SetHotkeyCaptureMode);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    internal async Task RunResourceWindowStressAsync()
    {
        const int rounds = 10;
        const int cyclesPerWindowPerRound = 5;
        try
        {
            ResourceDiagnostics.Sample("WindowStressInitialIdle");
            for (var round = 1; round <= rounds; round++)
            {
                for (var cycle = 0; cycle < cyclesPerWindowPerRound; cycle++)
                {
                    OpenHistoryWindow();
                    await Task.Delay(260);
                    System.Windows.Application.Current.Windows
                        .OfType<HistoryWindow>()
                        .FirstOrDefault()
                        ?.Close();
                    await Task.Delay(90);

                    OpenSettingsWindow();
                    await Task.Delay(120);
                    _settingsWindow?.Close();
                    await Task.Delay(60);
                }

                ResourceDiagnostics.Sample($"StressRoundFinished{round}");
            }

            await Task.Delay(1000);
            ResourceDiagnostics.Sample("WindowStressFinalIdle");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Resource", exception, "Window resource stress run failed.");
        }
        finally
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    private void OpenTutorialWindow(bool markCompletedOnClose)
    {
        if (_tutorialWindow is not null)
        {
            _tutorialWindow.Activate();
            return;
        }

        _tutorialWindow = new FirstRunTutorialWindow();
        _tutorialWindow.Closed += (_, _) =>
        {
            _tutorialWindow = null;
            if (markCompletedOnClose && !_settingsService.Current.HasCompletedFirstRunTutorial)
            {
                _settingsService.Save(_settingsService.Current with
                {
                    HasCompletedFirstRunTutorial = true
                });
            }
        };
        _tutorialWindow.Show();
        _tutorialWindow.Activate();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ConfigureTray();
        _captureCoordinator.CaptureCompleted += (_, result) =>
            _tray?.ShowCaptureCompleted(result);
        _captureCoordinator.CaptureFailed += (_, message) => _tray?.ShowError(message);
        _captureCoordinator.CaptureDelayStarted += (_, seconds) => _tray?.ShowDelay(seconds);
        _settingsService.SettingsChanged += OnSettingsChanged;

        ConfigureHotkeys();
    }

    private void ConfigureHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        _hotkeyRegistrationFailures = [];

        try
        {
            _hotkeys = new GlobalHotkeyService(this, _settingsService.Current.Hotkeys);
            _hotkeys.ActionRequested += OnHotkeyActionRequested;
            _hotkeyRegistrationFailures = _hotkeys.Register();
            if (_hotkeysSuspended && !_hotkeys.IsRegistered(HotkeyAction.ToggleGlobalHotkeys))
            {
                _hotkeysSuspended = false;
            }

            _hotkeys.SetSuspended(_hotkeysSuspended);
            if (_hotkeyRegistrationFailures.Count > 0)
            {
                var detail = string.Join(Environment.NewLine, _hotkeyRegistrationFailures.Select(failure => failure.DisplayText));
                DiagnosticLog.Warning("Hotkeys", $"部分全局快捷键注册失败：{detail}");
                _tray?.ShowError($"部分快捷键未启用：\n{detail}");
            }
        }
        catch (Exception exception)
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            _hotkeyRegistrationFailures =
            [
                new HotkeyRegistrationFailure(
                    HotkeyAction.RegionCapture,
                    string.Empty,
                    exception.Message)
            ];
            DiagnosticLog.Error("Hotkeys", exception, "初始化全局快捷键失败。");
            _tray?.ShowError(exception.Message);
        }
    }

    private void SetHotkeyCaptureMode(bool isCapturing)
    {
        if (isCapturing)
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            return;
        }

        if (_hotkeys is null)
        {
            ConfigureHotkeys();
        }
    }

    private void OnHotkeyActionRequested(object? sender, HotkeyActionEventArgs e)
    {
        switch (e.Action)
        {
            case HotkeyAction.RegionCapture:
                _captureCoordinator.StartRegionCapture();
                break;
            case HotkeyAction.RepeatLastRegion:
                _captureCoordinator.RepeatLastCapture();
                break;
            case HotkeyAction.AutomaticLongCapture:
                _captureCoordinator.StartLongCapture();
                break;
            case HotkeyAction.ManualLongCapture:
                _captureCoordinator.StartManualLongCapture();
                break;
            case HotkeyAction.PinRecentImage:
                _captureCoordinator.PinClipboardImage();
                break;
            case HotkeyAction.OcrLatestCapture:
                _captureCoordinator.RecognizeLatestCapture();
                break;
            case HotkeyAction.OpenHistory:
                _captureCoordinator.OpenHistoryWindow();
                break;
            case HotkeyAction.ToggleGlobalHotkeys:
                _hotkeysSuspended = !_hotkeysSuspended;
                _hotkeys?.SetSuspended(_hotkeysSuspended);
                _tray?.ShowHotkeyState(_hotkeysSuspended);
                DiagnosticLog.Info(
                    "Hotkeys",
                    _hotkeysSuspended ? "全局快捷键已暂停。" : "全局快捷键已恢复。");
                break;
        }
    }

    private void ConfigureTray()
    {
        _tray?.Dispose();
        _tray = new TrayIconService(_captureCoordinator, _settingsService, OpenSettingsWindow);
        _tray.ExitRequested += (_, _) => CloseApplication();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        ConfigureTray();
        ConfigureHotkeys();
        _ = ApplyOcrSettingsAsync();
        _ = CheckForUpdatesInBackgroundAsync();
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        if (!_settingsService.Current.AutoCheckUpdates)
        {
            return;
        }

        try
        {
            var result = await _updateService.CheckForUpdatesAsync(force: false);
            if (result.Status == UpdateCheckStatus.UpdateAvailable && result.Release is { } release)
            {
                _tray?.ShowUpdateAvailable(release.TagName);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Update", $"后台版本检查异常：{exception.GetType().Name}：{exception.Message}");
        }
    }

    private async Task ApplyOcrSettingsAsync()
    {
        try
        {
            await _ocrService.ApplySettingsAsync();
            _historyOcrIndexer.ScheduleBackfill();
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("OCR", exception, "Failed to apply OCR settings.");
        }
    }

    private void CloseApplication()
    {
        _tray?.Dispose();
        _tray = null;
        _hotkeys?.Dispose();
        _hotkeys = null;
        System.Windows.Application.Current.Shutdown();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _historyOcrIndexer.Dispose();
        _historyService.Dispose();
        _ocrService.Dispose();
        _updateService.Dispose();
        _clipboardService.Dispose();
        _settingsService.SettingsChanged -= OnSettingsChanged;
    }
}
