using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public static class DiagnosticLog
{
    private const long MaximumLogBytes = 4L * 1024L * 1024L;
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QingSnap",
        "Logs");
    private static readonly string CurrentLogPath = Path.Combine(LogDirectory, "QingSnap.log");

    public static string DirectoryPath => LogDirectory;

    public static void Initialize()
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                DeleteExpiredLogs();
            }

            Info("Application", "QingSnap diagnostics initialized.");
        }
        catch
        {
        }
    }

    public static IDisposable Measure(string category, string operation) =>
        new TimedOperation(category, operation);

    public static void Info(string category, string message) => Write("INF", category, message, null);

    public static void Warning(string category, string message) => Write("WRN", category, message, null);

    public static void Error(string category, Exception exception, string? message = null) =>
        Write("ERR", category, message ?? exception.Message, exception);

    public static string ExportBundle(
        string destinationPath,
        AppSettings settings,
        string? feedbackText = null,
        bool includeLogs = true)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(LogDirectory);
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
            if (includeLogs)
            {
                foreach (var logPath in Directory.EnumerateFiles(LogDirectory, "*.log"))
                {
                    archive.CreateEntryFromFile(logPath, $"logs/{Path.GetFileName(logPath)}", CompressionLevel.Optimal);
                }
            }

            if (!string.IsNullOrWhiteSpace(feedbackText))
            {
                var feedbackEntry = archive.CreateEntry("feedback.txt", CompressionLevel.Optimal);
                using var feedbackWriter = new StreamWriter(feedbackEntry.Open(), new UTF8Encoding(false));
                feedbackWriter.Write(feedbackText.Trim());
            }

            var systemEntry = archive.CreateEntry("system.json", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(systemEntry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(new
                {
                    Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
                    OperatingSystem = Environment.OSVersion.VersionString,
                    Framework = Environment.Version.ToString(),
                    ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    ProcessorCount = Environment.ProcessorCount,
                    WorkingSetBytes = Environment.WorkingSet,
                    OcrEngine = settings.OcrEngine,
                    OcrModel = settings.OcrModel,
                    OcrPerformanceMode = settings.OcrPerformanceMode,
                    OutputFormat = settings.OutputFormat,
                    SmartWindowSelection = settings.SmartWindowSelection,
                    LongScrollWheelDelta = settings.LongScrollWheelDelta,
                    LongMatchRetryCount = settings.LongMatchRetryCount,
                    LongMinimumOverlapPercent = settings.LongMinimumOverlapPercent,
                    ExportedAt = DateTimeOffset.Now
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        return destinationPath;
    }

    private static void Write(string level, string category, string message, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                var line = $"[{DateTimeOffset.Now:O}] [{level}] [{category}] {message}";
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                File.AppendAllText(CurrentLogPath, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(CurrentLogPath) || new FileInfo(CurrentLogPath).Length < MaximumLogBytes)
        {
            return;
        }

        var archivedPath = Path.Combine(LogDirectory, $"QingSnap-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.Move(CurrentLogPath, archivedPath, true);
    }

    private static void DeleteExpiredLogs()
    {
        var threshold = DateTime.Now.AddDays(-7);
        foreach (var path in Directory.EnumerateFiles(LogDirectory, "QingSnap-*.log"))
        {
            if (File.GetLastWriteTime(path) < threshold)
            {
                File.Delete(path);
            }
        }
    }

    private sealed class TimedOperation(string category, string operation) : IDisposable
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();
            Info(category, $"{operation} completed in {_stopwatch.Elapsed.TotalMilliseconds:0.0} ms.");
        }
    }
}
