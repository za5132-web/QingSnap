using System.IO;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public void LegacySettingsWithoutQuickTagFieldEnableTheFeatureSafely()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{}");

            var settings = new AppSettingsService(directory);

            Assert.True(settings.Current.ShowQuickCaptureTags);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExplicitlyDisabledQuickTagsRemainDisabledAfterLoading()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "settings.json"),
                """{"ShowQuickCaptureTags":false}""");

            var settings = new AppSettingsService(directory);

            Assert.False(settings.Current.ShowQuickCaptureTags);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacyThreeHotkeysMigrateIntoTheUnifiedActionList()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "settings.json"),
                """{"CaptureHotkey":"ctrl+f2","PinHotkey":"Alt+F4","RepeatHotkey":"Shift+Ctrl+F6"}""");

            var settings = new AppSettingsService(directory).Current;

            Assert.Equal(HotkeyCatalog.Definitions.Count, settings.Hotkeys.Count);
            AssertBinding(settings, HotkeyAction.RegionCapture, "Ctrl+F2", true);
            AssertBinding(settings, HotkeyAction.PinRecentImage, "Alt+F4", true);
            AssertBinding(settings, HotkeyAction.RepeatLastRegion, "Ctrl+Shift+F6", true);
            AssertBinding(settings, HotkeyAction.AutomaticLongCapture, string.Empty, false);
            AssertBinding(settings, HotkeyAction.OpenHistory, string.Empty, false);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DuplicateEnabledBindingsAreDisabledDefensivelyDuringNormalization()
    {
        var normalized = AppSettingsService.Normalize(new AppSettings
        {
            Hotkeys =
            [
                new HotkeyBinding
                {
                    Action = HotkeyAction.RegionCapture,
                    Gesture = "Ctrl+F8",
                    IsEnabled = true
                },
                new HotkeyBinding
                {
                    Action = HotkeyAction.OpenHistory,
                    Gesture = "ctrl+f8",
                    IsEnabled = true
                }
            ]
        });

        AssertBinding(normalized, HotkeyAction.RegionCapture, "Ctrl+F8", true);
        AssertBinding(normalized, HotkeyAction.OpenHistory, "Ctrl+F8", false);
    }

    [Theory]
    [InlineData("F1", true)]
    [InlineData("Ctrl+Shift+Alt+F12", true)]
    [InlineData("alt+f7", true)]
    [InlineData("F13", false)]
    [InlineData("Ctrl+A", false)]
    [InlineData("F1+F2", false)]
    [InlineData("", false)]
    public void HotkeyGestureValidationSupportsOnlyFunctionKeysAndModifiers(string gesture, bool expected) =>
        Assert.Equal(expected, GlobalHotkeyService.IsValidGesture(gesture));

    private static void AssertBinding(
        AppSettings settings,
        HotkeyAction action,
        string gesture,
        bool isEnabled)
    {
        var binding = Assert.Single(settings.Hotkeys, value => value.Action == action);
        Assert.Equal(gesture, binding.Gesture);
        Assert.Equal(isEnabled, binding.IsEnabled);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"QingSnap-settings-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
