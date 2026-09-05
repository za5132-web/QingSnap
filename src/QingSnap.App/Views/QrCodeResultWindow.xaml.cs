using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using QingSnap.App.Models;
using QingSnap.App.Services;
using WpfButton = System.Windows.Controls.Button;

namespace QingSnap.App.Views;

public partial class QrCodeResultWindow : Window
{
    private readonly ClipboardService _clipboardService;

    public QrCodeResultWindow(
        IReadOnlyList<QrCodeResult> results,
        ClipboardService clipboardService,
        string? sourceName = null)
    {
        Results = results;
        _clipboardService = clipboardService;
        InitializeComponent();
        DataContext = this;

        TitleStatusText.Text = string.IsNullOrWhiteSpace(sourceName)
            ? $"本地识别 · {results.Count:N0} 个结果"
            : $"{sourceName} · {results.Count:N0} 个结果";
        FooterStatusText.Text = results.Count == 1
            ? "识别完成，可以复制内容；网址需主动点击打开。"
            : $"识别完成，共检测到 {results.Count:N0} 个二维码。";
    }

    public IReadOnlyList<QrCodeResult> Results { get; }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: QrCodeResult result })
        {
            return;
        }

        try
        {
            await _clipboardService.CopyTextAsync(result.Text);
            FooterStatusText.Text = "二维码内容已复制到剪贴板。";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("QrCode", exception, "复制二维码内容失败。");
            FooterStatusText.Text = "复制失败，请稍后重试。";
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: QrCodeResult { SafeUrl: not null } result })
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = result.SafeUrl.AbsoluteUri,
                UseShellExecute = true
            });
            FooterStatusText.Text = "已交给默认浏览器打开。";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("QrCode", exception, "打开二维码网址失败。");
            FooterStatusText.Text = "无法打开该网址，可以先复制后手动打开。";
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
