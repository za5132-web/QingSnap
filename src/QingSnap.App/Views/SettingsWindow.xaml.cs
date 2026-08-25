using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QingSnap.App.Controls;
using QingSnap.App.Models;
using QingSnap.App.Services;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QingSnap.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly OcrService _ocrService;
    private CancellationTokenSource? _ocrOperationCancellation;

    public SettingsWindow(AppSettingsService settingsService, OcrService ocrService)
    {
        _settingsService = settingsService;
        _ocrService = ocrService;
        InitializeComponent();
        LoadSettings(settingsService.Current);
        RefreshOcrStatus();
        Closed += (_, _) => _ocrOperationCancellation?.Cancel();
    }

    private void LoadSettings(AppSettings settings)
    {
        CaptureHotkeyBox.Text = settings.CaptureHotkey;
        PinHotkeyBox.Text = settings.PinHotkey;
        RepeatHotkeyBox.Text = settings.RepeatHotkey;
        StartupCheck.IsChecked = settings.StartWithWindows;
        HistoryDirectoryBox.Text = settings.HistoryDirectory;
        RetentionBox.Text = settings.HistoryRetentionDays.ToString();
        SelectCombo(FormatCombo, settings.OutputFormat);
        JpegQualityBox.Text = settings.JpegQuality.ToString();
        AutoCopyCheck.IsChecked = settings.AutoCopy;
        SelectComboByTag(DelayCombo, settings.CaptureDelaySeconds.ToString());
        SmartSelectionCheck.IsChecked = settings.SmartWindowSelection;
        MagnifierCheck.IsChecked = settings.ShowMagnifier;
        SelectComboByTag(CloseInteractionCombo, settings.CloseInteraction);
        SelectComboByTag(
            OcrModelCombo,
            settings.OcrModel);
        SelectComboByTag(OcrPerformanceCombo, settings.OcrPerformanceMode);
        LongWheelBox.Text = settings.LongScrollWheelDelta.ToString();
        LongRetryBox.Text = settings.LongMatchRetryCount.ToString();
        LongOverlapBox.Text = settings.LongMinimumOverlapPercent.ToString();
        AnnotationColorBox.Text = settings.AnnotationColor;
        AnnotationThicknessBox.Text = settings.AnnotationThickness.ToString("0.#");
        AnnotationFontSizeBox.Text = settings.AnnotationFontSize.ToString("0.#");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var captureHotkey = CaptureHotkeyBox.Text.Trim();
            var pinHotkey = PinHotkeyBox.Text.Trim();
            var repeatHotkey = RepeatHotkeyBox.Text.Trim();
            if (!GlobalHotkeyService.IsValidGesture(captureHotkey) ||
                !GlobalHotkeyService.IsValidGesture(pinHotkey) ||
                !GlobalHotkeyService.IsValidGesture(repeatHotkey))
            {
                throw new InvalidOperationException("快捷键格式无效，请使用 Ctrl / Shift / Alt + F1–F12。");
            }

            if (new[] { captureHotkey, pinHotkey, repeatHotkey }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            {
                throw new InvalidOperationException("三个全局快捷键不能重复。");
            }

            var settings = _settingsService.Current with
            {
                CaptureHotkey = captureHotkey,
                PinHotkey = pinHotkey,
                RepeatHotkey = repeatHotkey,
                StartWithWindows = StartupCheck.IsChecked == true,
                HistoryDirectory = HistoryDirectoryBox.Text,
                HistoryRetentionDays = ParseInt(RetentionBox, "自动清理天数"),
                OutputFormat = SelectedContent(FormatCombo, "PNG"),
                JpegQuality = ParseInt(JpegQualityBox, "JPG 质量"),
                AutoCopy = AutoCopyCheck.IsChecked == true,
                CaptureDelaySeconds = int.Parse(SelectedTag(DelayCombo, "0")),
                SmartWindowSelection = SmartSelectionCheck.IsChecked == true,
                ShowMagnifier = MagnifierCheck.IsChecked == true,
                CloseInteraction = SelectedTag(CloseInteractionCombo, "Escape"),
                OcrEngine = SelectedOcrModel() == OcrModelManager.NoModel ? "None" : "Advanced",
                OcrModel = SelectedOcrModel(),
                OcrPerformanceMode = SelectedTag(OcrPerformanceCombo, "Instant"),
                LongScrollWheelDelta = ParseInt(LongWheelBox, "长截图滚动步长"),
                LongMatchRetryCount = ParseInt(LongRetryBox, "长截图重试次数"),
                LongMinimumOverlapPercent = ParseInt(LongOverlapBox, "长截图最小重叠"),
                AnnotationColor = AnnotationColorBox.Text,
                AnnotationThickness = ParseDouble(AnnotationThicknessBox, "标注线条粗细"),
                AnnotationFontSize = ParseDouble(AnnotationFontSizeBox, "标注文字大小")
            };
            _settingsService.Save(settings);
            StatusText.Text = "设置已保存并应用";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(118, 223, 238));
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 145, 137));
        }
    }

    private void OnBrowseHistoryClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择 QingSnap 截图记录目录",
            SelectedPath = HistoryDirectoryBox.Text,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            HistoryDirectoryBox.Text = dialog.SelectedPath;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnOcrEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || OcrEngineDescriptionText is null)
        {
            return;
        }

        RefreshOcrStatus();
    }

    private async void OnOcrRuntimeInstallClick(object sender, RoutedEventArgs e)
    {
        BeginOcrOperation();
        try
        {
            await _ocrService.InstallRuntimeAsync(
                CreateOcrProgress(),
                _ocrOperationCancellation!.Token);
            OcrDownloadStatusText.Text = "OCR 运行库安装完成；现在可以选择并安装模型。";
            OcrDownloadProgress.Value = 100;
        }
        catch (OperationCanceledException)
        {
            OcrDownloadStatusText.Text = "运行库安装已取消。";
        }
        catch (Exception exception)
        {
            OcrDownloadStatusText.Text = exception.Message;
        }
        finally
        {
            SetOcrControlsBusy(false);
            RefreshOcrStatus();
        }
    }

    private async void OnOcrInstallClick(object sender, RoutedEventArgs e)
    {
        var model = SelectedOcrModel();
        if (model == OcrModelManager.NoModel)
        {
            OcrDownloadStatusText.Text = "请先选择 Tiny 或 Small 模型。";
            return;
        }

        BeginOcrOperation();
        try
        {
            if (!_ocrService.IsRuntimeInstalled)
            {
                await _ocrService.InstallRuntimeAsync(
                    CreateOcrProgress(),
                    _ocrOperationCancellation!.Token);
            }

            await _ocrService.InstallModelAsync(
                model,
                CreateOcrProgress(),
                _ocrOperationCancellation!.Token);
            _settingsService.Save(_settingsService.Current with
            {
                OcrEngine = "Advanced",
                OcrModel = model
            });
            OcrDownloadStatusText.Text = $"{OcrModelManager.GetDisplayName(model)} 安装完成，可以立即离线识别。";
            OcrDownloadProgress.Value = 100;
        }
        catch (OperationCanceledException)
        {
            OcrDownloadStatusText.Text = "模型下载已取消。";
        }
        catch (Exception exception)
        {
            OcrDownloadStatusText.Text = exception.Message;
        }
        finally
        {
            SetOcrControlsBusy(false);
            RefreshOcrStatus();
        }
    }

    private async void OnOcrDeleteClick(object sender, RoutedEventArgs e)
    {
        BeginOcrOperation();
        try
        {
            await _ocrService.DeleteAllOcrAsync(_ocrOperationCancellation!.Token);
            _settingsService.Save(_settingsService.Current with
            {
                OcrEngine = "None",
                OcrModel = OcrModelManager.NoModel
            });
            SelectComboByTag(OcrModelCombo, OcrModelManager.NoModel);
            OcrDownloadProgress.Visibility = Visibility.Collapsed;
            OcrDownloadStatusText.Text = "OCR 运行库和全部模型已卸载，空间已经释放。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            OcrDownloadStatusText.Text = exception.Message;
        }
        finally
        {
            SetOcrControlsBusy(false);
            RefreshOcrStatus();
        }
    }

    private void RefreshOcrStatus()
    {
        var model = SelectedOcrModel();
        var modelSelected = model != OcrModelManager.NoModel;
        var runtimeInstalled = _ocrService.IsRuntimeInstalled;
        var modelInstalled = modelSelected && _ocrService.IsModelInstalled(model);
        var ready = runtimeInstalled && modelInstalled;
        var instantMode = string.Equals(
            SelectedTag(OcrPerformanceCombo, "Instant"),
            "Instant",
            StringComparison.OrdinalIgnoreCase);
        OcrEngineDescriptionText.Text = !modelSelected
            ? "基础版不包含 OCR。需要时选择 Tiny 或 Small，再依次安装运行库和模型。"
            : !runtimeInstalled
            ? "先安装通用 OCR 运行库；运行库只安装一次，Tiny 与 Small 可以自由切换。"
            : !modelInstalled
            ? $"{OcrModelManager.GetDisplayName(model)} 尚未安装，预计下载 {_ocrService.GetModelDownloadSize(model) / 1024D / 1024D:0.0} MB。"
            : instantMode
            ? "组件完整；后台预热引擎，优先保证第一次识别接近无感。"
            : "组件完整；闲置 5 分钟释放引擎，降低常驻内存。";
        OcrModelStatusText.Text = ready
            ? $"{OcrModelManager.GetDisplayName(model)} · 已就绪"
            : !runtimeInstalled
            ? "OCR 组件 · 未安装"
            : !modelSelected
            ? "OCR 运行库 · 已安装，尚未选择模型"
            : $"{OcrModelManager.GetDisplayName(model)} · 未安装";
        OcrStatusRail.Background = new System.Windows.Media.SolidColorBrush(ready
            ? System.Windows.Media.Color.FromRgb(118, 223, 238)
            : System.Windows.Media.Color.FromRgb(255, 178, 92));
        SetOcrStageState(OcrRuntimeStageText, runtimeInstalled);
        SetOcrStageState(OcrModelStageText, modelInstalled);
        SetOcrStageState(OcrReadyStageText, ready);
        OcrRuntimeButton.Content = runtimeInstalled ? "重新安装运行库" : "安装运行库";
        OcrInstallButton.Content = modelInstalled
            ? "校验模型"
            : runtimeInstalled
            ? "安装模型"
            : "一键安装 OCR";
        OcrRuntimeButton.IsEnabled = true;
        OcrInstallButton.IsEnabled = modelSelected;
        OcrDeleteButton.IsEnabled = runtimeInstalled || _ocrService.GetInstalledModels().Count > 0;
        OcrPerformanceCombo.IsEnabled = modelSelected;
        if (string.IsNullOrWhiteSpace(OcrDownloadStatusText.Text))
        {
            OcrDownloadStatusText.Text = ready
                ? $"模型保存在 {_ocrService.GetModelDirectory(model)}"
                : _ocrService.FindLocalRuntimePackage() is not null
                ? "已检测到本地 OCR 模块包，可以直接安装。"
                : "OCR 运行库和模型均按需安装，不占用基础包空间。";
        }
    }

    private void SetOcrControlsBusy(bool busy)
    {
        OcrRuntimeButton.IsEnabled = !busy;
        OcrInstallButton.IsEnabled = !busy && SelectedOcrModel() != OcrModelManager.NoModel;
        OcrDeleteButton.IsEnabled = !busy && (_ocrService.IsRuntimeInstalled || _ocrService.GetInstalledModels().Count > 0);
        OcrModelCombo.IsEnabled = !busy;
        OcrPerformanceCombo.IsEnabled = !busy && SelectedOcrModel() != OcrModelManager.NoModel;
    }

    private string SelectedOcrModel() =>
        OcrModelManager.NormalizeModel(SelectedTag(OcrModelCombo, OcrModelManager.NoModel));

    private void BeginOcrOperation()
    {
        _ocrOperationCancellation?.Cancel();
        _ocrOperationCancellation?.Dispose();
        _ocrOperationCancellation = new CancellationTokenSource();
        SetOcrControlsBusy(true);
        OcrDownloadProgress.Visibility = Visibility.Visible;
        OcrDownloadProgress.Value = 0;
    }

    private IProgress<OcrProgress> CreateOcrProgress() => new Progress<OcrProgress>(value =>
    {
        OcrDownloadStatusText.Text = value.Message;
        if (value.Percent is double percent)
        {
            OcrDownloadProgress.Value = percent * 100;
        }
    });

    private static void SetOcrStageState(TextBlock text, bool completed)
    {
        text.Text = completed ? "● " + text.Text.TrimStart('○', '●', ' ') : "○ " + text.Text.TrimStart('○', '●', ' ');
        text.Foreground = new System.Windows.Media.SolidColorBrush(completed
            ? System.Windows.Media.Color.FromRgb(118, 223, 238)
            : System.Windows.Media.Color.FromRgb(113, 132, 143));
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseWindowClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeIcon is null)
        {
            return;
        }

        MaximizeIcon.Kind = WindowState == WindowState.Maximized
            ? QingSnapIconKind.Restore
            : QingSnapIconKind.Maximize;
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private static int ParseInt(WpfTextBox box, string name) =>
        int.TryParse(box.Text, out var value) ? value : throw new InvalidOperationException($"{name}必须是整数。");

    private static double ParseDouble(WpfTextBox box, string name) =>
        double.TryParse(box.Text, out var value) ? value : throw new InvalidOperationException($"{name}必须是数字。");

    private static string SelectedContent(WpfComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private static string SelectedTag(WpfComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectCombo(WpfComboBox combo, string value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = combo.SelectedIndex < 0 ? 0 : combo.SelectedIndex;
    }

    private static void SelectComboByTag(WpfComboBox combo, string value)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == value);
        combo.SelectedIndex = combo.SelectedIndex < 0 ? 0 : combo.SelectedIndex;
    }
}
