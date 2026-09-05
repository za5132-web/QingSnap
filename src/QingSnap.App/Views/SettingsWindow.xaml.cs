using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private readonly UpdateService _updateService;
    private readonly Action _openTutorial;
    private readonly Func<IReadOnlyList<HotkeyRegistrationFailure>> _getHotkeyRegistrationFailures;
    private readonly Action<bool> _setHotkeyCaptureMode;
    private bool _isCapturingHotkey;
    private CancellationTokenSource? _ocrOperationCancellation;
    private CancellationTokenSource? _updateOperationCancellation;
    private UpdateReleaseInfo? _availableRelease;
    private string? _downloadedUpdatePath;

    public SettingsWindow(
        AppSettingsService settingsService,
        OcrService ocrService,
        UpdateService updateService,
        Action openTutorial,
        Func<IReadOnlyList<HotkeyRegistrationFailure>> getHotkeyRegistrationFailures,
        Action<bool> setHotkeyCaptureMode)
    {
        _settingsService = settingsService;
        _ocrService = ocrService;
        _updateService = updateService;
        _openTutorial = openTutorial;
        _getHotkeyRegistrationFailures = getHotkeyRegistrationFailures;
        _setHotkeyCaptureMode = setHotkeyCaptureMode;
        var bindings = settingsService.Current.Hotkeys.ToDictionary(binding => binding.Action);
        HotkeyRows = new ObservableCollection<HotkeySettingRow>(
            HotkeyCatalog.Definitions.Select(definition =>
            {
                var binding = bindings[definition.Action];
                return new HotkeySettingRow(
                    definition.Action,
                    definition.DisplayName,
                    definition.Description,
                    binding.Gesture,
                    binding.IsEnabled);
            }));
        DataContext = this;
        InitializeComponent();
        LoadSettings(settingsService.Current);
        RefreshOcrStatus();
        CurrentVersionText.Text = _updateService.CurrentVersionDisplay;
        if (_updateService.LastRelease is { } release)
        {
            ApplyUpdateRelease(release);
        }
        ShowHotkeyRegistrationFailures(_getHotkeyRegistrationFailures());
        Deactivated += (_, _) => EndHotkeyCapture();
        Closed += (_, _) =>
        {
            _ocrOperationCancellation?.Cancel();
            _ocrOperationCancellation?.Dispose();
            _ocrOperationCancellation = null;
            _updateOperationCancellation?.Cancel();
            _updateOperationCancellation?.Dispose();
            _updateOperationCancellation = null;
            EndHotkeyCapture();
            ResourceDiagnostics.Sample("SettingsClosed");
        };
        Loaded += (_, _) => ResourceDiagnostics.Sample("SettingsOpened");
    }

    public ObservableCollection<HotkeySettingRow> HotkeyRows { get; }

    private void LoadSettings(AppSettings settings)
    {
        StartupCheck.IsChecked = settings.StartWithWindows;
        AutoUpdateCheck.IsChecked = settings.AutoCheckUpdates;
        HistoryDirectoryBox.Text = settings.HistoryDirectory;
        RetentionBox.Text = settings.HistoryRetentionDays.ToString();
        SelectCombo(FormatCombo, settings.OutputFormat);
        JpegQualityBox.Text = settings.JpegQuality.ToString();
        AutoCopyCheck.IsChecked = settings.AutoCopy;
        SelectComboByTag(DelayCombo, settings.CaptureDelaySeconds.ToString());
        SmartSelectionCheck.IsChecked = settings.SmartWindowSelection;
        MagnifierCheck.IsChecked = settings.ShowMagnifier;
        QuickCaptureTagsCheck.IsChecked = settings.ShowQuickCaptureTags;
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
            var hotkeys = BuildValidatedHotkeys();

            var settings = _settingsService.Current with
            {
                Hotkeys = hotkeys,
                StartWithWindows = StartupCheck.IsChecked == true,
                AutoCheckUpdates = AutoUpdateCheck.IsChecked == true,
                HistoryDirectory = HistoryDirectoryBox.Text,
                HistoryRetentionDays = ParseInt(RetentionBox, "自动清理天数"),
                OutputFormat = SelectedContent(FormatCombo, "PNG"),
                JpegQuality = ParseInt(JpegQualityBox, "JPG 质量"),
                AutoCopy = AutoCopyCheck.IsChecked == true,
                CaptureDelaySeconds = int.Parse(SelectedTag(DelayCombo, "0")),
                SmartWindowSelection = SmartSelectionCheck.IsChecked == true,
                ShowMagnifier = MagnifierCheck.IsChecked == true,
                ShowQuickCaptureTags = QuickCaptureTagsCheck.IsChecked == true,
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
            var failures = _getHotkeyRegistrationFailures();
            ShowHotkeyRegistrationFailures(failures);
            if (failures.Count == 0)
            {
                StatusText.Text = "设置已保存并应用";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(118, 223, 238));
            }
            else
            {
                StatusText.Text = $"设置已保存；有 {failures.Count} 个快捷键未能注册";
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 145, 137));
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 145, 137));
        }
    }

    private List<HotkeyBinding> BuildValidatedHotkeys()
    {
        var result = new List<HotkeyBinding>(HotkeyRows.Count);
        var usedGestures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in HotkeyRows)
        {
            var rawGesture = row.Gesture.Trim();
            if (!row.IsEnabled)
            {
                result.Add(new HotkeyBinding
                {
                    Action = row.Action,
                    Gesture = GlobalHotkeyService.TryNormalizeGesture(rawGesture, out var disabledGesture)
                        ? disabledGesture
                        : string.Empty,
                    IsEnabled = false
                });
                continue;
            }

            if (!GlobalHotkeyService.TryNormalizeGesture(rawGesture, out var normalized))
            {
                throw new InvalidOperationException(
                    $"“{row.DisplayName}”的快捷键格式无效，请使用 F1–F12 与 Ctrl / Shift / Alt 组合。");
            }

            if (usedGestures.TryGetValue(normalized, out var existingAction))
            {
                throw new InvalidOperationException(
                    $"“{row.DisplayName}”和“{existingAction}”不能同时使用 {normalized}。");
            }

            usedGestures[normalized] = row.DisplayName;
            row.Gesture = normalized;
            result.Add(new HotkeyBinding
            {
                Action = row.Action,
                Gesture = normalized,
                IsEnabled = true
            });
        }

        return result;
    }

    private void OnHotkeyBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            _isCapturingHotkey = true;
            _setHotkeyCaptureMode(true);
        }

        StatusText.Text = "请直接按下组合键；Backspace 或 Delete 可清空";
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(118, 223, 238));
    }

    private void OnHotkeyBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (Keyboard.FocusedElement is WpfTextBox { Tag: HotkeySettingRow })
            {
                return;
            }

            EndHotkeyCapture();
        });
    }

    private void OnHotkeyBoxPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (sender is not WpfTextBox { Tag: HotkeySettingRow row })
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)
        {
            e.Handled = true;
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            row.Gesture = string.Empty;
            row.IsEnabled = false;
            StatusText.Text = $"已清空“{row.DisplayName}”快捷键";
            e.Handled = true;
            return;
        }

        if (key is >= Key.F1 and <= Key.F12)
        {
            var parts = new List<string>(4);
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Control))
            {
                parts.Add("Ctrl");
            }

            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                parts.Add("Shift");
            }

            if (modifiers.HasFlag(ModifierKeys.Alt))
            {
                parts.Add("Alt");
            }

            parts.Add(key.ToString());
            row.Gesture = string.Join('+', parts);
            row.IsEnabled = true;
            StatusText.Text = $"“{row.DisplayName}”已录入 {row.Gesture}";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(118, 223, 238));
            e.Handled = true;
            return;
        }

        StatusText.Text = "这里只接受 F1–F12 与 Ctrl / Shift / Alt 组合";
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(255, 145, 137));
        e.Handled = true;
    }

    private void OnHotkeyBoxPreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = true;

    private void EndHotkeyCapture()
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        _isCapturingHotkey = false;
        _setHotkeyCaptureMode(false);
    }

    private void ShowHotkeyRegistrationFailures(IReadOnlyList<HotkeyRegistrationFailure> failures)
    {
        if (failures.Count == 0)
        {
            HotkeyRegistrationStatusText.Visibility = Visibility.Collapsed;
            HotkeyRegistrationStatusText.Text = string.Empty;
            return;
        }

        HotkeyRegistrationStatusText.Text = "以下快捷键未启用：" + Environment.NewLine +
                                            string.Join(Environment.NewLine, failures.Select(failure => $"• {failure.DisplayText}"));
        HotkeyRegistrationStatusText.Visibility = Visibility.Visible;
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

    private void OnReplayTutorialClick(object sender, RoutedEventArgs e) => _openTutorial();

    private void OnOpenFeedbackClick(object sender, RoutedEventArgs e)
    {
        var window = new FeedbackWindow(_settingsService)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        BeginUpdateOperation();
        UpdateStatusText.Text = "正在连接 GitHub 检查最新版本…";
        UpdateStatusRail.Background = BrushFromRgb(118, 161, 177);
        UpdateNotesText.Text = string.Empty;
        UpdateDownloadButton.Visibility = Visibility.Collapsed;
        UpdateOpenFolderButton.Visibility = Visibility.Collapsed;
        try
        {
            var result = await _updateService.CheckForUpdatesAsync(
                force: true,
                _updateOperationCancellation!.Token);
            if (result.Release is { } release)
            {
                ApplyUpdateRelease(release);
            }

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    UpdateStatusText.Text = result.Release?.CanDownload == true
                        ? $"发现新版本 {result.Release.TagName}"
                        : $"发现新版本 {result.Release?.TagName}，但发布页没有可用的 SHA256";
                    UpdateStatusRail.Background = BrushFromRgb(
                        result.Release?.CanDownload == true ? (byte)118 : (byte)255,
                        result.Release?.CanDownload == true ? (byte)223 : (byte)178,
                        result.Release?.CanDownload == true ? (byte)238 : (byte)92);
                    break;
                case UpdateCheckStatus.UpToDate:
                    UpdateStatusText.Text = "当前已经是最新版本";
                    UpdateStatusRail.Background = BrushFromRgb(118, 223, 238);
                    break;
                case UpdateCheckStatus.NoCompatiblePackage:
                    UpdateStatusText.Text = result.Message ?? "最新发布没有可用的便携 ZIP。";
                    UpdateStatusRail.Background = BrushFromRgb(255, 178, 92);
                    break;
                case UpdateCheckStatus.Error:
                    UpdateStatusText.Text = result.Message ?? "检查更新失败，请稍后重试。";
                    UpdateStatusRail.Background = BrushFromRgb(255, 145, 137);
                    break;
                case UpdateCheckStatus.Skipped:
                    UpdateStatusText.Text = "最近已经检查过更新。";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText.Text = "更新检查已取消。";
        }
        finally
        {
            SetUpdateControlsBusy(false);
        }
    }

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_availableRelease is not { CanDownload: true } release)
        {
            UpdateDownloadStatusText.Text = "此版本缺少可信的 SHA256，不能下载。";
            return;
        }

        BeginUpdateOperation();
        UpdateDownloadProgress.Visibility = Visibility.Visible;
        UpdateDownloadProgress.Value = 0;
        UpdateDownloadStatusText.Text = "正在准备下载…";
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                UpdateDownloadProgress.Value = value.Percentage;
                UpdateDownloadStatusText.Text = value.TotalBytes > 0
                    ? $"正在下载 {FormatFileSize(value.BytesReceived)} / {FormatFileSize(value.TotalBytes)}"
                    : $"正在下载 {FormatFileSize(value.BytesReceived)}";
            });
            var downloaded = await _updateService.DownloadAsync(
                release,
                progress,
                _updateOperationCancellation!.Token);
            _downloadedUpdatePath = downloaded.FilePath;
            UpdateDownloadProgress.Value = 100;
            UpdateDownloadStatusText.Text = "下载完成，SHA256 校验通过。请解压后手动更新。";
            UpdateOpenFolderButton.Visibility = Visibility.Visible;
            UpdateStatusRail.Background = BrushFromRgb(118, 223, 238);
        }
        catch (OperationCanceledException)
        {
            UpdateDownloadStatusText.Text = "下载已取消。";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Update", exception, "更新包下载或校验失败。");
            UpdateDownloadStatusText.Text = exception.Message;
            UpdateStatusRail.Background = BrushFromRgb(255, 145, 137);
        }
        finally
        {
            SetUpdateControlsBusy(false);
        }
    }

    private void OnOpenUpdateFolderClick(object sender, RoutedEventArgs e)
    {
        var path = _downloadedUpdatePath ?? _updateService.LastDownloadedPackagePath;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new FileNotFoundException("还没有下载完成的更新包。");
            }

            UpdateService.OpenContainingFolder(path);
        }
        catch (Exception exception)
        {
            UpdateDownloadStatusText.Text = exception.Message;
        }
    }

    private void ApplyUpdateRelease(UpdateReleaseInfo release)
    {
        LatestVersionText.Text = release.TagName;
        UpdatePublishedText.Text = release.PublishedAt == DateTimeOffset.MinValue
            ? "—"
            : release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd");
        UpdateFileSizeText.Text = release.PackageSize > 0
            ? FormatFileSize(release.PackageSize)
            : "未知";
        UpdateNotesText.Text = string.IsNullOrWhiteSpace(release.ReleaseNotes)
            ? "该版本没有填写更新说明。"
            : release.ReleaseNotes.Trim();
        var hasUpdate = release.Version > _updateService.CurrentVersion;
        _availableRelease = hasUpdate ? release : null;
        UpdateDownloadButton.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
        UpdateDownloadButton.IsEnabled = hasUpdate && release.CanDownload;
        UpdateDownloadStatusText.Text = hasUpdate && !release.CanDownload
            ? "发布页缺少 SHA256，为安全起见已禁用下载。"
            : release.ExpectedSha256 is { } sha256
                ? $"SHA256  {sha256}"
                : string.Empty;

        var downloadedPath = _updateService.LastDownloadedPackagePath;
        if (!string.IsNullOrWhiteSpace(downloadedPath) && File.Exists(downloadedPath))
        {
            _downloadedUpdatePath = downloadedPath;
            UpdateOpenFolderButton.Visibility = Visibility.Visible;
        }
    }

    private void BeginUpdateOperation()
    {
        _updateOperationCancellation?.Cancel();
        _updateOperationCancellation?.Dispose();
        _updateOperationCancellation = new CancellationTokenSource();
        SetUpdateControlsBusy(true);
    }

    private void SetUpdateControlsBusy(bool busy)
    {
        UpdateCheckButton.IsEnabled = !busy;
        UpdateDownloadButton.IsEnabled = !busy && _availableRelease?.CanDownload == true;
        AutoUpdateCheck.IsEnabled = !busy;
    }

    private static System.Windows.Media.SolidColorBrush BrushFromRgb(byte red, byte green, byte blue) =>
        new(System.Windows.Media.Color.FromRgb(red, green, blue));

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / (1024D * 1024D * 1024D):0.00} GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024D * 1024D):0.0} MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024D:0.0} KB";
        }

        return $"{bytes:N0} B";
    }

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

public sealed class HotkeySettingRow(
    HotkeyAction action,
    string displayName,
    string description,
    string gesture,
    bool isEnabled) : INotifyPropertyChanged
{
    private string _gesture = gesture;
    private bool _isEnabled = isEnabled;

    public HotkeyAction Action { get; } = action;

    public string DisplayName { get; } = displayName;

    public string Description { get; } = description;

    public string Gesture
    {
        get => _gesture;
        set => SetField(ref _gesture, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
