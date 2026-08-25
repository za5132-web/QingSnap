using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class OcrModelManager
{
    public const string NoModel = "None";
    public const string TinyModel = "Tiny";
    public const string SmallModel = "Small";

    private static readonly HttpClient Client = CreateHttpClient();
    private static readonly ModelFile Classification = new(
        "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
        1_018_508,
        "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7",
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
    private static readonly ModelFile TinyDictionary = new(
        "ppocrv6_tiny_dict.txt",
        27_156,
        "c5cbe34ef40c29c4df07ed012bf96569cb69a2d2a01a07027e9f13cb832bd9cd",
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/paddle/PP-OCRv6/rec/PP-OCRv6_rec_tiny/ppocrv6_tiny_dict.txt");
    private static readonly ModelFile SmallDictionary = new(
        "ppocrv6_dict.txt",
        74_947,
        "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d",
        "https://cdn.jsdelivr.net/gh/BobLd/RapidOcrNet@master/RapidOcrNet/models/v6/ppocrv6_dict.txt",
        "https://raw.githubusercontent.com/BobLd/RapidOcrNet/master/RapidOcrNet/models/v6/ppocrv6_dict.txt");
    private static readonly IReadOnlyDictionary<string, ModelDefinition> Models =
        new Dictionary<string, ModelDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [TinyModel] = new(
                TinyModel,
                "PP-OCRv6 Tiny",
                new ModelFile(
                    "PP-OCRv6_det_tiny.onnx",
                    1_829_618,
                    "f42c0fbd294d95eac1a550e131b277dac97462c8025fa4b6c3cec1b7894bd3d5",
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/det/PP-OCRv6_det_tiny.onnx"),
                new ModelFile(
                    "PP-OCRv6_rec_tiny.onnx",
                    4_489_813,
                    "e16e242de5937ad92609223f19bc2aff3727ee40b095f996907c24749bad251b",
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/rec/PP-OCRv6_rec_tiny.onnx"),
                Classification,
                TinyDictionary),
            [SmallModel] = new(
                SmallModel,
                "PP-OCRv6 Small",
                new ModelFile(
                    "PP-OCRv6_det_small.onnx",
                    9_929_594,
                    "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f",
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx"),
                new ModelFile(
                    "PP-OCRv6_rec_small.onnx",
                    21_234_383,
                    "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884",
                    "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx"),
                Classification,
                SmallDictionary)
        };

    private readonly SemaphoreSlim _installLock = new(1, 1);

    public OcrModelManager(string? dataDirectory = null)
    {
        var resolvedDataDirectory = dataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QingSnap");
        RootDirectory = Path.Combine(
            resolvedDataDirectory,
            "Ocr",
            "Models");
        TryMigrateLegacySmallModel(resolvedDataDirectory);
    }

    public string RootDirectory { get; }

    public static string NormalizeModel(string? model) => model?.Trim() switch
    {
        var value when string.Equals(value, TinyModel, StringComparison.OrdinalIgnoreCase) => TinyModel,
        var value when string.Equals(value, SmallModel, StringComparison.OrdinalIgnoreCase) => SmallModel,
        _ => NoModel
    };

    public static string GetDisplayName(string? model) =>
        Models.TryGetValue(NormalizeModel(model), out var definition)
            ? definition.DisplayName
            : "未安装";

    public string GetModelDirectory(string model) =>
        Path.Combine(RootDirectory, $"PP-OCRv6-{NormalizeRequiredModel(model).ToLowerInvariant()}");

    public long GetDownloadSize(string model) =>
        GetDefinition(model).Files.Sum(file => file.Length);

    public bool IsInstalled(string model)
    {
        if (!Models.TryGetValue(NormalizeModel(model), out var definition))
        {
            return false;
        }

        var directory = GetModelDirectory(definition.Key);
        return definition.Files.All(file =>
        {
            var info = new FileInfo(Path.Combine(directory, file.Name));
            return info.Exists && info.Length == file.Length;
        });
    }

    public IReadOnlyList<string> GetInstalledModels() =>
        Models.Keys.Where(IsInstalled).ToArray();

    public OcrModelPaths GetPaths(string model)
    {
        var definition = GetDefinition(model);
        if (!IsInstalled(definition.Key))
        {
            throw new InvalidOperationException($"{definition.DisplayName} 模型尚未安装。");
        }

        var directory = GetModelDirectory(definition.Key);
        return new OcrModelPaths(
            definition.Key,
            Path.Combine(directory, definition.Files[0].Name),
            Path.Combine(directory, definition.Files[1].Name),
            Path.Combine(directory, definition.Files[2].Name),
            Path.Combine(directory, definition.Files[3].Name));
    }

    public async Task EnsureInstalledAsync(
        string model,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var definition = GetDefinition(model);
        var downloadSize = definition.Files.Sum(file => file.Length);
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            var directory = GetModelDirectory(definition.Key);
            Directory.CreateDirectory(directory);
            long completedBytes = 0;
            foreach (var file in definition.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(directory, file.Name);
                if (await IsValidAsync(destination, file, cancellationToken))
                {
                    completedBytes += file.Length;
                    continue;
                }

                progress?.Report(new OcrProgress(
                    $"正在下载 {definition.DisplayName} · {FormatBytes(completedBytes)} / {FormatBytes(downloadSize)}",
                    completedBytes / (double)downloadSize));
                await DownloadAsync(
                    definition,
                    file,
                    destination,
                    completedBytes,
                    downloadSize,
                    progress,
                    cancellationToken);
                completedBytes += file.Length;
            }

            if (string.Equals(definition.Key, TinyModel, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(Path.Combine(directory, SmallDictionary.Name));
            }

            progress?.Report(new OcrProgress($"{definition.DisplayName} 已就绪", 1));
        }
        finally
        {
            _installLock.Release();
        }
    }

    public async Task DeleteAsync(string model, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeRequiredModel(model);
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            var directory = GetModelDirectory(normalized);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        finally
        {
            _installLock.Release();
        }
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, true);
            }
        }
        finally
        {
            _installLock.Release();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QingSnap/1.0 (+https://github.com/)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream,*/*;q=0.8");
        return client;
    }

    private static ModelDefinition GetDefinition(string model)
    {
        var normalized = NormalizeRequiredModel(model);
        return Models[normalized];
    }

    private static string NormalizeRequiredModel(string model)
    {
        var normalized = NormalizeModel(model);
        return normalized == NoModel
            ? throw new InvalidOperationException("请先选择要安装的 OCR 模型。")
            : normalized;
    }

    private static async Task DownloadAsync(
        ModelDefinition definition,
        ModelFile file,
        string destination,
        long completedBeforeFile,
        long downloadSize,
        IProgress<OcrProgress>? progress,
        CancellationToken cancellationToken)
    {
        var temporaryPath = destination + ".download";
        Exception? lastError = null;
        foreach (var url in file.Urls)
        {
            try
            {
                TryDelete(temporaryPath);
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
                    var current = Math.Min(downloadSize, completedBeforeFile + fileBytes);
                    var percent = (int)Math.Floor(current * 100D / downloadSize);
                    if (percent != lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        progress?.Report(new OcrProgress(
                            $"正在下载 {definition.DisplayName} · {FormatBytes(current)} / {FormatBytes(downloadSize)}",
                            current / (double)downloadSize));
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

    private void TryMigrateLegacySmallModel(string dataDirectory)
    {
        var legacyDirectory = Path.Combine(dataDirectory, "Models", "PP-OCRv6-small");
        var currentDirectory = Path.Combine(RootDirectory, "PP-OCRv6-small");
        if (!Directory.Exists(legacyDirectory) || Directory.Exists(currentDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.Move(legacyDirectory, currentDirectory);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

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

    private sealed record ModelDefinition(
        string Key,
        string DisplayName,
        params ModelFile[] Files);

    private sealed record ModelFile(string Name, long Length, string Sha256, params string[] Urls);
}

public sealed record OcrModelPaths(
    string ModelVariant,
    string Detection,
    string Recognition,
    string Classification,
    string Dictionary);
