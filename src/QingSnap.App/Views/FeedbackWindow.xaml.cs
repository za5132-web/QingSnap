using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using QingSnap.App.Services;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QingSnap.App.Views;

public partial class FeedbackWindow : Window
{
    private const string NewIssueUrl = "https://github.com/za5132-web/QingSnap/issues/new";
    private readonly AppSettingsService _settingsService;

    public FeedbackWindow(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();
        Loaded += (_, _) => FeedbackTitleBox.Focus();
    }

    private void OnPrepareFeedbackClick(object sender, RoutedEventArgs e)
    {
        var title = FeedbackTitleBox.Text.Trim();
        var body = FeedbackBodyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
        {
            SetStatus("请填写反馈标题和问题描述。", isError: true);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存 QingSnap 反馈包",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            FileName = $"QingSnap-Feedback-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var feedbackText = BuildFeedbackText(title, body, ContactBox.Text.Trim());
            DiagnosticLog.ExportBundle(
                dialog.FileName,
                _settingsService.Current,
                feedbackText,
                IncludeLogsCheck.IsChecked == true);
            DiagnosticLog.Info("Feedback", $"反馈包已生成：{dialog.FileName}");

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{dialog.FileName}\"",
                UseShellExecute = true
            });
            Process.Start(new ProcessStartInfo
            {
                FileName = BuildIssueUrl(title, body),
                UseShellExecute = true
            });
            SetStatus("反馈包已生成，反馈页面已打开。请把 ZIP 拖入页面后提交。", isError: false);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Feedback", exception, "生成反馈包失败。");
            SetStatus($"生成反馈包失败：{exception.Message}", isError: true);
        }
    }

    private static string BuildFeedbackText(string title, string body, string contact)
    {
        var text = new StringBuilder()
            .AppendLine($"标题：{title}")
            .AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        if (!string.IsNullOrWhiteSpace(contact))
        {
            text.AppendLine($"联系方式：{contact}");
        }

        return text.AppendLine()
            .AppendLine("问题描述与复现步骤：")
            .AppendLine(body)
            .ToString();
    }

    private static string BuildIssueUrl(string title, string body)
    {
        var issueBody = $"{body}\n\n---\n已使用 QingSnap 内置反馈工具生成诊断包，请将 ZIP 拖到这里后再提交。";
        return $"{NewIssueUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(issueBody)}";
    }

    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(isError
            ? System.Windows.Media.Color.FromRgb(255, 145, 137)
            : System.Windows.Media.Color.FromRgb(118, 223, 238));
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
