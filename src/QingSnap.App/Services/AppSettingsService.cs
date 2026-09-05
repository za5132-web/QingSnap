using System.IO;
using System.Text.Json;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsPath;

    public AppSettingsService(string? dataDirectory = null)
    {
        DataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? ResolveDataDirectory()
            : Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        _settingsPath = Path.Combine(DataDirectory, "settings.json");
        Current = Load();
    }

    public string DataDirectory { get; }

    public bool IsPortableMode => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));

    public AppSettings Current { get; private set; }

    public event EventHandler? SettingsChanged;

    public void Save(AppSettings settings)
    {
        var normalized = Normalize(settings, DataDirectory);
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
                return Normalize(new AppSettings(), DataDirectory);
            }

            return Normalize(
                JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions) ??
                new AppSettings(),
                DataDirectory);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Normalize(new AppSettings(), DataDirectory);
        }
    }

    internal static AppSettings Normalize(AppSettings settings) =>
        Normalize(settings, ResolveDataDirectory());

    private static AppSettings Normalize(AppSettings settings, string dataDirectory)
    {
        var defaultHistoryDirectory = Path.Combine(dataDirectory, "History");
        var hotkeys = NormalizeHotkeys(settings);
        var ocrModel = string.IsNullOrWhiteSpace(settings.OcrModel)
            ? string.Equals(settings.OcrEngine, "Advanced", StringComparison.OrdinalIgnoreCase)
                ? OcrModelManager.SmallModel
                : OcrModelManager.NoModel
            : OcrModelManager.NormalizeModel(settings.OcrModel);
        return settings with
        {
            Hotkeys = hotkeys,
            CaptureHotkey = FindGesture(hotkeys, HotkeyAction.RegionCapture),
            PinHotkey = FindGesture(hotkeys, HotkeyAction.PinRecentImage),
            RepeatHotkey = FindGesture(hotkeys, HotkeyAction.RepeatLastRegion),
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
            OcrEngine = ocrModel == OcrModelManager.NoModel ? "None" : "Advanced",
            OcrModel = ocrModel,
            OcrPerformanceMode = string.Equals(
                settings.OcrPerformanceMode,
                "Balanced",
                StringComparison.OrdinalIgnoreCase)
                ? "Balanced"
                : "Instant",
            LongScrollWheelDelta = Math.Clamp(settings.LongScrollWheelDelta / 120 * 120, 120, 1200),
            LongMatchRetryCount = Math.Clamp(settings.LongMatchRetryCount, 1, 6),
            LongMinimumOverlapPercent = Math.Clamp(settings.LongMinimumOverlapPercent, 12, 50),
            AnnotationThickness = Math.Clamp(settings.AnnotationThickness, 1, 12),
            AnnotationFontSize = Math.Clamp(settings.AnnotationFontSize, 12, 48),
            AnnotationColor = NormalizeColor(settings.AnnotationColor)
        };
    }

    private static List<HotkeyBinding> NormalizeHotkeys(AppSettings settings)
    {
        if (settings.Hotkeys is null || settings.Hotkeys.Count == 0)
        {
            return HotkeyCatalog.Definitions
                .Select(definition => definition.Action switch
                {
                    HotkeyAction.RegionCapture => CreateLegacyBinding(
                        definition,
                        settings.CaptureHotkey),
                    HotkeyAction.RepeatLastRegion => CreateLegacyBinding(
                        definition,
                        settings.RepeatHotkey),
                    HotkeyAction.PinRecentImage => CreateLegacyBinding(
                        definition,
                        settings.PinHotkey),
                    _ => new HotkeyBinding
                    {
                        Action = definition.Action,
                        Gesture = definition.DefaultGesture,
                        IsEnabled = definition.DefaultEnabled
                    }
                })
                .ToList();
        }

        var existing = settings.Hotkeys
            .GroupBy(binding => binding.Action)
            .ToDictionary(group => group.Key, group => group.Last());
        var usedGestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<HotkeyBinding>(HotkeyCatalog.Definitions.Count);
        foreach (var definition in HotkeyCatalog.Definitions)
        {
            var binding = existing.TryGetValue(definition.Action, out var configured)
                ? configured
                : new HotkeyBinding
                {
                    Action = definition.Action,
                    Gesture = definition.DefaultGesture,
                    IsEnabled = definition.DefaultEnabled
                };
            var isValid = HotkeyGestureParser.TryNormalize(binding.Gesture, out var gesture);
            var isEnabled = binding.IsEnabled && isValid && usedGestures.Add(gesture);
            normalized.Add(binding with
            {
                Action = definition.Action,
                Gesture = isValid ? gesture : string.Empty,
                IsEnabled = isEnabled
            });
        }

        return normalized;
    }

    private static HotkeyBinding CreateLegacyBinding(
        HotkeyActionDefinition definition,
        string? legacyGesture)
    {
        if (!HotkeyGestureParser.TryNormalize(legacyGesture, out var normalized) &&
            !HotkeyGestureParser.TryNormalize(definition.DefaultGesture, out normalized))
        {
            return new HotkeyBinding { Action = definition.Action };
        }

        return new HotkeyBinding
        {
            Action = definition.Action,
            Gesture = normalized,
            IsEnabled = true
        };
    }

    private static string FindGesture(IEnumerable<HotkeyBinding> bindings, HotkeyAction action) =>
        bindings.First(binding => binding.Action == action).Gesture;

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

    private static string ResolveDataDirectory() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QingSnap");
}
