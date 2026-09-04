using System.Drawing;
using System.Windows.Forms;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly CaptureCoordinator _captureCoordinator;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _appIcon;
    private readonly ToastNotificationService _toastNotifications = new();
    private bool _disposed;

    public TrayIconService(
        CaptureCoordinator captureCoordinator,
        AppSettingsService settingsService,
        Action openSettings)
    {
        _captureCoordinator = captureCoordinator;
        _appIcon = QingSnapTrayIconFactory.Create();

        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9F)
        };
        menu.Items.Add(CreateItem($"区域截图    {settingsService.Current.CaptureHotkey}", (_, _) => _captureCoordinator.StartRegionCapture(), true));
        menu.Items.Add(CreateItem("长截图（自动滚动）", (_, _) => _captureCoordinator.StartLongCapture()));
        menu.Items.Add(CreateItem("长截图（手动滚动）", (_, _) => _captureCoordinator.StartManualLongCapture()));
        menu.Items.Add(CreateItem($"重复上次范围    {settingsService.Current.RepeatHotkey}", (_, _) => _captureCoordinator.RepeatLastCapture()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem($"循环贴图（最近 5 张）    {settingsService.Current.PinHotkey}", (_, _) => _captureCoordinator.PinClipboardImage()));
        menu.Items.Add(CreateItem("识别最近截图文字", (_, _) => _captureCoordinator.RecognizeLatestCapture()));
        menu.Items.Add(CreateItem("截图记录", (_, _) => _captureCoordinator.OpenHistoryWindow()));
        menu.Items.Add(CreateItem("打开记录文件夹", (_, _) => _captureCoordinator.OpenHistoryDirectory()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("设置", (_, _) => openSettings()));
        menu.Items.Add(CreateItem("导出诊断信息", (_, _) => _captureCoordinator.ExportDiagnostics()));
        menu.Items.Add(CreateItem("退出 QingSnap", (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = $"QingSnap — {settingsService.Current.CaptureHotkey} 截图 · {settingsService.Current.PinHotkey} 贴图",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _captureCoordinator.StartRegionCapture();
    }

    public event EventHandler? ExitRequested;

    public void ShowCaptureCompleted(CaptureResult result)
    {
        if (result.CopiedToClipboard)
        {
            _toastNotifications.ShowSuccess("已复制到剪贴板");
        }
        else if (result.CopyRequested)
        {
            _toastNotifications.ShowWarning("截图已保存\n剪贴板仍被占用");
        }
        else
        {
            _toastNotifications.ShowSuccess("截图已保存");
        }
    }

    public void ShowError(string message) => _toastNotifications.ShowWarning(message);

    public void ShowDelay(int seconds) => _toastNotifications.ShowCountdown(seconds);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _toastNotifications.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _appIcon.Dispose();
    }

    private static ToolStripMenuItem CreateItem(string text, EventHandler onClick, bool isDefault = false)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += onClick;
        if (isDefault)
        {
            item.Font = new Font(item.Font, FontStyle.Bold);
        }

        return item;
    }
}
