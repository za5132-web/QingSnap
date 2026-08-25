using System.Drawing;
using System.Windows.Forms;

namespace QingSnap.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly CaptureCoordinator _captureCoordinator;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _appIcon;
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
        menu.Items.Add(CreateItem($"贴出剪贴板图片    {settingsService.Current.PinHotkey}", (_, _) => _captureCoordinator.PinClipboardImage()));
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

    public void ShowCaptureCompleted(string imagePath, int width, int height)
    {
        _notifyIcon.BalloonTipTitle = $"已复制  {width} × {height}";
        _notifyIcon.BalloonTipText = "截图已保存到记录。";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(1400);
    }

    public void ShowError(string message)
    {
        _notifyIcon.BalloonTipTitle = "QingSnap";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _notifyIcon.ShowBalloonTip(2600);
    }

    public void ShowDelay(int seconds)
    {
        _notifyIcon.BalloonTipTitle = $"{seconds} 秒后截图";
        _notifyIcon.BalloonTipText = "请切换到需要截取的画面。";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(Math.Max(1000, seconds * 1000));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
