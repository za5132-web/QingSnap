using System.IO;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class HistoryOcrIndexingService : IDisposable
{
    private sealed record WorkItem(
        string FilePath,
        Task<OcrRecognitionResult>? PrefetchedOcr);

    private readonly CaptureHistoryService _historyService;
    private readonly OcrService _ocrService;
    private readonly object _sync = new();
    private readonly Queue<WorkItem> _captureQueue = new();
    private readonly Queue<WorkItem> _backfillQueue = new();
    private readonly HashSet<string> _queuedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _workAvailable = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _indexedCount;
    private int _backfillScanRunning;
    private int _activeCount;
    private bool _disposed;

    public HistoryOcrIndexingService(
        CaptureHistoryService historyService,
        OcrService ocrService)
    {
        _historyService = historyService;
        _ocrService = ocrService;
        _worker = Task.Run(ProcessQueueAsync);
    }

    public event EventHandler<HistoryOcrIndexProgress>? ProgressChanged;

    public void EnqueueCapture(
        string filePath,
        Task<OcrRecognitionResult>? prefetchedOcr = null)
    {
        if (!_ocrService.IsOcrAvailable || _historyService.HasOcrIndex(filePath))
        {
            return;
        }

        Enqueue(new WorkItem(filePath, prefetchedOcr), isBackfill: false);
    }

    public void ScheduleBackfill()
    {
        if (_disposed)
        {
            return;
        }

        if (!_ocrService.IsOcrAvailable)
        {
            lock (_sync)
            {
                while (_captureQueue.TryDequeue(out var capture))
                {
                    _queuedPaths.Remove(capture.FilePath);
                }

                while (_backfillQueue.TryDequeue(out var backfill))
                {
                    _queuedPaths.Remove(backfill.FilePath);
                }
            }

            RaiseProgress(isOcrAvailable: false);
            return;
        }

        if (Interlocked.Exchange(ref _backfillScanRunning, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                foreach (var filePath in _historyService.FindImagesWithoutOcrIndex(_shutdown.Token))
                {
                    Enqueue(new WorkItem(filePath, null), isBackfill: true);
                }

                RaiseProgress();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error("HistoryOCR", exception, "Failed to scan history OCR backlog.");
            }
            finally
            {
                Interlocked.Exchange(ref _backfillScanRunning, 0);
            }
        });
    }

    private void Enqueue(WorkItem item, bool isBackfill)
    {
        lock (_sync)
        {
            if (_disposed || !_queuedPaths.Add(item.FilePath))
            {
                return;
            }

            if (isBackfill)
            {
                _backfillQueue.Enqueue(item);
            }
            else
            {
                _captureQueue.Enqueue(item);
            }
        }

        _workAvailable.Release();
        RaiseProgress();
    }

    private async Task ProcessQueueAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _workAvailable.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            WorkItem? item;
            lock (_sync)
            {
                item = _captureQueue.Count > 0
                    ? _captureQueue.Dequeue()
                    : _backfillQueue.Count > 0
                        ? _backfillQueue.Dequeue()
                        : null;
            }

            if (item is null)
            {
                continue;
            }

            Interlocked.Increment(ref _activeCount);
            RaiseProgress();
            string? recognizedText = null;
            var indexWritten = false;
            try
            {
                if (!_historyService.HasOcrIndex(item.FilePath) && File.Exists(item.FilePath))
                {
                    OcrRecognitionResult result;
                    try
                    {
                        result = item.PrefetchedOcr is not null
                            ? await item.PrefetchedOcr.ConfigureAwait(false)
                            : await RecognizeFileAsync(item.FilePath, _shutdown.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (item.PrefetchedOcr is not null &&
                                                     exception is not OperationCanceledException)
                    {
                        DiagnosticLog.Warning(
                            "HistoryOCR",
                            $"Prefetched OCR failed; retrying from history image: {exception.Message}");
                        result = await RecognizeFileAsync(item.FilePath, _shutdown.Token).ConfigureAwait(false);
                    }

                    recognizedText = result.Text;
                    _historyService.SaveOcrText(item.FilePath, recognizedText);
                    indexWritten = true;
                    Interlocked.Increment(ref _indexedCount);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error(
                    "HistoryOCR",
                    exception,
                    $"Failed to build OCR index for {Path.GetFileName(item.FilePath)}.");
            }
            finally
            {
                lock (_sync)
                {
                    _queuedPaths.Remove(item.FilePath);
                }

                Interlocked.Decrement(ref _activeCount);
                RaiseProgress(
                    indexWritten ? item.FilePath : null,
                    indexWritten ? recognizedText : null);
            }

            if (GetBackfillCount() > 0)
            {
                try
                {
                    await Task.Delay(120, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task<OcrRecognitionResult> RecognizeFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        if (!_ocrService.IsOcrAvailable)
        {
            throw new InvalidOperationException("OCR component is not available.");
        }

        var image = _historyService.LoadFullImage(filePath);
        return await _ocrService
            .RecognizeAsync(image, cancellationToken, includeWordBoxes: false)
            .ConfigureAwait(false);
    }

    private int GetBackfillCount()
    {
        lock (_sync)
        {
            return _backfillQueue.Count;
        }
    }

    private void RaiseProgress(
        string? completedFilePath = null,
        string? recognizedText = null,
        bool isOcrAvailable = true)
    {
        int pendingCount;
        lock (_sync)
        {
            pendingCount = _captureQueue.Count + _backfillQueue.Count + Volatile.Read(ref _activeCount);
        }

        ProgressChanged?.Invoke(
            this,
            new HistoryOcrIndexProgress(
                pendingCount,
                Volatile.Read(ref _indexedCount),
                completedFilePath,
                recognizedText,
                isOcrAvailable));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Cancel();
        _workAvailable.Release();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is AggregateException or OperationCanceledException)
        {
        }

        _workAvailable.Dispose();
        _shutdown.Dispose();
    }
}
