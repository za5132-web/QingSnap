using QingSnap.App.Models;
using RapidOcrNet;
using SkiaSharp;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using WindowsBitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WindowsOcrEngine = Windows.Media.Ocr.OcrEngine;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;

namespace QingSnap.App.Services;

public sealed class OcrService : IDisposable
{
    private const int AdvancedSegmentHeight = 2400;
    private const int AdvancedSegmentationThreshold = 3200;
    private const int SegmentOverlap = 120;

    private readonly AppSettingsService _settingsService;
    private readonly OcrModelManager _modelManager = new();
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private readonly object _warmupSync = new();
    private readonly ConditionalWeakTable<BitmapSource, OcrCacheEntry> _resultCache = new();
    private RapidOcr? _advancedEngine;
    private Task? _warmupTask;

    public OcrService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public bool AreAdvancedModelsInstalled => _modelManager.IsInstalled;

    public bool UsesAdvancedEngine =>
        !string.Equals(_settingsService.Current.OcrEngine, "Windows", StringComparison.OrdinalIgnoreCase);

    public string AdvancedModelDirectory => _modelManager.ModelDirectory;

    public long AdvancedModelDownloadSize => _modelManager.DownloadSize;

    public Task InstallAdvancedModelsAsync(
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _modelManager.EnsureInstalledAsync(progress, cancellationToken);

    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (string.Equals(_settingsService.Current.OcrEngine, "Windows", StringComparison.OrdinalIgnoreCase) ||
            !_modelManager.IsInstalled)
        {
            return Task.CompletedTask;
        }

        lock (_warmupSync)
        {
            if (_advancedEngine is not null)
            {
                return Task.CompletedTask;
            }

            return _warmupTask ??= WarmUpCoreAsync(cancellationToken);
        }
    }

    private async Task WarmUpCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureAdvancedEngine();
                using var warmupBitmap = new SKBitmap(96, 48, SKColorType.Bgra8888, SKAlphaType.Premul);
                warmupBitmap.Erase(SKColors.White);
                await _advancedEngine!.DetectAsync(
                    warmupBitmap,
                    CreateAdvancedOptions(false),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _engineLock.Release();
            }
        }
        catch
        {
            lock (_warmupSync)
            {
                _warmupTask = null;
            }
        }
    }

    public async Task DeleteAdvancedModelsAsync(CancellationToken cancellationToken = default)
    {
        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            _advancedEngine?.Dispose();
            _advancedEngine = null;
            lock (_warmupSync)
            {
                _warmupTask = null;
            }
            await _modelManager.DeleteAsync(cancellationToken);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    public async Task<OcrRecognitionResult> RecognizeAsync(
        BitmapSource source,
        CancellationToken cancellationToken = default,
        IProgress<OcrProgress>? progress = null,
        bool includeWordBoxes = true)
    {
        var cacheEntry = _resultCache.GetOrCreateValue(source);
        var cached = includeWordBoxes ? cacheEntry.Detailed : cacheEntry.Basic ?? cacheEntry.Detailed;
        if (cached is not null)
        {
            return cached;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = await RecognizeCoreAsync(source, cancellationToken, progress, includeWordBoxes);
        stopwatch.Stop();
        result = result with { ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds };
        if (includeWordBoxes)
        {
            cacheEntry.Detailed = result;
        }
        else
        {
            cacheEntry.Basic = result;
        }

        return result;
    }

    public async Task<OcrRecognitionResult> RecognizeFastAsync(
        BitmapSource source,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await RecognizeWindowsAsync(source, cancellationToken);
        stopwatch.Stop();
        return result with { ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds };
    }

    private async Task<OcrRecognitionResult> RecognizeCoreAsync(
        BitmapSource source,
        CancellationToken cancellationToken,
        IProgress<OcrProgress>? progress,
        bool includeWordBoxes)
    {
        if (string.Equals(_settingsService.Current.OcrEngine, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new OcrProgress("正在使用 Windows OCR…"));
            return await RecognizeWindowsAsync(source, cancellationToken);
        }

        try
        {
            if (!_modelManager.IsInstalled)
            {
                progress?.Report(new OcrProgress("首次使用，正在准备高精度 OCR…", 0));
            }

            if (!_modelManager.IsInstalled)
            {
                await _modelManager.EnsureInstalledAsync(progress, cancellationToken);
            }
            progress?.Report(new OcrProgress("正在使用 PP-OCRv6 识别…"));
            return await RecognizeAdvancedAsync(source, cancellationToken, progress, includeWordBoxes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            progress?.Report(new OcrProgress("高精度 OCR 暂不可用，已切换 Windows OCR"));
            var fallback = await RecognizeWindowsAsync(source, cancellationToken);
            return fallback with
            {
                LanguageName = $"{fallback.LanguageName}（自动回退）"
            };
        }
    }

    private async Task<OcrRecognitionResult> RecognizeAdvancedAsync(
        BitmapSource source,
        CancellationToken cancellationToken,
        IProgress<OcrProgress>? progress,
        bool includeWordBoxes)
    {
        var sourceWidth = source.PixelWidth;
        var sourceHeight = source.PixelHeight;
        if (sourceHeight <= AdvancedSegmentationThreshold || sourceHeight <= sourceWidth * 1.35)
        {
            return await RecognizeAdvancedSegmentAsync(source, cancellationToken, progress, includeWordBoxes);
        }

        var step = AdvancedSegmentHeight - SegmentOverlap;
        var collectedLines = new List<OcrTextLine>();
        var nextWordIndex = 0;
        var segmentNumber = 0;
        var segmentCount = (int)Math.Ceiling((sourceHeight - SegmentOverlap) / (double)step);
        for (var top = 0; top < sourceHeight; top += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segmentNumber++;
            progress?.Report(new OcrProgress($"正在识别长图 · {segmentNumber} / {segmentCount}"));
            var height = Math.Min(AdvancedSegmentHeight, sourceHeight - top);
            var segment = new CroppedBitmap(
                source,
                new System.Windows.Int32Rect(0, top, sourceWidth, height));
            segment.Freeze();
            var result = await RecognizeAdvancedSegmentAsync(segment, cancellationToken, null, includeWordBoxes);
            var isFirst = top == 0;
            var isLast = top + height >= sourceHeight;
            foreach (var line in result.Lines)
            {
                var centerY = line.Bounds.Y + line.Bounds.Height / 2;
                if ((!isFirst && centerY < SegmentOverlap / 2D) ||
                    (!isLast && centerY > height - SegmentOverlap / 2D))
                {
                    continue;
                }

                var lineIndex = collectedLines.Count;
                var words = line.Words.Select(word => new OcrTextWord(
                    nextWordIndex++,
                    lineIndex,
                    word.Text,
                    word.Bounds with { Y = word.Bounds.Y + top })).ToArray();
                collectedLines.Add(new OcrTextLine(
                    lineIndex,
                    line.Text,
                    line.Bounds with { Y = line.Bounds.Y + top },
                    words));
            }

            if (isLast)
            {
                break;
            }
        }

        return BuildResult(
            collectedLines,
            "PP-OCRv6 Small",
            "离线 · 多语言",
            sourceWidth,
            sourceHeight,
            sourceWidth,
            sourceHeight);
    }

    private async Task<OcrRecognitionResult> RecognizeAdvancedSegmentAsync(
        BitmapSource source,
        CancellationToken cancellationToken,
        IProgress<OcrProgress>? progress,
        bool includeWordBoxes)
    {
        using var bitmap = EncodeSkBitmap(source);
        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            EnsureAdvancedEngine();

            var lineProgress = progress is null
                ? null
                : new Progress<(int Completed, int Total)>(value =>
                    progress.Report(new OcrProgress(
                        value.Total > 0
                            ? $"正在识别文字 · {value.Completed} / {value.Total} 行"
                            : "正在识别文字…")));
            var result = await _advancedEngine!.DetectAsync(
                bitmap,
                CreateAdvancedOptions(includeWordBoxes),
                lineProgress,
                cancellationToken);

            var lineIndex = 0;
            var wordIndex = 0;
            var lines = result.TextBlocks.Select(block =>
            {
                var currentLineIndex = lineIndex++;
                var words = block.WordResults?.Select(word => new OcrTextWord(
                    wordIndex++,
                    currentLineIndex,
                    word.Text,
                    BoundsFromPoints(word.BoxPoints))).ToArray() ?? [];
                return new OcrTextLine(
                    currentLineIndex,
                    block.Text,
                    BoundsFromPoints(block.BoxPoints),
                    words);
            }).ToArray();

            return BuildResult(
                lines,
                "PP-OCRv6 Small",
                "离线 · 多语言",
                source.PixelWidth,
                source.PixelHeight,
                source.PixelWidth,
                source.PixelHeight);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    private async Task<OcrRecognitionResult> RecognizeWindowsAsync(
        BitmapSource source,
        CancellationToken cancellationToken)
    {
        var sourceWidth = source.PixelWidth;
        var sourceHeight = source.PixelHeight;
        var maximumDimension = checked((int)WindowsOcrEngine.MaxImageDimension);
        var segmentHeight = Math.Min(maximumDimension, Math.Max(1800, sourceWidth * 4));
        if (sourceHeight <= segmentHeight)
        {
            return await RecognizeWindowsSegmentAsync(source, cancellationToken);
        }

        var overlap = Math.Min(SegmentOverlap, segmentHeight / 12);
        var step = segmentHeight - overlap;
        var collectedLines = new List<OcrTextLine>();
        string? languageName = null;
        string? languageTag = null;
        var nextWordIndex = 0;
        for (var top = 0; top < sourceHeight; top += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var height = Math.Min(segmentHeight, sourceHeight - top);
            var segment = new CroppedBitmap(source, new System.Windows.Int32Rect(0, top, sourceWidth, height));
            segment.Freeze();
            var result = await RecognizeWindowsSegmentAsync(segment, cancellationToken);
            languageName ??= result.LanguageName;
            languageTag ??= result.LanguageTag;
            var isFirst = top == 0;
            var isLast = top + height >= sourceHeight;
            foreach (var line in result.Lines)
            {
                var centerY = line.Bounds.Y + line.Bounds.Height / 2;
                if ((!isFirst && centerY < overlap / 2D) ||
                    (!isLast && centerY > height - overlap / 2D))
                {
                    continue;
                }

                var lineIndex = collectedLines.Count;
                var words = line.Words.Select(word => new OcrTextWord(
                    nextWordIndex++,
                    lineIndex,
                    word.Text,
                    word.Bounds with { Y = word.Bounds.Y + top })).ToArray();
                collectedLines.Add(new OcrTextLine(
                    lineIndex,
                    line.Text,
                    line.Bounds with { Y = line.Bounds.Y + top },
                    words));
            }

            if (isLast)
            {
                break;
            }
        }

        return BuildResult(
            collectedLines,
            languageName ?? "Windows OCR",
            languageTag ?? string.Empty,
            sourceWidth,
            sourceHeight,
            sourceWidth,
            sourceHeight);
    }

    private async Task<OcrRecognitionResult> RecognizeWindowsSegmentAsync(
        BitmapSource source,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"QingSnap-ocr-{Guid.NewGuid():N}.png");
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(WpfBitmapFrame.Create(source));
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             65536,
                             useAsync: true))
            {
                encoder.Save(stream);
                await stream.FlushAsync(cancellationToken);
            }

            return await RecognizeWindowsFileAsync(temporaryPath, cancellationToken);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task<OcrRecognitionResult> RecognizeWindowsFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = WindowsOcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "Windows 没有可用的 OCR 语言。请在系统语言设置中安装中文或英文语言功能。");
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await WindowsBitmapDecoder.CreateAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceWidth = checked((int)decoder.PixelWidth);
        var sourceHeight = checked((int)decoder.PixelHeight);
        var scale = Math.Min(
            1D,
            WindowsOcrEngine.MaxImageDimension / (double)Math.Max(sourceWidth, sourceHeight));
        var recognitionWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var recognitionHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = checked((uint)recognitionWidth),
            ScaledHeight = checked((uint)recognitionHeight)
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        var result = await engine.RecognizeAsync(bitmap);
        cancellationToken.ThrowIfCancellationRequested();
        var scaleX = sourceWidth / (double)Math.Max(1, recognitionWidth);
        var scaleY = sourceHeight / (double)Math.Max(1, recognitionHeight);
        var lineIndex = 0;
        var wordIndex = 0;
        var lines = result.Lines.Select(line =>
        {
            var currentLineIndex = lineIndex++;
            var words = line.Words.Select(word => new OcrTextWord(
                wordIndex++,
                currentLineIndex,
                word.Text,
                new OcrTextBounds(
                    word.BoundingRect.X * scaleX,
                    word.BoundingRect.Y * scaleY,
                    word.BoundingRect.Width * scaleX,
                    word.BoundingRect.Height * scaleY))).ToArray();
            return new OcrTextLine(currentLineIndex, line.Text, CombineBounds(words.Select(word => word.Bounds)), words);
        }).ToArray();
        return BuildResult(
            lines,
            engine.RecognizerLanguage.DisplayName,
            engine.RecognizerLanguage.LanguageTag,
            sourceWidth,
            sourceHeight,
            recognitionWidth,
            recognitionHeight);
    }

    private static SKBitmap EncodeSkBitmap(BitmapSource source)
    {
        BitmapSource bgraSource = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(bgraSource.PixelWidth * 4);
        var pixels = new byte[checked(stride * bgraSource.PixelHeight)];
        bgraSource.CopyPixels(pixels, stride, 0);
        var bitmap = new SKBitmap(
            bgraSource.PixelWidth,
            bgraSource.PixelHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    private void EnsureAdvancedEngine()
    {
        if (_advancedEngine is not null)
        {
            return;
        }

        var paths = _modelManager.GetPaths();
        _advancedEngine = new RapidOcr();
        var inferenceThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        using var sessionOptions = RapidOcr.GetDefaultSessionOptions(inferenceThreads);
        sessionOptions.EnableCpuMemArena = false;
        sessionOptions.EnableMemoryPattern = false;
        _advancedEngine.InitModels(RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = paths.Detection,
            RecModelPath = paths.Recognition,
            ClsModelPath = paths.Classification,
            KeysPath = paths.Dictionary
        }, sessionOptions);
    }

    private static RapidOcrOptions CreateAdvancedOptions(bool includeWordBoxes) =>
        RapidOcrOptions.PPOCRv6 with
        {
            LimitSideLen = 384,
            DoAngle = false,
            ReturnWordBox = includeWordBoxes,
            ReturnSingleCharBox = false
        };

    private static OcrTextBounds BoundsFromPoints(IEnumerable<SKPointI> points)
    {
        var items = points.ToArray();
        if (items.Length == 0)
        {
            return new OcrTextBounds(0, 0, 0, 0);
        }

        var left = items.Min(point => point.X);
        var top = items.Min(point => point.Y);
        var right = items.Max(point => point.X);
        var bottom = items.Max(point => point.Y);
        return new OcrTextBounds(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static OcrRecognitionResult BuildResult(
        IReadOnlyList<OcrTextLine> lines,
        string languageName,
        string languageTag,
        int sourceWidth,
        int sourceHeight,
        int recognitionWidth,
        int recognitionHeight) =>
        new(
            string.Join(Environment.NewLine, lines.Select(line => line.Text)).Trim(),
            languageName,
            languageTag,
            lines.Count,
            sourceWidth,
            sourceHeight,
            recognitionWidth,
            recognitionHeight,
            lines);

    private static OcrTextBounds CombineBounds(IEnumerable<OcrTextBounds> bounds)
    {
        var items = bounds.ToArray();
        if (items.Length == 0)
        {
            return new OcrTextBounds(0, 0, 0, 0);
        }

        var left = items.Min(item => item.X);
        var top = items.Min(item => item.Y);
        var right = items.Max(item => item.Right);
        var bottom = items.Max(item => item.Bottom);
        return new OcrTextBounds(left, top, right - left, bottom - top);
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

    public void Dispose()
    {
        _advancedEngine?.Dispose();
        _engineLock.Dispose();
    }

    private sealed class OcrCacheEntry
    {
        public OcrRecognitionResult? Basic { get; set; }

        public OcrRecognitionResult? Detailed { get; set; }
    }
}
