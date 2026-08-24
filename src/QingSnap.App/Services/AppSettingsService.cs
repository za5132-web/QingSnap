using System.IO;
using System.Text.Json;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsPath;

    public AppSettingsService()
    {
        var appDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QingSnap");
        Directory.CreateDirectory(appDirectory);
        _settingsPath = Path.Combine(appDirectory, "settings.json");
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler? SettingsChanged;

    public void Save(AppSettings settings)
    {
        var normalized = Normalize(settings);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temporaryPath, _settingsPath, true);
        ApplyStartupSetting(normalized.StartWithWindows);
        Current = normalized;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return Normalize(new AppSettings());
            }

            return Normalize(
                JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions) ??
                new AppSettings());
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Normalize(new AppSettings());
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        var defaultHistoryDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QingSnap",
            "History");
        return settings with
        {
            CaptureHotkey = NormalizeHotkey(settings.CaptureHotkey, "F1"),
            PinHotkey = NormalizeHotkey(settings.PinHotkey, "F3"),
            RepeatHotkey = NormalizeHotkey(settings.RepeatHotkey, "Shift+F1"),
            HistoryDirectory = string.IsNullOrWhiteSpace(settings.HistoryDirectory)
                ? defaultHistoryDirectory
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.HistoryDirectory.Trim())),
            HistoryRetentionDays = Math.Clamp(settings.HistoryRetentionDays, 0, 3650),
            OutputFormat = settings.OutputFormat.ToUpperInvariant() is "JPG" or "BMP"
                ? settings.OutputFormat.ToUpperInvariant()
                : "PNG",
            JpegQuality = Math.Clamp(settings.JpegQuality, 50, 100),
            CaptureDelaySeconds = Math.Clamp(settings.CaptureDelaySeconds, 0, 10),
            CloseInteraction = string.Equals(
                settings.CloseInteraction,
                "Button",
                StringComparison.OrdinalIgnoreCase)
                ? "Button"
                : "Escape",
            OcrEngine = string.Equals(settings.OcrEngine, "Windows", StringComparison.OrdinalIgnoreCase)
                ? "Windows"
                : "Advanced",
            LongScrollWheelDelta = Math.Clamp(settings.LongScrollWheelDelta / 120 * 120, 120, 1200),
            LongMatchRetryCount = Math.Clamp(settings.LongMatchRetryCount, 1, 6),
            LongMinimumOverlapPercent = Math.Clamp(settings.LongMinimumOverlapPercent, 12, 50),
            AnnotationThickness = Math.Clamp(settings.AnnotationThickness, 1, 12),
            AnnotationFontSize = Math.Clamp(settings.AnnotationFontSize, 12, 48),
            AnnotationColor = NormalizeColor(settings.AnnotationColor)
        };
    }

    private static string NormalizeHotkey(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string NormalizeColor(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                _ = System.Windows.Media.ColorConverter.ConvertFromString(value);
                return value.Trim();
            }
            catch (FormatException)
            {
            }
        }

        return "#FFFF4E5B";
    }

    private static void ApplyStartupSetting(bool enabled)
    {
        var startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        Directory.CreateDirectory(startupDirectory);
        var launcherPath = Path.Combine(startupDirectory, "QingSnap.cmd");
        if (enabled)
        {
            var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 QingSnap 程序路径。");
            File.WriteAllText(
                launcherPath,
                $"@echo off{Environment.NewLine}start \"\" \"{executablePath}\"{Environment.NewLine}");
        }
        else
        {
            if (File.Exists(launcherPath))
            {
                File.Delete(launcherPath);
            }
        }
    }
}
