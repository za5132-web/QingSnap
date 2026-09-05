using System.Diagnostics;
using System.Text;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;

namespace QingSnap.App.Services;

internal static class CaptureSourceMetadataService
{
    public static CaptureHistoryContext Create(
        DrawingRectangle region,
        bool isLongCapture,
        nint preferredWindow = default)
    {
        string? processName = null;
        string? windowTitle = null;
        try
        {
            var center = new NativeMethods.NativePoint(
                region.Left + Math.Max(0, region.Width / 2),
                region.Top + Math.Max(0, region.Height / 2));
            var window = preferredWindow;
            window = NativeMethods.GetAncestor(window, NativeMethods.GetAncestorRoot);
            if (window == nint.Zero || IsQingSnapWindow(window))
            {
                window = NativeMethods.FindWindowAtPointExcludingProcess(center, (uint)Environment.ProcessId);
                window = NativeMethods.GetAncestor(window, NativeMethods.GetAncestorRoot);
            }

            if (window != nint.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(window, out var processId);
                if (processId > 0 && processId != Environment.ProcessId)
                {
                    try
                    {
                        using var process = Process.GetProcessById(checked((int)processId));
                        processName = NormalizeProcessName(process.ProcessName);
                    }
                    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                    {
                        DiagnosticLog.Warning("HistoryMetadata", $"读取截图来源进程失败：{exception.Message}");
                    }

                    var length = NativeMethods.GetWindowTextLength(window);
                    if (length > 0)
                    {
                        var buffer = new StringBuilder(length + 1);
                        if (NativeMethods.GetWindowText(window, buffer, buffer.Capacity) > 0)
                        {
                            windowTitle = buffer.ToString();
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("HistoryMetadata", exception, "读取截图来源窗口信息失败。");
        }

        var monitor = FormsScreen.FromRectangle(region);
        return new CaptureHistoryContext(
            isLongCapture,
            processName,
            windowTitle,
            monitor.DeviceName,
            monitor.DeviceName,
            region.X,
            region.Y,
            region.Width,
            region.Height);
    }

    private static bool IsQingSnapWindow(nint window)
    {
        if (window == nint.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId == Environment.ProcessId;
    }

    internal static string NormalizeProcessName(string processName)
    {
        var normalized = processName.Trim();
        return string.IsNullOrEmpty(normalized) || normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : normalized + ".exe";
    }
}
