using QingSnap.App.Models;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace QingSnap.App.Services;

public sealed class OcrService : IDisposable
{
    private const int AdvancedSegmentHeight = 2400;
    private const int AdvancedSegmentationThreshold = 3200;
    private const int SegmentOverlap = 120;

    private readonly AppSettingsService _settingsService;
    private readonly OcrModelManager _modelManager;
    private readonly OcrRuntimeManager _runtimeManager;
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private readonly object _warmupSync = new();
    private readonly ConditionalWeakTable<BitmapSource, OcrCacheEntry> _resultCache = new();
    private readonly OcrResultCache _contentCache = new();
    private readonly System.Threading.Timer _idleReleaseTimer;
    private IAdvancedOcrRuntime? _advancedEngine;
    private string? _loadedModel;
    private Task? _warmupTask;
    private DateTime _lastAdvancedUseUtc = DateTime.UtcNow;
    private bool _disposed;

    public OcrService(AppSettingsService settingsService)
    {
        _settingsService = settingsService;
        _modelManager = new OcrModelManager(settingsService.DataDirectory);
        _runtimeManager = new OcrRuntimeManager(settingsService.DataDirectory);
        _idleReleaseTimer = new System.Threading.Timer(
            _ => _ = ReleaseIdleEngineAsync(),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    public string SelectedModel => OcrModelManager.NormalizeModel(_settingsService.Current.OcrModel);

    public bool IsRuntimeInstalled =>
        AdvancedOcrRuntimeLoader.IsAvailable(_settingsService.DataDirectory);

    public bool IsSelectedModelInstalled => _modelManager.IsInstalled(SelectedModel);

    public bool IsOcrAvailable =>
        SelectedModel != OcrModelManager.NoModel &&
        IsRuntimeInstalled &&
        IsSelectedModelInstalled;

    public bool UsesAdvancedEngine => IsOcrAvailable;

    public string RuntimeDirectory => _runtimeManager.RuntimeDirectory;

    public long RuntimeInstalledSize => _runtimeManager.InstalledSize;

    public string? FindLocalRuntimePackage() => _runtimeManager.FindLocalPackage();

    public string GetModelDirectory(string model) => _modelManager.GetModelDirectory(model);

    public long GetModelDownloadSize(string model) => _modelManager.GetDownloadSize(model);

    public bool IsModelInstalled(string model) => _modelManager.IsInstalled(model);

    public IReadOnlyList<string> GetInstalledModels() => _modelManager.GetInstalledModels();

    public Task InstallModelAsync(
        string model,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _modelManager.EnsureInstalledAsync(model, progress, cancellationToken);

    public async Task InstallRuntimeAsync(
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await ReleaseAdvancedEngineAsync(true, cancellationToken).ConfigureAwait(false);
        await _runtimeManager.DownloadAndInstallAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplySettingsAsync(CancellationToken cancellationToken = default)
    {
        _resultCache.Clear();
        _contentCache.Clear();
        if (!IsOcrAvailable)
        {
            await ReleaseAdvancedEngineAsync(true, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(_loadedModel, SelectedModel, StringComparison.OrdinalIgnoreCase))
        {
            await ReleaseAdvancedEngineAsync(true, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(_settingsService.Current.OcrPerformanceMode, "Instant", StringComparison.OrdinalIgnoreCase))
        {
            await WarmUpAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (string.Equals(_settingsService.Current.OcrPerformanceMode, "Balanced", StringComparison.OrdinalIgnoreCase) ||
            !IsOcrAvailable)
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

    public async Task DeleteModelAsync(string model, CancellationToken cancellationToken = default)
    {
        await _engineLock.WaitAsync(cancellationToken);
        try
        {
            _advancedEngine?.Dispose();
            _advancedEngine = null;
            _loadedModel = null;
            lock (_warmupSync)
            {
                _warmupTask = null;
            }
            await _modelManager.DeleteAsync(model, cancellationToken);
        }
        finally
        {
            _engineLock.Release();
        }
    }

    public async Task DeleteAllOcrAsync(CancellationToken cancellationToken = default)
    {
        await ReleaseAdvancedEngineAsync(true, cancellationToken).ConfigureAwait(false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await _modelManager.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
        await _runtimeManager.DeleteAsync(cancellationToken).ConfigureAwait(false);
        _resultCache.Clear();
        _contentCache.Clear();
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

    public Task<OcrRecognitionResult> RecognizeFastAsync(
        BitmapSource source,
        CancellationToken cancellationToken = default) =>
        RecognizeAsync(source, cancellationToken, includeWordBoxes: false);

    private async Task<OcrRecognitionResult> RecognizeCoreAsync(
        BitmapSource source,
        CancellationToken cancellationToken,
        IProgress<OcrProgress>? progress,
        bool includeWordBoxes)
    {
        if (!IsOcrAvailable)
        {
            throw new InvalidOperationException(
                "OCR 尚未安装。请打开设置，在“OCR 组件”中安装运行库并选择 Tiny 或 Small 模型。");
        }

        progress?.Report(new OcrProgress($"正在使用 {OcrModelManager.GetDisplayName(SelectedModel)} 识别…"));
        return await RecognizeAdvancedAsync(source, cancellationToken, progress, includeWordBoxes);
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
            OcrModelManager.GetDisplayName(SelectedModel),
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

    private void EnsureAdvancedEngine()
    {
        if (_advancedEngine is not null)
        {
            return;
        }

        _advancedEngine = AdvancedOcrRuntimeLoader.Create(_settingsService.DataDirectory);
        _advancedEngine.Initialize(_modelManager.GetPaths(SelectedModel));
        _loadedModel = SelectedModel;
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
                    _loadedModel = null;
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
