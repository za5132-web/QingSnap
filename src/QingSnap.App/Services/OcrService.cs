using QingSnap.App.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
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
    private readonly OcrModelManager _modelManager;
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private readonly object _warmupSync = new();
    private readonly ConditionalWeakTable<BitmapSource, OcrCacheEntry> _resultCache = new();
    private readonly OcrResultCache _contentCache = new();
    private readonly System.Threading.Timer _idleReleaseTimer;
    private IAdvancedOcrRuntime? _advancedEngine;
    private Task? _warmupTask;
    private DateTime _lastAdvancedUseUtc = DateTime.UtcNow;
    private bool _disposed;

    public OcrService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        _modelManager = new OcrModelManager(settingsService.DataDirectory);
        _idleReleaseTimer = new System.Threading.Timer(
            _ => _ = ReleaseIdleEngineAsync(),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    public bool AreAdvancedModelsInstalled => _modelManager.IsInstalled;

    public bool IsAdvancedRuntimeAvailable => AdvancedOcrRuntimeLoader.IsAvailable;

    public bool UsesAdvancedEngine =>
        IsAdvancedRuntimeAvailable &&
        !string.Equals(_settingsService.Current.OcrEngine, "Windows", StringComparison.OrdinalIgnoreCase);

    public string AdvancedModelDirectory => _modelManager.ModelDirectory;

    public long AdvancedModelDownloadSize => _modelManager.DownloadSize;

    public Task InstallAdvancedModelsAsync(
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _modelManager.EnsureInstalledAsync(progress, cancellationToken);

    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        _resultCache.Clear();
        _contentCache.Clear();
        if (!IsAdvancedRuntimeAvailable ||
            string.Equals(_settingsService.Current.OcrEngine, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            await ReleaseAdvancedEngineAsync(true, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(_settingsService.Current.OcrPerformanceMode, "Instant", StringComparison.OrdinalIgnoreCase))
        {
            await WarmUpAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (string.Equals(_settingsService.Current.OcrEngine, "Windows", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_settingsService.Current.OcrPerformanceMode, "Balanced", StringComparison.OrdinalIgnoreCase) ||
            !IsAdvancedRuntimeAvailable ||
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
                await _advancedEngine!.WarmUpAsync(cancellationToken).ConfigureAwait(false);
                _lastAdvancedUseUtc = DateTime.UtcNow;
                DiagnosticLog.Info("OCR", "Advanced engine warm-up completed.");
            }
            finally
            {
                _engineLock.Release();
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("OCR", exception, "Advanced engine warm-up failed.");
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
            DiagnosticLog.Info("OCR", "Object cache hit.");
            return cached;
        }

        var fingerprint = _contentCache.CreateFingerprint(source);
        if (fingerprint is { } key)
        {
            cached = _contentCache.TryGet(key, includeWordBoxes);
            if (cached is not null)
            {
                if (includeWordBoxes)
                {
                    cacheEntry.Detailed = cached;
                }
                else
                {
                    cacheEntry.Basic = cached;
                }

                DiagnosticLog.Info("OCR", "Content cache hit.");
                return cached;
            }
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

        if (fingerprint is { } completedKey)
        {
            _contentCache.Set(completedKey, includeWordBoxes, result);
        }

        DiagnosticLog.Info(
            "OCR",
            $"Recognition completed in {result.ElapsedMilliseconds:0.0} ms; lines={result.LineCount}; detailed={includeWordBoxes}.");

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
        if (!IsAdvancedRuntimeAvailable ||
            string.Equals(_settingsService.Current.OcrEngine, "Windows", StringComparison.OrdinalIgnoreCase))
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
        catch (Exception exception)
        {
            DiagnosticLog.Error("OCR", exception, "Advanced OCR failed; falling back to Windows OCR.");
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
        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            EnsureAdvancedEngine();
            var result = await _advancedEngine!.RecognizeAsync(
                source,
                includeWordBoxes,
                progress,
                cancellationToken).ConfigureAwait(false);
            _lastAdvancedUseUtc = DateTime.UtcNow;
            return result;
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

    private void EnsureAdvancedEngine()
    {
        if (_advancedEngine is not null)
        {
            return;
        }

        _advancedEngine = AdvancedOcrRuntimeLoader.Create();
        _advancedEngine.Initialize(_modelManager.GetPaths());
        _lastAdvancedUseUtc = DateTime.UtcNow;
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
        _disposed = true;
        _idleReleaseTimer.Dispose();
        _advancedEngine?.Dispose();
        _contentCache.Clear();
        _engineLock.Dispose();
    }

    private async Task ReleaseIdleEngineAsync()
    {
        if (_disposed ||
            !string.Equals(_settingsService.Current.OcrPerformanceMode, "Balanced", StringComparison.OrdinalIgnoreCase) ||
            DateTime.UtcNow - _lastAdvancedUseUtc < TimeSpan.FromMinutes(5) ||
            _advancedEngine is null)
        {
            return;
        }

        await ReleaseAdvancedEngineAsync(false, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ReleaseAdvancedEngineAsync(bool force, CancellationToken cancellationToken)
    {
        try
        {
            await _engineLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_advancedEngine is not null &&
                    (force || DateTime.UtcNow - _lastAdvancedUseUtc >= TimeSpan.FromMinutes(5)))
                {
                    _advancedEngine.Dispose();
                    _advancedEngine = null;
                    lock (_warmupSync)
                    {
                        _warmupTask = null;
                    }

                    DiagnosticLog.Info("OCR", force
                        ? "Advanced engine released after settings change."
                        : "Balanced mode released the idle advanced engine.");
                }
            }
            finally
            {
                _engineLock.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class OcrCacheEntry
    {
        public OcrRecognitionResult? Basic { get; set; }

        public OcrRecognitionResult? Detailed { get; set; }
    }
}
