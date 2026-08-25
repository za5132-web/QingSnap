using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class OcrModelManager
{
    private static readonly HttpClient Client = CreateHttpClient();

    private static readonly ModelFile[] Files =
    [
        new(
            "PP-OCRv6_det_small.onnx",
            9_929_594,
            "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f",
            "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx"),
        new(
            "PP-OCRv6_rec_small.onnx",
            21_234_383,
            "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884",
            "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx"),
        new(
            "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
            1_018_508,
            "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7",
            "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"),
        new(
            "ppocrv6_dict.txt",
            74_947,
            "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d",
            "https://cdn.jsdelivr.net/gh/BobLd/RapidOcrNet@master/RapidOcrNet/models/v6/ppocrv6_dict.txt",
            "https://raw.githubusercontent.com/BobLd/RapidOcrNet/master/RapidOcrNet/models/v6/ppocrv6_dict.txt")
    ];

    private readonly SemaphoreSlim _installLock = new(1, 1);

    public OcrModelManager(string? dataDirectory = null)
    {
        ModelDirectory = Path.Combine(
            dataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QingSnap"),
            "Models",
            "PP-OCRv6-small");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QingSnap/1.0 (+https://github.com/)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream,*/*;q=0.8");
        return client;
    }

    public string ModelDirectory { get; }

    public long DownloadSize => Files.Sum(file => file.Length);

    public bool IsInstalled => Files.All(file =>
    {
        var info = new FileInfo(Path.Combine(ModelDirectory, file.Name));
        return info.Exists && info.Length == file.Length;
    });

    public OcrModelPaths GetPaths()
    {
        if (!IsInstalled)
        {
            throw new InvalidOperationException("高精度 OCR 模型尚未安装。");
        }

        return new OcrModelPaths(
            Path.Combine(ModelDirectory, Files[0].Name),
            Path.Combine(ModelDirectory, Files[1].Name),
            Path.Combine(ModelDirectory, Files[2].Name),
            Path.Combine(ModelDirectory, Files[3].Name));
    }

    public async Task EnsureInstalledAsync(
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(ModelDirectory);
            long completedBytes = 0;
            foreach (var file in Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(ModelDirectory, file.Name);
                if (await IsValidAsync(destination, file, cancellationToken))
                {
                    completedBytes += file.Length;
                    continue;
                }

                progress?.Report(new OcrProgress(
                    $"正在下载高精度模型 · {FormatBytes(completedBytes)} / {FormatBytes(DownloadSize)}",
                    completedBytes / (double)DownloadSize));
                await DownloadAsync(file, destination, completedBytes, progress, cancellationToken);
                completedBytes += file.Length;
            }

            progress?.Report(new OcrProgress("高精度 OCR 已就绪", 1));
        }
        finally
        {
            _installLock.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(ModelDirectory))
            {
                Directory.Delete(ModelDirectory, true);
            }
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task DownloadAsync(
        ModelFile file,
        string destination,
        long completedBeforeFile,
        IProgress<OcrProgress>? progress,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destination + ".download";
        Exception? lastError = null;
        foreach (var url in file.Urls)
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                using var response = await Client.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[128 * 1024];
                long fileBytes = 0;
                var lastReportedPercent = -1;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, cancellationToken);
                    if (count == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    fileBytes += count;
                    var current = Math.Min(DownloadSize, completedBeforeFile + fileBytes);
                    var percent = (int)Math.Floor(current * 100D / DownloadSize);
                    if (percent != lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        progress?.Report(new OcrProgress(
                            $"正在下载高精度模型 · {FormatBytes(current)} / {FormatBytes(DownloadSize)}",
                            current / (double)DownloadSize));
                    }
                }

                await target.FlushAsync(cancellationToken);
                target.Close();
                if (!await IsValidAsync(temporaryPath, file, cancellationToken))
                {
                    throw new InvalidDataException($"模型文件 {file.Name} 校验失败。");
                }

                File.Move(temporaryPath, destination, true);
                return;
            }
            catch (OperationCanceledException)
            {
                TryDelete(temporaryPath);
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
            {
                lastError = exception;
                TryDelete(temporaryPath);
            }
        }

        throw new InvalidOperationException($"无法下载模型文件 {file.Name}。", lastError);
    }

    private static async Task<bool> IsValidAsync(
        string path,
        ModelFile file,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != file.Length)
        {
            return false;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexString(hash), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes) => $"{bytes / 1024D / 1024D:0.0} MB";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ModelFile(string Name, long Length, string Sha256, params string[] Urls);
}

public sealed record OcrModelPaths(
    string Detection,
    string Recognition,
    string Classification,
    string Dictionary);
