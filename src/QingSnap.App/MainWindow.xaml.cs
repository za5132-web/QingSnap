using System.Windows;
using QingSnap.App.Services;
using QingSnap.App.Views;

namespace QingSnap.App;

public partial class MainWindow : Window
{
    private readonly CaptureCoordinator _captureCoordinator;
    private readonly AppSettingsService _settingsService;
    private readonly OcrService _ocrService;
    private GlobalHotkeyService? _hotkeys;
    private TrayIconService? _tray;
    private SettingsWindow? _settingsWindow;

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new AppSettingsService();
        var captureService = new ScreenCaptureService();
        var clipboardService = new ClipboardService();
        var stateStore = new AppStateStore();
        var historyService = new CaptureHistoryService(_settingsService);
        _ocrService = new OcrService(_settingsService);
        _captureCoordinator = new CaptureCoordinator(
            captureService,
            clipboardService,
            stateStore,
            historyService,
            _ocrService,
            _settingsService);

        SourceInitialized += OnSourceInitialized;
        ContentRendered += (_, _) =>
        {
            Hide();
            Dispatcher.BeginInvoke(
                () => _ = _ocrService.WarmUpAsync(),
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

        _settingsWindow = new SettingsWindow(_settingsService, _ocrService);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ConfigureTray();
        _captureCoordinator.CaptureCompleted += (_, result) =>
            _tray?.ShowCaptureCompleted(result.ImagePath, result.ImageWidth, result.ImageHeight);
        _captureCoordinator.CaptureFailed += (_, message) => _tray?.ShowError(message);
        _captureCoordinator.CaptureDelayStarted += (_, seconds) => _tray?.ShowDelay(seconds);
        _settingsService.SettingsChanged += OnSettingsChanged;

        ConfigureHotkeys();
    }

    private void ConfigureHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;

        try
        {
            _hotkeys = new GlobalHotkeyService(this, _settingsService.Current);
            _hotkeys.RegionCaptureRequested += (_, _) => _captureCoordinator.StartRegionCapture();
            _hotkeys.RepeatCaptureRequested += (_, _) => _captureCoordinator.RepeatLastCapture();
            _hotkeys.PinLatestRequested += (_, _) => _captureCoordinator.PinClipboardImage();
            _hotkeys.Register();
        }
        catch (InvalidOperationException exception)
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            _tray?.ShowError(exception.Message);
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
        ConfigureHotkeys();
        ConfigureTray();
        _ = _ocrService.WarmUpAsync();
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
        _ocrService.Dispose();
        _settingsService.SettingsChanged -= OnSettingsChanged;
    }
}
