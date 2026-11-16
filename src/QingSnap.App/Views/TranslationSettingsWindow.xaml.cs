using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using QingSnap.App.Models;
using QingSnap.App.Services;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QingSnap.App.Views;

public partial class TranslationSettingsWindow : Window
{
    private readonly TranslationCoordinator _translation;
    private CancellationTokenSource? _test;
    private bool _closed;

    public TranslationSettingsWindow(TranslationCoordinator translation)
    {
        _translation = translation;
        InitializeComponent();
        var settings = translation.Settings.Current;
        EnabledCheck.IsChecked = settings.TranslationEnabled;
        ConsentCheck.IsChecked = settings.TranslationCloudConsent;
        TargetCombo.ItemsSource = TranslationLanguages.All;
        TargetCombo.SelectedValue = settings.TranslationTargetLanguage;
        try
        {
            if (translation.Credentials.Load() is { } credentials)
            {
                AppIdBox.Text = credentials.AppId;
                SecretBox.Password = credentials.Secret;
            }
        }
        catch (TranslationException exception) { SetStatus(exception.Message, true); }
        Closed += (_, _) =>
        {
            _closed = true;
            _test?.Cancel();
            SecretBox.Clear();
        };
    }

    private TranslationCredentials GetCredentials()
    {
        var credentials = new TranslationCredentials(AppIdBox.Text.Trim(), SecretBox.Password.Trim());
        if (!credentials.IsValid) throw new TranslationException("credentials", "请填写有效的 APPID（纯数字）和密钥。");
        return credentials;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (EnabledCheck.IsChecked == true && ConsentCheck.IsChecked != true)
                throw new TranslationException("consent", "启用前，请先阅读并勾选云端处理与服务规则提示。");
            if (EnabledCheck.IsChecked == true || AppIdBox.Text.Length > 0 || SecretBox.Password.Length > 0)
                _translation.Credentials.Save(GetCredentials());
            _translation.Settings.Save(_translation.Settings.Current with
            {
                TranslationEnabled = EnabledCheck.IsChecked == true,
                TranslationCloudConsent = ConsentCheck.IsChecked == true,
                TranslationTargetLanguage = TargetCombo.SelectedValue as string ?? "zh"
            });
            SetStatus("翻译设置已保存；没有发送翻译请求。", false);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Translation", $"Saving translation settings failed ({exception.GetType().Name}).");
            SetStatus(exception is TranslationException ? exception.Message : "保存未完成，请检查目录写入权限后重试。", true);
        }
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_test is not null) return;
        var cancellation = new CancellationTokenSource();
        _test = cancellation;
        TestButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        SetStatus("正在发送测试文字 Hello…", false);
        try
        {
            _ = await _translation.Client.TranslateAsync("Hello", TargetCombo.SelectedValue as string ?? "zh",
                GetCredentials(), enabled: true, cloudConsent: ConsentCheck.IsChecked == true, cancellation.Token);
            if (!_closed) SetStatus("连接成功。点击“保存翻译设置”后才会保存当前账号。", false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Translation", $"Connection test failed ({exception.GetType().Name}).");
            if (!_closed) SetStatus(exception is TranslationException ? exception.Message : "连接未完成，请检查网络与账号。", true);
        }
        finally
        {
            _test = null;
            cancellation.Dispose();
            if (!_closed) { TestButton.IsEnabled = true; DeleteButton.IsEnabled = true; SaveButton.IsEnabled = true; }
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Disable sending before deleting the only credential file.
            _translation.Settings.Save(_translation.Settings.Current with
            { TranslationEnabled = false, TranslationCloudConsent = false });
            _translation.Credentials.Delete();
            SecretBox.Clear();
            AppIdBox.Clear();
            EnabledCheck.IsChecked = false;
            ConsentCheck.IsChecked = false;
            SetStatus("已移除本机密钥并关闭翻译。", false);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Translation", $"Credential deletion failed ({exception.GetType().Name}).");
            SetStatus("密钥移除未完成，请检查目录权限后重试。", true);
        }
    }

    private void OnDocsClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://fanyi-api.baidu.com/product/113") { UseShellExecute = true }); }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Translation", $"Opening provider documentation failed ({exception.GetType().Name}).");
            SetStatus("无法打开浏览器，请手动访问 fanyi-api.baidu.com。", true);
        }
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            error ? "ThemeDangerBrush" : "ThemeMutedTextBrush");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    { if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button) DragMove(); }
}
