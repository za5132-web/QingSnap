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
        SelectComboByTag(OcrEngineCombo, settings.OcrEngine);
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
                OcrEngine = SelectedTag(OcrEngineCombo, "Advanced"),
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

    private async void OnOcrInstallClick(object sender, RoutedEventArgs e)
    {
        _ocrOperationCancellation?.Cancel();
        _ocrOperationCancellation?.Dispose();
        _ocrOperationCancellation = new CancellationTokenSource();
        SetOcrControlsBusy(true);
        OcrDownloadProgress.Visibility = Visibility.Visible;
        OcrDownloadProgress.Value = 0;
        try
        {
            var progress = new Progress<OcrProgress>(value =>
            {
                OcrDownloadStatusText.Text = value.Message;
                if (value.Percent is double percent)
                {
                    OcrDownloadProgress.Value = percent * 100;
                }
            });
            await _ocrService.InstallAdvancedModelsAsync(progress, _ocrOperationCancellation.Token);
            await _ocrService.WarmUpAsync(_ocrOperationCancellation.Token);
            OcrDownloadStatusText.Text = "模型下载完成，可以立即离线识别。";
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
        _ocrOperationCancellation?.Cancel();
        _ocrOperationCancellation?.Dispose();
        _ocrOperationCancellation = new CancellationTokenSource();
        SetOcrControlsBusy(true);
        try
        {
            await _ocrService.DeleteAdvancedModelsAsync(_ocrOperationCancellation.Token);
            OcrDownloadProgress.Visibility = Visibility.Collapsed;
            OcrDownloadStatusText.Text = "本地模型已删除，需要时可以重新下载。";
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
        var advancedSelected = string.Equals(
            SelectedTag(OcrEngineCombo, "Advanced"),
            "Advanced",
            StringComparison.OrdinalIgnoreCase);
        var installed = _ocrService.AreAdvancedModelsInstalled;
        OcrEngineDescriptionText.Text = advancedSelected
            ? "中文与混排文字识别更准确；模型只需下载一次，之后完全离线运行。"
            : "使用系统自带识别，无需下载模型；中文复杂页面的识别率相对较低。";
        OcrModelStatusText.Text = installed
            ? "PP-OCRv6 Small · 已就绪"
            : "PP-OCRv6 Small · 未安装";
        OcrStatusRail.Background = new System.Windows.Media.SolidColorBrush(installed
            ? System.Windows.Media.Color.FromRgb(118, 223, 238)
            : System.Windows.Media.Color.FromRgb(255, 178, 92));
        OcrInstallButton.Content = installed ? "重新校验" : "立即下载";
        OcrDeleteButton.IsEnabled = installed;
        if (string.IsNullOrWhiteSpace(OcrDownloadStatusText.Text))
        {
            OcrDownloadStatusText.Text = installed
                ? $"模型保存在 {_ocrService.AdvancedModelDirectory}"
                : $"首次识别会自动下载约 {_ocrService.AdvancedModelDownloadSize / 1024D / 1024D:0.0} MB。";
        }
    }

    private void SetOcrControlsBusy(bool busy)
    {
        OcrInstallButton.IsEnabled = !busy;
        OcrDeleteButton.IsEnabled = !busy && _ocrService.AreAdvancedModelsInstalled;
        OcrEngineCombo.IsEnabled = !busy;
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
