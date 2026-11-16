using System.IO;
using System.IO.Compression;
using System.Text;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class TranslationCredentialTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "QingSnap-translation-tests", Guid.NewGuid().ToString("N"));
    public TranslationCredentialTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void CredentialsRoundTripEncryptedAndAreExcludedFromSettingsAndFeedback()
    {
        const string secret = "only-synthetic-secret-for-test";
        var credentials = new TranslationCredentialStore(_directory);
        credentials.Save(new("2015063000000001", secret));
        Assert.Equal(secret, credentials.Load()!.Secret);
        var raw = File.ReadAllBytes(Path.Combine(_directory, "baidu-translation.credentials"));
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain(secret, Encoding.Unicode.GetString(raw));
        var settings = new AppSettingsService(_directory);
        settings.Save(settings.Current with { TranslationEnabled = true, TranslationCloudConsent = true });
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(_directory, "settings.json")));
        var bundle = Path.Combine(_directory, "feedback.zip");
        DiagnosticLog.ExportBundle(bundle, settings.Current, includeLogs: false);
        using var archive = ZipFile.OpenRead(bundle);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("credentials", StringComparison.OrdinalIgnoreCase));
        credentials.Delete();
        Assert.Null(credentials.Load());
    }

    [Fact]
    public void DamagedCredentialsFailWithSanitizedMessage()
    {
        File.WriteAllText(Path.Combine(_directory, "baidu-translation.credentials"), "sensitive-plaintext-not-valid-dpapi");
        var exception = Assert.Throws<TranslationException>(() => new TranslationCredentialStore(_directory).Load());
        Assert.DoesNotContain("sensitive-plaintext", exception.ToString());
    }

    [Fact]
    public void LegacySettingsDefaultOffAndSavingDoesNotModifyStableStartup()
    {
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{}");
        var service = new AppSettingsService(_directory);
        Assert.False(service.Current.TranslationEnabled);
        Assert.False(service.Current.TranslationCloudConsent);
        Assert.Equal("zh", service.Current.TranslationTargetLanguage);
        var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "QingSnap.cmd");
        var before = File.Exists(startup) ? File.ReadAllBytes(startup) : null;
        service.Save(service.Current with { StartWithWindows = false, TranslationTargetLanguage = "bad" });
        service.Save(service.Current with { StartWithWindows = true });
        Assert.Equal(before, File.Exists(startup) ? File.ReadAllBytes(startup) : null);
        Assert.Equal("zh", service.Current.TranslationTargetLanguage);
    }

    [Fact]
    public void TrialUsesDifferentDataStateLogsAndInstanceIdentityWithoutVersionBump()
    {
        var stable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingSnap");
        Assert.NotEqual(stable, TranslationTrialEnvironment.DataDirectory);
        Assert.Contains("TranslationTrial", TranslationTrialEnvironment.DataDirectory);
        Assert.Contains("TranslationTrial", DiagnosticLog.DirectoryPath);
        Assert.Contains("BaiduTranslationTrial", TranslationTrialEnvironment.MutexName);
        Assert.Equal(new Version(1, 1, 8, 0), typeof(AppSettings).Assembly.GetName().Version);
        Assert.DoesNotContain("only-synthetic-secret", new TranslationCredentials("2015063000000001", "only-synthetic-secret").ToString());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
