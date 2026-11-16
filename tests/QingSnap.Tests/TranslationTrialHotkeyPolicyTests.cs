using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class TranslationTrialHotkeyPolicyTests
{
    [Fact]
    public void DefaultTrialRemainsIsolated() => Assert.False(TranslationTrialEnvironment.HasHotkeyOptIn([]));

    [Theory]
    [InlineData("--enable-global-hotkeys", true)]
    [InlineData("--ENABLE-GLOBAL-HOTKEYS", true)]
    [InlineData("--settings", false)]
    [InlineData("--enable-global-hotkeys=false", false)]
    [InlineData("--enable-global-hotkeys=1", false)]
    [InlineData("--disable-global-hotkeys", false)]
    public void RequiresExactExplicitLaunchArgument(string argument, bool expected) =>
        Assert.Equal(expected, TranslationTrialEnvironment.HasHotkeyOptIn(["QingSnap.exe", argument]));

    [Fact]
    public void OptInDoesNotChangeTrialIdentityOrStorage()
    {
        var directory = TranslationTrialEnvironment.DataDirectory;
        var mutex = TranslationTrialEnvironment.MutexName;
        Assert.True(TranslationTrialEnvironment.HasHotkeyOptIn(["--settings", "--enable-global-hotkeys"]));
        Assert.Equal(directory, TranslationTrialEnvironment.DataDirectory);
        Assert.Equal(mutex, TranslationTrialEnvironment.MutexName);
        Assert.True(TranslationTrialEnvironment.IsTrial);
    }
}
