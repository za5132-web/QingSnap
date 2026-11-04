using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class ThemeServiceTests
{
    [Theory]
    [InlineData(null, "Dark")]
    [InlineData("", "Dark")]
    [InlineData("unexpected", "Dark")]
    [InlineData("dark", "Dark")]
    [InlineData(" LIGHT ", "Light")]
    [InlineData("system", "System")]
    public void PreferenceNormalizationUsesSafeDefaults(string? input, string expected) =>
        Assert.Equal(expected, ThemeService.NormalizePreference(input));

    [Theory]
    [InlineData("Dark", true, false, "Dark")]
    [InlineData("Light", false, false, "Light")]
    [InlineData("System", true, false, "Light")]
    [InlineData("System", false, false, "Dark")]
    [InlineData("Dark", false, true, "HighContrast")]
    [InlineData("Light", true, true, "HighContrast")]
    public void ResolutionHonorsSystemAndHighContrast(string preference, bool systemLight, bool highContrast, string expected) =>
        Assert.Equal(expected, ThemeService.ResolveTheme(preference, systemLight, highContrast));

    [Fact]
    public void OldJsonAndInvalidPreferenceRemainDarkWithoutChangingAnnotationColor()
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("""{"AnnotationColor":"#FF123456"}""")!;
        Assert.Equal("Dark", AppSettingsService.Normalize(legacy).Theme);
        var normalized = AppSettingsService.Normalize(legacy with { Theme = "not-a-theme" });
        Assert.Equal("Dark", normalized.Theme);
        Assert.Equal("#FF123456", normalized.AnnotationColor);
        Assert.Equal("Light", AppSettingsService.Normalize(legacy with { Theme = "light" }).Theme);
    }

    [Fact]
    public void SavedPreferenceCanLoadFromIsolatedJsonWithoutStartupSideEffects()
    {
        var directory = Path.Combine(Path.GetTempPath(), "QingSnap-theme-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"), """{"Theme":"System","HasCompletedFirstRunTutorial":true}""");
            var settings = new AppSettingsService(directory).Current;
            Assert.Equal("System", settings.Theme);
            Assert.True(settings.HasCompletedFirstRunTutorial);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BothDictionariesHaveTheSameTwentySixFrozenBrushKeys() => RunSta(() =>
    {
        var dark = ThemeService.LoadDictionary("Dark");
        var light = ThemeService.LoadDictionary("Light");
        var darkKeys = dark.Keys.Cast<string>().Order().ToArray();
        Assert.Equal(26, darkKeys.Length);
        Assert.Equal(darkKeys, light.Keys.Cast<string>().Order().ToArray());
        foreach (var key in darkKeys)
        {
            Assert.StartsWith("Theme", key);
            Assert.EndsWith("Brush", key);
            Assert.True(Assert.IsType<SolidColorBrush>(dark[key]).IsFrozen);
            Assert.True(Assert.IsType<SolidColorBrush>(light[key]).IsFrozen);
        }
    });

    [Fact]
    public void RepeatedSwitchingDoesNotAccumulateDictionariesAndDisposedServiceIgnoresChanges() => RunSta(() =>
    {
        // Never use Application.Current.Dispatcher: another STA test may own it.
        var resources = new ResourceDictionary();
        var service = new ThemeService(resources, Dispatcher.CurrentDispatcher, false, () => false, () => false);
        using (service)
        {
            var notifications = 0;
            service.ThemeChanged += (_, _) => notifications++;
            for (var index = 0; index < 20; index++)
            {
                service.Apply(index % 2 == 0 ? "Light" : "Dark");
                Assert.Single(resources.MergedDictionaries);
            }
            Assert.Equal(20, notifications);
            service.Apply("Dark");
            Assert.Equal(20, notifications);
        }
        var previous = resources.MergedDictionaries[0];
        service.Apply("Light");
        Assert.Same(previous, resources.MergedDictionaries[0]);
    });

    [Fact]
    public void ExistingControlAndNewControlResolveCurrentPalette() => RunSta(() =>
    {
        var resources = new ResourceDictionary();
        using var service = new ThemeService(resources, Dispatcher.CurrentDispatcher, false, () => false, () => false);
        service.Apply("Dark");
        var host = new Border { Resources = resources };
        var existing = new TextBlock();
        existing.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextBrush");
        host.Child = existing;
        var dark = ((SolidColorBrush)existing.Foreground).Color;
        service.Apply("Light");
        var light = ((SolidColorBrush)existing.Foreground).Color;
        Assert.NotEqual(dark, light);
        var next = new TextBlock();
        next.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextBrush");
        host.Child = next;
        Assert.Equal(light, ((SolidColorBrush)next.Foreground).Color);
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Theme test did not finish.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
