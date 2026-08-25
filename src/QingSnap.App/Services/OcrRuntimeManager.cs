using System.IO;
using System.IO.Compression;
using System.Net.Http;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class OcrRuntimeManager
{
    public const string PackageFileName = "QingSnap-OCR-Module-v1.0.0.zip";
    public const string PackagePattern = "QingSnap-OCR-Module-*.zip";

    private static readonly HttpClient Client = CreateHttpClient();
    private static readonly string[] PackageUrls =
    [
        "https://github.com/za5132-web/QingSnap/releases/download/v1.0.0/QingSnap-OCR-Module-v1.0.0.zip"
    ];

    private static readonly string[] RequiredFiles =
    [
        "QingSnap.AdvancedOcr.dll",
        "QingSnap.AdvancedOcr.deps.json",
        "Clipper2Lib.dll",
        "RapidOcrNet.dll",
        "Microsoft.ML.OnnxRuntime.dll",
        "onnxruntime.dll",
        "onnxruntime_providers_shared.dll",
        "SkiaSharp.dll",
        "libSkiaSharp.dll",
        "System.Numerics.Tensors.dll"
    ];

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly string _dataDirectory;

    public OcrRuntimeManager(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        RuntimeDirectory = AdvancedOcrRuntimeLoader.GetRuntimeDirectory(_dataDirectory);
    }

    public string RuntimeDirectory { get; }

    public bool IsInstalled => AdvancedOcrRuntimeLoader.IsAvailable(_dataDirectory);

    public long InstalledSize => !Directory.Exists(ActiveRuntimeDirectory)
        ? 0
        : Directory.EnumerateFiles(ActiveRuntimeDirectory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

    private string ActiveRuntimeDirectory => Directory.Exists(RuntimeDirectory)
        ? RuntimeDirectory
        : AdvancedOcrRuntimeLoader.GetLegacyRuntimeDirectory();

    public string? FindLocalPackage() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, PackagePattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    public async Task DownloadAndInstallAsync(
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var localPackage = FindLocalPackage();
        if (localPackage is not null)
        {
            progress?.Report(new OcrProgress("已找到本地 OCR 模块，正在安装…", 0.05));
            await InstallAsync(
                localPackage,
                ScaleProgress(progress, 0.08, 0.92),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"QingSnap-OCR-Module-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadPackageAsync(temporaryPath, progress, cancellationToken).ConfigureAwait(false);
            await InstallAsync(
                temporaryPath,
                ScaleProgress(progress, 0.72, 0.28),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public async Task InstallAsync(
        string packagePath,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("找不到 OCR 运行库安装包。", packagePath);
        }

        await _operationLock.WaitAsync(cancellationToken);
        var parentDirectory = Path.GetDirectoryName(RuntimeDirectory)
            ?? throw new InvalidOperationException("OCR 运行库目录无效。");
        var stagingDirectory = Path.Combine(parentDirectory, $"Runtime.installing-{Guid.NewGuid():N}");
        var backupDirectory = Path.Combine(parentDirectory, $"Runtime.backup-{Guid.NewGuid():N}");
        try
        {
            progress?.Report(new OcrProgress("正在校验 OCR 运行库…", 0.08));
            Directory.CreateDirectory(stagingDirectory);
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                var stagingRoot = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar) +
                                  Path.DirectorySeparatorChar;
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
                    if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("OCR 运行库安装包包含无效路径。");
                    }
                }

                progress?.Report(new OcrProgress("正在安装 OCR 运行库…", 0.24));
                archive.ExtractToDirectory(stagingDirectory, true);
            }

            ValidateRuntime(stagingDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(RuntimeDirectory))
            {
                Directory.Move(RuntimeDirectory, backupDirectory);
            }

            try
            {
                Directory.Move(stagingDirectory, RuntimeDirectory);
                TryDeleteDirectory(backupDirectory);
            }
            catch
            {
                if (Directory.Exists(backupDirectory) && !Directory.Exists(RuntimeDirectory))
                {
                    Directory.Move(backupDirectory, RuntimeDirectory);
                }

                throw;
            }
            progress?.Report(new OcrProgress("OCR 运行库已安装", 1));
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
            TryDeleteDirectory(backupDirectory);
            _operationLock.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(RuntimeDirectory))
            {
                Directory.Delete(RuntimeDirectory, true);
            }

            var legacyDirectory = AdvancedOcrRuntimeLoader.GetLegacyRuntimeDirectory();
            if (!string.Equals(legacyDirectory, RuntimeDirectory, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(legacyDirectory))
            {
                Directory.Delete(legacyDirectory, true);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private static void ValidateRuntime(string directory)
    {
        var missing = RequiredFiles
            .Where(file => !File.Exists(Path.Combine(directory, file)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"OCR 运行库安装包不完整：缺少 {string.Join("、", missing)}。");
        }
    }

    private static async Task DownloadPackageAsync(
        string destination,
        IProgress<OcrProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var url in PackageUrls)
        {
            try
            {
                progress?.Report(new OcrProgress("正在下载 OCR 运行库…", 0.02));
                using var response = await Client.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(
                    destination,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[128 * 1024];
                long completedBytes = 0;
                var lastPercent = -1;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    completedBytes += count;
                    var percent = totalBytes is > 0
                        ? (int)Math.Floor(completedBytes * 100D / totalBytes.Value)
                        : -1;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(new OcrProgress(
                            totalBytes is > 0
                                ? $"正在下载 OCR 运行库 · {completedBytes / 1024D / 1024D:0.0} / {totalBytes.Value / 1024D / 1024D:0.0} MB"
                                : $"正在下载 OCR 运行库 · {completedBytes / 1024D / 1024D:0.0} MB",
                            totalBytes is > 0
                                ? Math.Min(0.7, completedBytes / (double)totalBytes.Value * 0.7)
                                : null));
                    }
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (new FileInfo(destination).Length == 0)
                {
                    throw new InvalidDataException("下载的 OCR 运行库为空。");
                }

                return;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = exception;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
            {
                lastError = exception;
            }
        }

        throw new InvalidOperationException("OCR 运行库下载失败，请检查网络连接后重试。", lastError);
    }

    private static IProgress<OcrProgress>? ScaleProgress(
        IProgress<OcrProgress>? progress,
        double offset,
        double scale) => progress is null
        ? null
        : new Progress<OcrProgress>(value => progress.Report(value with
        {
            Percent = value.Percent is double percent
                ? Math.Clamp(offset + percent * scale, 0, 1)
                : null
        }));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QingSnap/0.47 (+https://github.com/za5132-web/QingSnap)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/zip,application/octet-stream;q=0.9,*/*;q=0.5");
        return client;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
