using System.Diagnostics;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public static class QrCodeInteractionService
{
    public static async Task<string> InvokeAsync(
        QrCodeResult result,
        ClipboardService clipboardService)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(clipboardService);

        if (result.SafeUrl is { } safeUrl)
        {
            Process.Start(new ProcessStartInfo(safeUrl.AbsoluteUri)
            {
                UseShellExecute = true
            });
            DiagnosticLog.Info("QrCode", $"用户从二维码热点打开链接：{safeUrl.Host}");
            return "已在默认浏览器中打开";
        }

        await clipboardService.CopyTextAsync(result.Text);
        DiagnosticLog.Info("QrCode", $"用户从二维码热点复制文本：{result.Text.Length:N0} 个字符。");
        return "二维码内容已复制";
    }
}
