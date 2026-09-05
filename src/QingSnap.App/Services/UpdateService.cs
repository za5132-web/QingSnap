using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed partial class UpdateService : IDisposable
{
    private const string RepositoryOwner = "za5132-web";
    private const string RepositoryName = "QingSnap";
    private static readonly Uri LatestReleaseApiUri =
        new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(12);

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string _statePath;
    private readonly Version _currentVersion;
    private bool _disposed;

    public UpdateService(string dataDirectory)
        : this(dataDirectory, CreateHttpClient(), ResolveCurrentVersion(), ownsClient: true)
    {
    }

    internal UpdateService(
        string dataDirectory,
        HttpClient client,
        Version currentVersion,
        bool ownsClient = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _ownsClient = ownsClient;
        Directory.CreateDirectory(dataDirectory);
        _statePath = Path.Combine(dataDirectory, "update-state.json");
    }

    public UpdateReleaseInfo? LastRelease { get; private set; }

    public string? LastDownloadedPackagePath { get; private set; }

    public Version CurrentVersion => _currentVersion;

    public string CurrentVersionDisplay => $"v{_currentVersion.ToString(3)}";

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!force)
        {
            var state = await LoadStateAsync(cancellationToken);
            if (state.LastAttemptUtc is { } lastAttempt &&
                DateTimeOffset.UtcNow - lastAttempt < AutomaticCheckInterval)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Skipped);
            }
        }

        await SaveStateAsync(new UpdateState(DateTimeOffset.UtcNow), cancellationToken);
        try
        {
            using var response = await _client.GetAsync(
                LatestReleaseApiUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var tagName = ReadRequiredString(root, "tag_name");
            if (!TryParseVersion(tagName, out var latestVersion))
            {
                throw new InvalidDataException($"GitHub Release 版本号无效：{tagName}");
            }

            var publishedAt = root.TryGetProperty("published_at", out var publishedElement) &&
                              publishedElement.ValueKind == JsonValueKind.String &&
                              DateTimeOffset.TryParse(
                                  publishedElement.GetString(),
                                  CultureInfo.InvariantCulture,
                                  DateTimeStyles.AssumeUniversal,
                                  out var parsedPublishedAt)
                ? parsedPublishedAt
                : DateTimeOffset.MinValue;
            var notes = root.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString() ?? string.Empty
                : string.Empty;
            var releasePage = root.TryGetProperty("html_url", out var pageElement) &&
                              Uri.TryCreate(pageElement.GetString(), UriKind.Absolute, out var parsedPage)
                ? parsedPage
                : new Uri($"https://github.com/{RepositoryOwner}/{RepositoryName}/releases");

            if (!TrySelectPackage(root, out var package))
            {
                DiagnosticLog.Warning("Update", $"Release {tagName} 没有可用的 QingSnap ZIP 附件。");
                return new UpdateCheckResult(
                    UpdateCheckStatus.NoCompatiblePackage,
                    Message: "最新发布中没有找到可下载的 QingSnap ZIP 文件。");
            }

            var expectedSha256 = TryExtractSha256(notes, package.Name);
            if (expectedSha256 is null)
            {
                expectedSha256 = await TryLoadChecksumAssetAsync(root, package.Name, cancellationToken);
            }

            var release = new UpdateReleaseInfo(
                tagName,
                latestVersion,
                publishedAt,
                notes,
                package.Name,
                package.DownloadUri,
                package.Size,
                expectedSha256,
                releasePage);
            LastRelease = release;
            var status = latestVersion > _currentVersion
                ? UpdateCheckStatus.UpdateAvailable
                : UpdateCheckStatus.UpToDate;
            DiagnosticLog.Info(
                "Update",
                $"版本检查完成：当前 {_currentVersion.ToString(3)}，最新 {latestVersion.ToString(3)}，状态 {status}。");
            return new UpdateCheckResult(
                status,
                release,
                expectedSha256 is null
                    ? "Release 未提供 SHA256，已禁止下载。"
                    : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Update", $"版本检查失败：{exception.GetType().Name}：{exception.Message}");
            return new UpdateCheckResult(
                UpdateCheckStatus.Error,
                Message: "暂时无法连接 GitHub，请稍后重试。");
        }
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateReleaseInfo release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(release);
        if (!release.CanDownload || release.ExpectedSha256 is null)
        {
            throw new InvalidOperationException("此版本没有可信的 SHA256，无法安全下载。");
        }

        EnsureTrustedDownloadUri(release.DownloadUri);
        var safeName = Path.GetFileName(release.PackageName);
        if (string.IsNullOrWhiteSpace(safeName) || !safeName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新包文件名无效。");
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            "QingSnap",
            "Updates",
            SanitizePathSegment(release.TagName));
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, safeName);
        var temporaryPath = finalPath + ".download";

        try
        {
            if (File.Exists(finalPath))
            {
                var existingHash = await ComputeSha256Async(finalPath, cancellationToken);
                if (HashesEqual(existingHash, release.ExpectedSha256))
                {
                    LastDownloadedPackagePath = finalPath;
                    progress?.Report(new UpdateDownloadProgress(release.PackageSize, release.PackageSize));
                    return new UpdateDownloadResult(finalPath, existingHash);
                }

                File.Delete(finalPath);
            }

            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            using var response = await _client.GetAsync(
                release.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? release.PackageSize;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long received = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new UpdateDownloadProgress(received, totalBytes));
                }
            }

            var actualSha256 = await ComputeSha256Async(temporaryPath, cancellationToken);
            if (!HashesEqual(actualSha256, release.ExpectedSha256))
            {
                File.Delete(temporaryPath);
                DiagnosticLog.Warning("Update", $"更新包校验失败：{safeName}。");
                throw new InvalidDataException("下载文件校验失败，错误文件已删除。");
            }

            File.Move(temporaryPath, finalPath, true);
            LastDownloadedPackagePath = finalPath;
            progress?.Report(new UpdateDownloadProgress(totalBytes, totalBytes));
            DiagnosticLog.Info("Update", $"更新包下载并校验完成：{safeName}。");
            return new UpdateDownloadResult(finalPath, actualSha256);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    public static void OpenContainingFolder(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("更新包不存在，请重新下载。", fullPath);
        }

        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        startInfo.ArgumentList.Add($"/select,{fullPath}");
        Process.Start(startInfo);
    }

    internal static bool TryParseVersion(string? value, out Version version)
    {
        var normalized = value?.Trim();
        if (!string.IsNullOrEmpty(normalized) &&
            normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var prereleaseIndex = normalized?.IndexOfAny(['-', '+']) ?? -1;
        if (prereleaseIndex >= 0)
        {
            normalized = normalized![..prereleaseIndex];
        }

        return Version.TryParse(normalized, out version!);
    }

    internal static string? TryExtractSha256(string? text, string packageName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var hashMatch = Sha256ValueRegex().Match(line);
            if (!hashMatch.Success)
            {
                continue;
            }

            if (line.Contains(packageName, StringComparison.OrdinalIgnoreCase) ||
                Sha256LabelRegex().IsMatch(line))
            {
                return hashMatch.Value.ToUpperInvariant();
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private async Task<string?> TryLoadChecksumAssetAsync(
        JsonElement root,
        string packageName,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            if (!IsChecksumAsset(name, packageName) ||
                !asset.TryGetProperty("browser_download_url", out var urlElement) ||
                !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri))
            {
                continue;
            }

            EnsureTrustedDownloadUri(uri);
            using var response = await _client.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var hash = TryExtractSha256(content, packageName) ?? Sha256ValueRegex().Match(content).Value;
            return hash.Length == 64 ? hash.ToUpperInvariant() : null;
        }

        return null;
    }

    private static bool TrySelectPackage(JsonElement root, out ReleaseAsset package)
    {
        package = default;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        ReleaseAsset? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                !asset.TryGetProperty("browser_download_url", out var urlElement) ||
                !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var downloadUri))
            {
                continue;
            }

            EnsureTrustedDownloadUri(downloadUri);
            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                ? Math.Max(0, parsedSize)
                : 0;
            var candidate = new ReleaseAsset(name, downloadUri, size);
            fallback ??= candidate;
            if (name.StartsWith("QingSnap", StringComparison.OrdinalIgnoreCase))
            {
                package = candidate;
                return true;
            }
        }

        if (fallback is { } value)
        {
            package = value;
            return true;
        }

        return false;
    }

    private async Task<UpdateState> LoadStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return new UpdateState(null);
            }

            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<UpdateState>(stream, cancellationToken: cancellationToken) ??
                   new UpdateState(null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Warning("Update", $"更新检查状态读取失败，将重新检查：{exception.Message}");
            return new UpdateState(null);
        }
    }

    private async Task SaveStateAsync(UpdateState state, CancellationToken cancellationToken)
    {
        try
        {
            var temporaryPath = _statePath + ".tmp";
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
            }

            File.Move(temporaryPath, _statePath, true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warning("Update", $"更新检查状态保存失败：{exception.Message}");
        }
    }

    private static string ReadRequiredString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? throw new InvalidDataException($"Release 字段 {propertyName} 为空。")
            : throw new InvalidDataException($"Release 缺少字段 {propertyName}。");

    private static bool IsChecksumAsset(string name, string packageName) =>
        name.Equals(packageName + ".sha256", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(packageName + ".sha256.txt", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase);

    private static void EnsureTrustedDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("更新下载地址不是可信的 GitHub HTTPS 地址。");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static bool HashesEqual(string first, string second) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(first),
            Convert.FromHexString(second));

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "latest" : sanitized;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warning("Update", $"无法删除未完成的更新文件：{exception.Message}");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QingSnap-Update/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static Version ResolveCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return TryParseVersion(informational, out var version)
            ? version
            : assembly.GetName().Version ?? new Version(0, 0, 0);
    }

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256ValueRegex();

    [GeneratedRegex("SHA[\\s-]?256\\s*[:：]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sha256LabelRegex();

    private readonly record struct ReleaseAsset(string Name, Uri DownloadUri, long Size);

    private sealed record UpdateState(DateTimeOffset? LastAttemptUtc);
}
