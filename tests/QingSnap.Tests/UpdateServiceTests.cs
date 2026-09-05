using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.3.0-beta.1", 1, 3, 0)]
    [InlineData("V2.0.1+build.7", 2, 0, 1)]
    public void TryParseVersion_AcceptsReleaseTagFormats(
        string value,
        int major,
        int minor,
        int build)
    {
        Assert.True(UpdateService.TryParseVersion(value, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Fact]
    public void TryExtractSha256_ReadsChecksumFromReleaseNotes()
    {
        const string package = "QingSnap-v1.1.0.zip";
        const string hash = "21031098EFF6D22131DD3A76E9D67B8DB19A8B73D4A1DCF761363E9F1C3288C3";
        var notes = $"便携包已上传。\nSHA-256: {hash}\n文件：{package}";

        Assert.Equal(hash, UpdateService.TryExtractSha256(notes, package));
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ParsesGitHubReleaseAndDetectsNewVersion()
    {
        var dataDirectory = CreateDataDirectory();
        try
        {
            const string hash = "21031098EFF6D22131DD3A76E9D67B8DB19A8B73D4A1DCF761363E9F1C3288C3";
            var payload = JsonSerializer.Serialize(new
            {
                tag_name = "v1.1.0",
                published_at = "2026-09-05T01:30:00Z",
                body = $"功能更新\nSHA-256: {hash}",
                html_url = "https://github.com/za5132-web/QingSnap/releases/tag/v1.1.0",
                assets = new[]
                {
                    new
                    {
                        name = "QingSnap-v1.1.0.zip",
                        browser_download_url = "https://github.com/za5132-web/QingSnap/releases/download/v1.1.0/QingSnap-v1.1.0.zip",
                        size = 6543210
                    }
                }
            });
            using var client = new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                }));
            using var service = new UpdateService(dataDirectory, client, new Version(1, 0, 2));

            var result = await service.CheckForUpdatesAsync(force: true);

            Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
            Assert.NotNull(result.Release);
            Assert.Equal("v1.1.0", result.Release.TagName);
            Assert.Equal(hash, result.Release.ExpectedSha256);
            Assert.True(result.Release.CanDownload);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_VerifiesSha256BeforePublishingPackage()
    {
        var dataDirectory = CreateDataDirectory();
        var packageBytes = Encoding.UTF8.GetBytes("QingSnap verified update payload");
        var hash = Convert.ToHexString(SHA256.HashData(packageBytes));
        var unique = Guid.NewGuid().ToString("N");
        var packageName = $"QingSnap-test-{unique}.zip";
        var release = new UpdateReleaseInfo(
            $"test-{unique}",
            new Version(9, 9, 9),
            DateTimeOffset.UtcNow,
            "test",
            packageName,
            new Uri($"https://github.com/za5132-web/QingSnap/releases/download/test/{packageName}"),
            packageBytes.Length,
            hash,
            new Uri("https://github.com/za5132-web/QingSnap/releases"));

        string? downloadedPath = null;
        try
        {
            using var client = new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes)
                }));
            using var service = new UpdateService(dataDirectory, client, new Version(1, 0, 2));

            var result = await service.DownloadAsync(release);
            downloadedPath = result.FilePath;

            Assert.True(File.Exists(result.FilePath));
            Assert.Equal(hash, result.Sha256);
            Assert.Equal(packageBytes, await File.ReadAllBytesAsync(result.FilePath));
        }
        finally
        {
            if (downloadedPath is not null && File.Exists(downloadedPath))
            {
                File.Delete(downloadedPath);
                var parent = Directory.GetParent(downloadedPath)?.FullName;
                if (parent is not null && Directory.Exists(parent))
                {
                    Directory.Delete(parent, recursive: true);
                }
            }

            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static string CreateDataDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "QingSnap.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
