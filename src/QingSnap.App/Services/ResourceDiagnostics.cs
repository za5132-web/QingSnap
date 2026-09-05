using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace QingSnap.App.Services;

internal sealed record ResourceSnapshot(
    DateTimeOffset Timestamp,
    string Label,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long GcHeapBytes,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int HandleCount,
    int GdiObjectCount,
    int UserObjectCount,
    int ThreadCount,
    IReadOnlyDictionary<string, int> Gauges)
{
    public string ToLogLine()
    {
        var gauges = Gauges.Count == 0
            ? string.Empty
            : " | " + string.Join(" | ", Gauges.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
        return $"{Label} | WS={ToMegabytes(WorkingSetBytes)}MB | Private={ToMegabytes(PrivateMemoryBytes)}MB | " +
               $"GC={ToMegabytes(GcHeapBytes)}MB | Alloc={ToMegabytes(TotalAllocatedBytes)}MB | " +
               $"Gen={Gen0Collections}/{Gen1Collections}/{Gen2Collections} | Handles={HandleCount} | " +
               $"GDI={GdiObjectCount} | USER={UserObjectCount} | Threads={ThreadCount}{gauges}";
    }

    private static string ToMegabytes(long bytes) => (bytes / (1024D * 1024D)).ToString("0.0");
}

internal static class ResourceDiagnostics
{
    private const string DiagnosticsEnvironmentVariable = "QINGSNAP_DIAGNOSTICS";
    private static readonly ConcurrentDictionary<string, int> Gauges = new(StringComparer.OrdinalIgnoreCase);

#if DEBUG
    private static volatile bool _enabled = true;
#else
    private static volatile bool _enabled;
#endif

    static ResourceDiagnostics()
    {
        var environmentValue = Environment.GetEnvironmentVariable(DiagnosticsEnvironmentVariable);
        if (environmentValue is "1" ||
            string.Equals(environmentValue, "true", StringComparison.OrdinalIgnoreCase))
        {
            _enabled = true;
        }
    }

    public static bool IsEnabled => _enabled;

    public static void Enable() => _enabled = true;

    public static void SetGauge(string name, int value)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Gauges[name] = Math.Max(0, value);
    }

    public static void RemoveGauge(string name)
    {
        if (IsEnabled && !string.IsNullOrWhiteSpace(name))
        {
            Gauges.TryRemove(name, out _);
        }
    }

    public static ResourceSnapshot? Sample(string label, params (string Name, int Value)[] additionalGauges)
    {
        if (!IsEnabled)
        {
            return null;
        }

        try
        {
            var snapshot = Capture(label, additionalGauges);
            DiagnosticLog.Info("Resource", snapshot.ToLogLine());
            return snapshot;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Resource", exception, $"Resource sampling failed at {label}.");
            return null;
        }
    }

    internal static ResourceSnapshot Capture(
        string label,
        params (string Name, int Value)[] additionalGauges)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var gauges = new Dictionary<string, int>(Gauges, StringComparer.OrdinalIgnoreCase);
        AddWindowCounts(gauges);
        foreach (var (name, value) in additionalGauges)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                gauges[name] = Math.Max(0, value);
            }
        }

        var gcInfo = GC.GetGCMemoryInfo();
        return new ResourceSnapshot(
            DateTimeOffset.Now,
            label,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            gcInfo.HeapSizeBytes,
            GC.GetTotalAllocatedBytes(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            process.HandleCount,
            GetGuiResources(process.Handle, 0),
            GetGuiResources(process.Handle, 1),
            process.Threads.Count,
            gauges);
    }

    private static void AddWindowCounts(IDictionary<string, int> gauges)
    {
        var application = System.Windows.Application.Current;
        if (application?.Dispatcher.CheckAccess() != true)
        {
            return;
        }

        var windows = application.Windows.Cast<Window>().ToArray();
        gauges["Overlay"] = windows.Count(window => window.GetType().Name == "CaptureOverlayWindow");
        gauges["Pin"] = windows.Count(window => window.GetType().Name == "StickyImageWindow");
        gauges["History"] = windows.Count(window => window.GetType().Name == "HistoryWindow");
        gauges["OCRWindow"] = windows.Count(window => window.GetType().Name == "OcrResultWindow");
        gauges["Settings"] = windows.Count(window => window.GetType().Name == "SettingsWindow");
        gauges["LongCapture"] = windows.Count(window =>
            window.GetType().Name is "LongCaptureControlWindow" or "LongCaptureOverlayWindow");
    }

    [DllImport("user32.dll")]
    private static extern int GetGuiResources(nint processHandle, int flags);
}
