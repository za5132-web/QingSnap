using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SystemColors = System.Windows.SystemColors;
using WpfColor = System.Windows.Media.Color;

namespace QingSnap.App.Services;

/// <summary>One event-driven palette for all WPF windows and the tray menu.</summary>
public sealed class ThemeService : IDisposable
{
    private readonly ResourceDictionary _resources;
    private readonly Dispatcher _dispatcher;
    private readonly bool _watchSystemChanges;
    private readonly Func<bool> _systemUsesLight;
    private readonly Func<bool> _highContrast;
    private ResourceDictionary? _activeDictionary;
    private string _preference = "Dark";
    private bool _disposed;

    public ThemeService(ResourceDictionary resources, Dispatcher dispatcher, bool watchSystemChanges = true)
        : this(resources, dispatcher, watchSystemChanges, ReadSystemUsesLightTheme, () => SystemParameters.HighContrast)
    {
    }

    internal ThemeService(ResourceDictionary resources, Dispatcher dispatcher, bool watchSystemChanges,
        Func<bool> systemUsesLight, Func<bool> highContrast)
    {
        _resources = resources;
        _dispatcher = dispatcher;
        _watchSystemChanges = watchSystemChanges;
        _systemUsesLight = systemUsesLight;
        _highContrast = highContrast;
        _activeDictionary = resources.MergedDictionaries.FirstOrDefault(IsThemeDictionary);
        if (watchSystemChanges)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged += OnSystemPropertyChanged;
        }
    }

    public string CurrentTheme { get; private set; } = string.Empty;

    public event EventHandler? ThemeChanged;

    public static string NormalizePreference(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "LIGHT" => "Light",
        "SYSTEM" => "System",
        _ => "Dark"
    };

    internal static string ResolveTheme(string? preference, bool systemUsesLight, bool highContrast) =>
        highContrast ? "HighContrast" : NormalizePreference(preference) switch
        {
            "Light" => "Light",
            "System" when systemUsesLight => "Light",
            _ => "Dark"
        };

    public void Apply(string? preference)
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            if (!_dispatcher.HasShutdownStarted)
            {
                _dispatcher.BeginInvoke(() => Apply(preference));
            }
            return;
        }

        _preference = NormalizePreference(preference);
        ApplyResolvedTheme(ResolveTheme(_preference, _systemUsesLight(), _highContrast()));
    }

    public WpfColor GetColor(string key) =>
        (_resources[key] as SolidColorBrush)?.Color ?? SystemColors.WindowTextColor;

    private void ApplyResolvedTheme(string resolvedTheme, bool refreshHighContrast = false)
    {
        if (CurrentTheme == resolvedTheme && !refreshHighContrast)
        {
            return;
        }

        try
        {
            var dictionary = resolvedTheme == "HighContrast"
                ? CreateHighContrastDictionary()
                : LoadDictionary(resolvedTheme);
            if (_activeDictionary is not null && _resources.MergedDictionaries.Contains(_activeDictionary))
            {
                var index = _resources.MergedDictionaries.IndexOf(_activeDictionary);
                _resources.MergedDictionaries[index] = dictionary;
            }
            else
            {
                _resources.MergedDictionaries.Add(dictionary);
            }
            _activeDictionary = dictionary;
            CurrentTheme = resolvedTheme;
            ThemeChanged?.Invoke(this, EventArgs.Empty);
            DiagnosticLog.Info("Theme", $"Applied {resolvedTheme} interface palette.");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Theme", exception, "Could not apply the requested interface palette; previous palette retained.");
        }
    }

    internal static ResourceDictionary LoadDictionary(string name)
    {
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"/QingSnap;component/Themes/{(name == "Light" ? "Light" : "Dark")}.xaml", UriKind.Relative)
        };
        foreach (var brush in dictionary.Values.OfType<SolidColorBrush>())
        {
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }
        }
        return dictionary;
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary) =>
        dictionary.Source?.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase) == true ||
        dictionary.Contains("ThemeWindowBrush");

    private static bool ReadSystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLog.Warning("Theme", $"System theme preference unavailable: {exception.GetType().Name}; using dark.");
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueSystemRefresh();

    private void OnSystemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            QueueSystemRefresh();
        }
    }

    private void QueueSystemRefresh()
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                ApplyResolvedTheme(
                    ResolveTheme(_preference, _systemUsesLight(), _highContrast()),
                    refreshHighContrast: CurrentTheme == "HighContrast");
            }
        }, DispatcherPriority.Background);
    }

    private static ResourceDictionary CreateHighContrastDictionary()
    {
        var result = LoadDictionary("Dark");
        foreach (var key in result.Keys.Cast<string>().ToArray())
        {
            var color = key switch
            {
                "ThemeTextBrush" or "ThemeMutedTextBrush" or "ThemeSubtleTextBrush" or
                    "ThemeDangerBrush" or "ThemeWarningBrush" => SystemColors.WindowTextColor,
                "ThemeAccentBrush" or "ThemeAccentHoverBrush" or "ThemeAccentPressedBrush" or
                    "ThemeSelectionBrush" or "ThemeScrollbarBrush" => SystemColors.HighlightColor,
                "ThemeOnAccentBrush" => SystemColors.HighlightTextColor,
                "ThemeBorderBrush" or "ThemeBorderStrongBrush" => SystemColors.WindowTextColor,
                _ => SystemColors.WindowColor
            };
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            result[key] = brush;
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_watchSystemChanges)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemPropertyChanged;
        }
        ThemeChanged = null;
    }
}
