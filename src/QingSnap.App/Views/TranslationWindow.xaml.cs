using System.Windows;
using System.Windows.Input;
using QingSnap.App.Models;
using QingSnap.App.Services;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QingSnap.App.Views;

public partial class TranslationWindow : Window
{
    private readonly TranslationCoordinator _translation;
    private CancellationTokenSource? _request;
    private int _requestVersion;
    private bool _closed;
    private bool _ready;

    public TranslationWindow(TranslationCoordinator translation, string? source = null)
    {
        _translation = translation;
        InitializeComponent();
        TrialModeText.Text = TranslationTrialEnvironment.GlobalHotkeysEnabled
            ? "独立试验版 · 本次允许全局快捷键" : "独立试验版 · 未启用全局快捷键";
        TargetCombo.ItemsSource = TranslationLanguages.All;
        TargetCombo.SelectedValue = translation.Settings.Current.TranslationTargetLanguage;
        SourceBox.Text = source ?? string.Empty;
        _ready = true;
        Loaded += async (_, _) =>
        {
            SourceBox.Focus();
            if (!string.IsNullOrWhiteSpace(source) && translation.Settings.Current is
                { TranslationEnabled: true, TranslationCloudConsent: true }) await TranslateAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            CancelRequest();
            SourceBox.Clear();
            ResultBox.Clear();
        };
    }

    internal async Task TranslateAsync()
    {
        CancelRequest();
        var version = ++_requestVersion;
        var cancellation = new CancellationTokenSource();
        _request = cancellation;
        TranslateButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        CopyButton.IsEnabled = false;
        ResultBox.Clear();
        SetStatus("正在翻译… 较长文字会分段处理，可随时取消。", false);
        try
        {
            var credentials = _translation.Credentials.Load() ?? new TranslationCredentials("", "");
            var settings = _translation.Settings.Current;
            var result = await _translation.Client.TranslateAsync(SourceBox.Text,
                TargetCombo.SelectedValue as string ?? "zh", credentials,
                settings.TranslationEnabled, settings.TranslationCloudConsent, cancellation.Token);
            if (_closed || version != _requestVersion || cancellation.IsCancellationRequested) return;
            ResultBox.Text = result;
            CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(result);
            SetStatus("翻译完成。译文仅保留在当前窗口中。", false);
        }
        catch (OperationCanceledException)
        {
            if (!_closed && version == _requestVersion) SetStatus("请求已取消。已发送的内容仍可能由百度计入额度。", false);
        }
        catch (Exception exception)
        {
            // Only show controlled messages; never log/source-format a request exception.
            DiagnosticLog.Warning("Translation", $"Translation window operation failed ({exception.GetType().Name}).");
            if (!_closed && version == _requestVersion)
                SetStatus(exception is TranslationException ? exception.Message : "翻译未完成，请检查设置后重试。", true);
        }
        finally
        {
            if (ReferenceEquals(_request, cancellation)) _request = null;
            cancellation.Dispose();
            if (!_closed && version == _requestVersion)
            {
                TranslateButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
            }
        }
    }

    private void CancelRequest()
    {
        _request?.Cancel();
        _request = null; // The awaiting operation owns disposal.
    }

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || _closed) return;
        CancelRequest();
        _requestVersion++;
        TranslateButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        ResultBox.Clear();
        SetStatus("内容已调整；点击“翻译”后才会发送。", false);
    }

    private async void OnTranslateClick(object sender, RoutedEventArgs e) => await TranslateAsync();
    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequest();
    private void OnSettingsClick(object sender, RoutedEventArgs e) => _translation.OpenSettings(this);

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!CopyButton.IsEnabled || string.IsNullOrWhiteSpace(ResultBox.Text)) return;
        try
        {
            await _translation.Clipboard.CopyTextAsync(ResultBox.Text);
            if (!_closed) SetStatus("译文已复制。", false);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Translation", $"Copy translation failed ({exception.GetType().Name}).");
            if (!_closed) SetStatus("剪贴板暂不可用，译文仍保留，请重试。", true);
        }
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            error ? "ThemeDangerBrush" : "ThemeMutedTextBrush");
    }

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        { if (TranslateButton.IsEnabled) _ = TranslateAsync(); e.Handled = true; }
        else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        { OnCopyClick(sender, e); e.Handled = true; }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    { if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button) DragMove(); }
}
