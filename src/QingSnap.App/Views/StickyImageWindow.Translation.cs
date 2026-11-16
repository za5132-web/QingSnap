using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;

namespace QingSnap.App.Views;

public partial class StickyImageWindow
{
    private PinTranslationDocument? _pinTranslationDocument;
    private OcrRecognitionResult? _pinTranslationOriginalOcr;
    private CancellationTokenSource? _pinTranslationCancellation;
    private int _pinTranslationVersion;
    private bool _pinTranslationVisible;
    private bool _pinTranslationBusy;
    private string? _pinTranslationScope;
    private readonly PinTranslationCache _pinTranslationCache = new();
    private string _pinTranslationTargetLanguage = "zh";
    private bool _pinTranslationHasLocalTarget;

    private void InitializePinTranslation()
    {
        if (_translation is not null)
        {
            _pinTranslationTargetLanguage = TranslationLanguages.Normalize(_translation.Settings.Current.TranslationTargetLanguage);
            _translation.Settings.SettingsChanged += OnPinTranslationSettingsChanged;
            foreach (var language in TranslationLanguages.All)
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = language.Name, Tag = language.Code, IsCheckable = true, StaysOpenOnClick = true,
                    Style = (Style)FindResource("PinMenuItem")
                };
                item.Click += OnPinTranslationLanguageClick;
                TranslateTargetMenuItem.Items.Add(item);
            }
        }
        UpdatePinTranslationMenus();
    }

    private void OnPinTranslationSettingsChanged(object? sender, EventArgs e)
    {
        _pinTranslationCancellation?.Cancel();
        if (!_pinTranslationHasLocalTarget && _translation is not null)
            SelectPinTranslationTarget(_translation.Settings.Current.TranslationTargetLanguage, localChoice: false);
        if (_translation?.Settings.Current is { TranslationEnabled: false } or { TranslationCloudConsent: false })
            SetPinTranslationVisible(false);
        UpdatePinTranslationMenus();
    }

    private void DisposePinTranslation()
    {
        _pinTranslationVersion++;
        _pinTranslationCancellation?.Cancel();
        if (_translation is not null) _translation.Settings.SettingsChanged -= OnPinTranslationSettingsChanged;
        _pinTranslationDocument = null;
        _pinTranslationCache.Clear();
        _pinTranslationOriginalOcr = null;
        PinTranslationOverlay.Source = null;
        LongPinTranslationOverlay.Source = null;
    }

    private void InvalidatePinTranslation()
    {
        if (_pinTranslationBusy)
        {
            _feedbackTimer.Stop();
            FeedbackBadge.Visibility = Visibility.Collapsed;
        }
        _pinTranslationVersion++;
        _pinTranslationCancellation?.Cancel();
        _pinTranslationBusy = false;
        _pinTranslationVisible = false;
        _pinTranslationDocument = null;
        _pinTranslationCache.Clear();
        _pinTranslationOriginalOcr = null;
        _pinTranslationScope = null;
        PinTranslationOverlay.Source = null;
        LongPinTranslationOverlay.Source = null;
        PinTranslationOverlay.Visibility = Visibility.Collapsed;
        LongPinTranslationOverlay.Visibility = Visibility.Collapsed;
        UpdatePinTranslationMenus();
    }

    private void UpdatePinTranslationMenus()
    {
        var visible = _translation is null ? Visibility.Collapsed : Visibility.Visible;
        TranslatePinMenuItem.Visibility = TranslationSettingsMenuItem.Visibility = TranslateTargetMenuItem.Visibility = visible;
        TranslatePinMenuItem.Header = _pinTranslationBusy ? "取消翻译"
            : _pinTranslationVisible ? "查看原图" : _pinTranslationDocument is not null ? "显示译图"
                : "翻译为" + TranslationLanguages.GetName(_pinTranslationTargetLanguage);
        TranslatePinMenuItem.IsEnabled = !_isEditingPin && !_isCollapsed;
        TranslateTargetMenuItem.Header = "目标语言：" + TranslationLanguages.GetName(_pinTranslationTargetLanguage);
        TranslateTargetMenuItem.IsEnabled = !_isEditingPin && !_isCollapsed;
        foreach (var item in TranslateTargetMenuItem.Items.OfType<System.Windows.Controls.MenuItem>())
            item.IsChecked = string.Equals(item.Tag as string, _pinTranslationTargetLanguage, StringComparison.Ordinal);
        CopyOriginalImageMenuItem.Header = _pinTranslationVisible ? "复制原图" : "复制图片";
        SaveOriginalImageMenuItem.Header = _pinTranslationVisible ? "保存原图" : "另存图片";
        TranslateSelectedOcrMenuItem.IsEnabled = !_pinTranslationBusy;
        CopyTranslatedImageMenuItem.Visibility = SaveTranslatedImageMenuItem.Visibility = CopyTranslatedTextMenuItem.Visibility =
            _pinTranslationDocument is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool HandlePinTranslationShortcut(Key key, ModifierKeys modifiers)
    {
        if (key != Key.T || modifiers != ModifierKeys.Control || _isEditingPin || _translation is null) return false;
        TogglePinTranslation();
        return true;
    }

    private void TogglePinTranslation()
    {
        if (_pinTranslationBusy) { _pinTranslationCancellation?.Cancel(); return; }
        if (_pinTranslationDocument is not null) SetPinTranslationVisible(!_pinTranslationVisible);
        else _ = TranslatePinAsync(selectedOnly: false);
    }

    private void OnPinTranslationLanguageClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: string code }) SelectPinTranslationTarget(code);
        e.Handled = true;
    }

    private void SelectPinTranslationTarget(string code, bool localChoice = true)
    {
        var target = TranslationLanguages.Normalize(code);
        if (localChoice) _pinTranslationHasLocalTarget = true;
        if (target == _pinTranslationTargetLanguage) { UpdatePinTranslationMenus(); return; }
        _pinTranslationVersion++;
        _pinTranslationCancellation?.Cancel();
        _pinTranslationBusy = false;
        SetPinTranslationVisible(false);
        _pinTranslationTargetLanguage = target;
        _pinTranslationDocument = null;
        _pinTranslationScope = null;
        PinTranslationOverlay.Source = LongPinTranslationOverlay.Source = null;
        UpdatePinTranslationMenus();
        ShowFeedback("已选择" + TranslationLanguages.GetName(target) + " · 按 Ctrl+T 开始翻译");
    }

    internal async Task TranslatePinAsync(bool selectedOnly)
    {
        if (_translation is null || _isPinClosed || _isEditingPin || _isCollapsed || _pinTranslationBusy) return;
        var selected = selectedOnly ? _selectedWordIndices.ToHashSet() : null;
        if (selectedOnly && (selected!.Count == 0 || _pinTranslationVisible)) return;
        var scope = selected is null ? "all" : string.Join(',', selected.Order());
        var target = _pinTranslationTargetLanguage;
        var original = _pinTranslationVisible ? _pinTranslationOriginalOcr : _ocrResult;
        if (original is not null && _pinTranslationCache.TryGet(target, scope, original, out var cached))
        {
            _pinTranslationDocument = cached;
            _pinTranslationOriginalOcr = original;
            _pinTranslationScope = scope;
            SetPinTranslationVisible(true);
            return;
        }
        var version = ++_pinTranslationVersion;
        var cancellation = new CancellationTokenSource();
        _pinTranslationCancellation = cancellation;
        _pinTranslationBusy = true;
        UpdatePinTranslationMenus();
        try
        {
            var settings = _translation.Settings.Current;
            if (!settings.TranslationEnabled || !settings.TranslationCloudConsent)
                throw new TranslationException("settings", "请右键打开“翻译设置”，启用并确认文字发送提示。");
            var credentials = _translation.Credentials.Load();
            if (credentials?.IsValid != true)
                throw new TranslationException("credentials", "请先在“翻译设置”填写自己的 APPID 与密钥。");
            if (original is null)
            {
                if (_ocrPreloadTask is null && !_ocrService.IsOcrAvailable)
                    throw new TranslationException("ocr", "需要离线识字组件；请在试验版设置中安装 OCR 后再翻译。");
                ShowPinTranslationStatus("正在本地识字… Ctrl+T 取消");
                original = await (_ocrPreloadTask ?? _ocrService.RecognizeAsync(CreatePinOutput(),
                    cancellation.Token, includeWordBoxes: true)).WaitAsync(cancellation.Token);
            }
            var blocks = await Task.Run(() => PinTranslationLayout.Plan(original, selected, cancellation.Token, target), cancellation.Token);
            if (blocks.Count == 0) throw new TranslationException("empty", "没有找到需要翻译的文字；原图已保留。");
            ShowPinTranslationStatus("正在翻译为" + TranslationLanguages.GetName(target) + "… Ctrl+T 取消");
            var progress = new Progress<int>(value =>
            {
                if (!_isPinClosed && version == _pinTranslationVersion && _pinTranslationBusy)
                    ShowPinTranslationStatus($"正在翻译 {value}% · Ctrl+T 取消");
            });
            var translated = await _translation.Client.TranslateSegmentsAsync(
                blocks.Select(block => new TranslationSegment(block.Id, block.Text)).ToArray(), credentials,
                settings.TranslationEnabled, settings.TranslationCloudConsent, progress, cancellation.Token, target);
            var document = await Task.Run(() => PinTranslationLayout.Render(_image, original, blocks, translated,
                cancellation.Token, target), cancellation.Token);
            if (_isPinClosed || version != _pinTranslationVersion || cancellation.IsCancellationRequested) return;
            _pinTranslationDocument = document;
            _pinTranslationOriginalOcr = original;
            _pinTranslationScope = scope;
            _pinTranslationCache.Store(scope, original, document);
            SetPinTranslationVisible(true);
        }
        catch (OperationCanceledException)
        {
            if (!_isPinClosed && version == _pinTranslationVersion) ShowFeedback("翻译已取消 · 原有内容保留");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Translation", $"Pin translation failed ({exception.GetType().Name}); content omitted.");
            if (!_isPinClosed && version == _pinTranslationVersion)
                ShowFeedback(exception is TranslationException ? exception.Message : "原位翻译未完成，原图已保留，请重试。");
        }
        finally
        {
            if (ReferenceEquals(_pinTranslationCancellation, cancellation)) _pinTranslationCancellation = null;
            cancellation.Dispose();
            if (!_isPinClosed && version == _pinTranslationVersion)
            {
                _pinTranslationBusy = false;
                UpdatePinTranslationMenus();
            }
        }
    }

    private void ShowPinTranslationStatus(string message)
    {
        FeedbackText.Text = message;
        FeedbackBadge.Visibility = Visibility.Visible;
        _feedbackTimer.Stop();
    }

    private void SetPinTranslationVisible(bool visible)
    {
        if (_isPinClosed || (visible && (_isEditingPin || _pinTranslationDocument is null))) return;
        if (!visible && !_pinTranslationVisible) return;
        _pinTranslationVisible = visible;
        PinTranslationOverlay.Source = LongPinTranslationOverlay.Source = _pinTranslationDocument?.Overlay;
        PinTranslationOverlay.Visibility = LongPinTranslationOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _selectedWordIndices.Clear();
        _selectionAnchorIndex = -1;
        var result = visible ? _pinTranslationDocument?.SelectionResult : _pinTranslationOriginalOcr;
        if (result is not null) ApplyOcrResult(result);
        CollapsedThumbnail.Source = CreatePinOutput(thumbnail: true, includeTranslation: visible);
        UpdatePinTranslationMenus();
        var language = TranslationLanguages.GetName(_pinTranslationDocument?.TargetLanguage ?? _pinTranslationTargetLanguage);
        ShowFeedback(visible
            ? _pinTranslationDocument!.Patches.Count == 0 ? "译文与原文一致 · 未覆盖原图 · Ctrl+T 切换"
                : _pinTranslationDocument.Patches.Any(patch => patch.IsTruncated) ? "已显示" + language + " · 部分文字过长，可右键复制完整译文"
                : _pinTranslationDocument.ApproximateBackground ? "已显示" + language + " · 复杂底色为近似覆盖 · Ctrl+T 查看原图"
                : "已显示" + language + " · Ctrl+T 查看原图"
            : "已显示原图 · Ctrl+T 显示译图");
    }

    private void OnTranslatePinClick(object sender, RoutedEventArgs e) => TogglePinTranslation();
    private void OnPinTranslationSettingsClick(object sender, RoutedEventArgs e) => _translation?.OpenSettings(this);

    private async void OnCopyTranslatedImageClick(object sender, RoutedEventArgs e)
    {
        if (_pinTranslationDocument is null) return;
        try
        {
            await _clipboardService.CopyImageAsync(CreatePinOutput(includeTranslation: true));
            if (!_isPinClosed) ShowFeedback("译图已复制");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Translation", $"Copy translated image failed ({exception.GetType().Name}).");
            if (!_isPinClosed) ShowFeedback("复制译图未完成，请重试");
        }
    }

    private async void OnCopyTranslatedTextClick(object sender, RoutedEventArgs e)
    {
        if (_pinTranslationDocument is null) return;
        try
        {
            await _clipboardService.CopyTextAsync(_pinTranslationDocument.Text);
            if (!_isPinClosed) ShowFeedback("完整译文已复制");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Translation", $"Copy pin translation failed ({exception.GetType().Name}).");
            if (!_isPinClosed) ShowFeedback("复制译文未完成，请重试");
        }
    }

    private async void OnSaveTranslatedImageClick(object sender, RoutedEventArgs e)
    {
        if (_pinTranslationDocument is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        { Filter = "PNG 图片|*.png", DefaultExt = ".png", FileName = $"QingSnap-译图-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var image = CreatePinOutput(includeTranslation: true);
            await Task.Run(() =>
            {
                using var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
            });
            if (!_isPinClosed) ShowFeedback("译图已另存，原截图未修改");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Translation", $"Save translated image failed ({exception.GetType().Name}).");
            if (!_isPinClosed) ShowFeedback("保存译图未完成，请重试");
        }
    }
}
