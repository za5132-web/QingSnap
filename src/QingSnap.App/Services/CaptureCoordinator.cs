using System.IO;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Views;
using DrawingRectangle = System.Drawing.Rectangle;

namespace QingSnap.App.Services;

public sealed class CaptureCoordinator
{
    private readonly ScreenCaptureService _captureService;
    private readonly ClipboardService _clipboardService;
    private readonly AppStateStore _stateStore;
    private readonly CaptureHistoryService _historyService;
    private readonly HistoryOcrIndexingService _historyOcrIndexer;
    private readonly OcrService _ocrService;
    private readonly QrCodeService _qrCodeService;
    private readonly AppSettingsService _settingsService;
    private readonly PinHistoryBuffer _pinHistory = new();
    private readonly SemaphoreSlim _pinRequestGate = new(1, 1);
    private HistoryWindow? _historyWindow;
    private bool _isCapturing;
    private uint? _lastClipboardSequenceNumber;

    public CaptureCoordinator(
        ScreenCaptureService captureService,
        ClipboardService clipboardService,
        AppStateStore stateStore,
        CaptureHistoryService historyService,
        HistoryOcrIndexingService historyOcrIndexer,
        OcrService ocrService,
        QrCodeService qrCodeService,
        AppSettingsService settingsService)
    {
        _captureService = captureService;
        _clipboardService = clipboardService;
        _stateStore = stateStore;
        _historyService = historyService;
        _historyOcrIndexer = historyOcrIndexer;
        _ocrService = ocrService;
        _qrCodeService = qrCodeService;
        _settingsService = settingsService;
    }

    public event EventHandler<CaptureResult>? CaptureCompleted;
    public event EventHandler<string>? CaptureFailed;
    public event EventHandler<int>? CaptureDelayStarted;

    public async void StartRegionCapture()
    {
        if (_isCapturing)
        {
            return;
        }

        var delaySeconds = _settingsService.Current.CaptureDelaySeconds;
        ResourceDiagnostics.Sample("CaptureStarted", ("Recent", _pinHistory.Count));
        DiagnosticLog.Info("Capture", $"Region capture requested; delay={delaySeconds}s.");
        if (delaySeconds > 0)
        {
            _isCapturing = true;
            CaptureDelayStarted?.Invoke(this, delaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            _isCapturing = false;
        }

        OpenCaptureOverlay(null);
    }

    public void StartLongCapture() => StartLongCapture(LongCaptureMode.Automatic);

    public void StartManualLongCapture() => StartLongCapture(LongCaptureMode.Manual);

    private void StartLongCapture(
        LongCaptureMode mode,
        CaptureRegion? initialRegion = null,
        int recallHistoryIndex = -1)
    {
        if (_isCapturing)
        {
            return;
        }

        try
        {
            _isCapturing = true;
            var snapshot = initialRegion is null
                ? _captureService.CaptureScreenContainingCursor()
                : _captureService.CaptureScreenContainingRegion(initialRegion.ToRectangle());
            var initialLocalRegion = ToLocalRegion(initialRegion, snapshot.Bounds);
            var recentRegions = _stateStore.LoadRecentRegions();
            var recallLocalRegions = ToLocalRegions(recentRegions, snapshot.Bounds);
            var overlay = new CaptureOverlayWindow(
                snapshot,
                initialLocalRegion,
                mode == LongCaptureMode.Automatic
                    ? "双击 / ENTER 开始自动长截图  ·  ESC 取消"
                    : "双击 / ENTER 开始手动长截图  ·  ESC 取消",
                false,
                recallLocalRegions,
                recallHistoryIndex,
                _settingsService.Current,
                clipboardService: _clipboardService);
            var sessionStarted = false;

            overlay.SelectionConfirmed += (_, localRegion) =>
            {
                sessionStarted = true;
                var globalRegion = new DrawingRectangle(
                    snapshot.Bounds.X + localRegion.X,
                    snapshot.Bounds.Y + localRegion.Y,
                    localRegion.Width,
                    localRegion.Height);
                overlay.Hide();
                var targetWindow = FindTargetWindow(globalRegion);
                overlay.Close();
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    () => OpenLongCaptureSession(globalRegion, mode, targetWindow));
            };

            overlay.SelectionCancelled += (_, _) => overlay.Close();
            overlay.PreviousSelectionRequested += (_, request) =>
            {
                if (request.HistoryIndex < 0 || request.HistoryIndex >= recentRegions.Count)
                {
                    CaptureFailed?.Invoke(this, "还没有历史截图选区，请先完成一张截图。");
                    return;
                }

                var previousRegion = recentRegions[request.HistoryIndex];
                overlay.Close();
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    () => StartLongCapture(mode, previousRegion, request.HistoryIndex));
            };
            overlay.Closed += (_, _) =>
            {
                if (!sessionStarted)
                {
                    _isCapturing = false;
                }
            };
            overlay.Show();
        }
        catch (Exception exception)
        {
            _isCapturing = false;
            CaptureFailed?.Invoke(this, exception.Message);
        }
    }

    public void RepeatLastCapture()
    {
        var recentRegions = _stateStore.LoadRecentRegions();
        if (recentRegions.Count == 0)
        {
            CaptureFailed?.Invoke(this, "还没有可重复的截图范围，请先按 F1 截图。");
            return;
        }

        OpenCaptureOverlay(recentRegions[0], recallHistoryIndex: 0);
    }

    public void OpenHistoryWindow()
    {
        if (_historyWindow is not null)
        {
            if (_historyWindow.WindowState == System.Windows.WindowState.Minimized)
            {
                _historyWindow.WindowState = System.Windows.WindowState.Normal;
            }

            _historyWindow.Activate();
            _historyWindow.RefreshHistory();
            return;
        }

        _historyWindow = new HistoryWindow(
            _historyService,
            _historyOcrIndexer,
            _clipboardService,
            path => PinImage(path),
            RecognizeImage,
            _qrCodeService);
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    public void OpenHistoryDirectory() => _historyService.OpenHistoryDirectory();

    public void ExportDiagnostics()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出 QingSnap 诊断包",
                Filter = "ZIP 压缩包 (*.zip)|*.zip",
                DefaultExt = ".zip",
                AddExtension = true,
                FileName = $"QingSnap-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            DiagnosticLog.ExportBundle(dialog.FileName, _settingsService.Current);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{dialog.FileName}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Diagnostics", exception, "Failed to export diagnostics bundle.");
            CaptureFailed?.Invoke(this, $"导出诊断包失败：{exception.Message}");
        }
    }

    public void PinLatestCapture()
    {
        if (_isCapturing)
        {
            return;
        }

        try
        {
            EnsurePinHistorySeeded();
            var item = _pinHistory.SelectLatest();
            if (item is null)
            {
                CaptureFailed?.Invoke(this, "还没有可以贴出的截图，请先按 F1 截图。");
                return;
            }

            ShowPinHistoryItem(item);
        }
        catch (Exception exception)
        {
            CaptureFailed?.Invoke(this, exception.Message);
        }
    }

    public async void PinClipboardImage()
    {
        if (_isCapturing)
        {
            return;
        }

        await _pinRequestGate.WaitAsync();
        try
        {
            ClipboardImageContent? clipboardImage = null;
            try
            {
                clipboardImage = await _clipboardService.TryGetImageAsync();
            }
            catch (Exception exception)
            {
                DiagnosticLog.Warning("PinHistory", $"读取剪贴板失败，已继续使用贴图历史：{exception.Message}");
            }

            PinHistoryItem? item;
            if (clipboardImage is not null &&
                _lastClipboardSequenceNumber != clipboardImage.SequenceNumber)
            {
                _lastClipboardSequenceNumber = clipboardImage.SequenceNumber;
                EnsurePinHistorySeeded();
                _pinHistory.AddClipboard(clipboardImage);
                item = _pinHistory.SelectLatest();
            }
            else
            {
                EnsurePinHistorySeeded();
                item = _pinHistory.SelectNext();
            }

            if (item is null)
            {
                CaptureFailed?.Invoke(this, "还没有可以贴出的图片，请先截图或复制一张图片。");
                return;
            }

            ShowPinHistoryItem(item);
        }
        catch (Exception exception)
        {
            CaptureFailed?.Invoke(this, $"贴图失败：{exception.Message}");
        }
        finally
        {
            _pinRequestGate.Release();
        }
    }

    public void RecognizeLatestCapture()
    {
        try
        {
            var latestImagePath = _historyService.FindLatestImagePath();
            if (latestImagePath is null)
            {
                CaptureFailed?.Invoke(this, "还没有可以识别的截图，请先按 F1 截图。");
                return;
            }

            RecognizeImage(latestImagePath);
        }
        catch (Exception exception)
        {
            CaptureFailed?.Invoke(this, exception.Message);
        }
    }

    private void OpenCaptureOverlay(CaptureRegion? initialRegion, int recallHistoryIndex = -1)
    {
        if (_isCapturing)
        {
            return;
        }

        try
        {
            _isCapturing = true;
            var initialSourceWindow = NativeMethods.GetForegroundWindow();
            var snapshot = initialRegion is null
                ? _captureService.CaptureScreenContainingCursor()
                : _captureService.CaptureScreenContainingRegion(initialRegion.ToRectangle());
            DrawingRectangle? initialLocalRegion = initialRegion is null
                ? null
                : new DrawingRectangle(
                    initialRegion.X - snapshot.Bounds.X,
                    initialRegion.Y - snapshot.Bounds.Y,
                    initialRegion.Width,
                    initialRegion.Height);
            var recentRegions = _stateStore.LoadRecentRegions();
            var recallLocalRegions = ToLocalRegions(recentRegions, snapshot.Bounds);
            var overlay = new CaptureOverlayWindow(
                snapshot,
                initialLocalRegion,
                recallLocalRegions: recallLocalRegions,
                recallIndex: recallHistoryIndex,
                settings: _settingsService.Current,
                ocrService: _ocrService,
                qrCodeService: _qrCodeService,
                clipboardService: _clipboardService,
                loadTagsAsync: _historyService.LoadAllTagsAsync);
            var longCaptureStarted = false;

            overlay.ActionRequested += async (_, request) =>
            {
                var globalRegion = new DrawingRectangle(
                    snapshot.Bounds.X + request.LocalRegion.X,
                    snapshot.Bounds.Y + request.LocalRegion.Y,
                    request.LocalRegion.Width,
                    request.LocalRegion.Height);
                var sourceMetadata = CaptureSourceMetadataService.Create(
                    globalRegion,
                    isLongCapture: false,
                    initialSourceWindow);

                if (request.Action == CaptureOverlayAction.AutomaticLongCapture)
                {
                    longCaptureStarted = true;
                    overlay.Hide();
                    var targetWindow = FindTargetWindow(globalRegion);
                    overlay.Close();
                    _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(
                        () => OpenLongCaptureSession(
                            globalRegion,
                            LongCaptureMode.Automatic,
                            targetWindow,
                            request.Tags));
                    return;
                }

                if (request.Action == CaptureOverlayAction.Pin)
                {
                    try
                    {
                        var (imagePath, _) = SaveCaptureToHistory(
                            request.Image,
                            globalRegion,
                            request.PrefetchedOcr,
                            tags: request.Tags,
                            sourceMetadata: sourceMetadata);
                        PinImage(
                            imagePath,
                            globalRegion,
                            request.Image,
                            request.PrefetchedOcr,
                            overlay.CloseAfterPinPresented);
                    }
                    catch (Exception exception)
                    {
                        overlay.Close();
                        CaptureFailed?.Invoke(this, exception.Message);
                    }

                    return;
                }

                overlay.Close();
                try
                {
                    await HandleOverlayActionAsync(request, globalRegion, sourceMetadata);
                }
                catch (Exception exception)
                {
                    CaptureFailed?.Invoke(this, exception.Message);
                }
            };

            overlay.SelectionConfirmed += async (_, localRegion) =>
            {
                var globalRegion = new DrawingRectangle(
                    snapshot.Bounds.X + localRegion.X,
                    snapshot.Bounds.Y + localRegion.Y,
                    localRegion.Width,
                    localRegion.Height);
                var sourceMetadata = CaptureSourceMetadataService.Create(
                    globalRegion,
                    isLongCapture: false,
                    initialSourceWindow);

                try
                {
                    var image = overlay.CreateSelectedImage();
                    overlay.Close();
                    await CompleteCaptureAsync(
                        image,
                        globalRegion,
                        sourceMetadata: sourceMetadata,
                        tags: overlay.SelectedTags);
                }
                catch (Exception exception)
                {
                    overlay.Close();
                    CaptureFailed?.Invoke(this, exception.Message);
                }
            };

            overlay.SelectionCancelled += (_, _) => overlay.Close();
            overlay.PreviousSelectionRequested += (_, request) =>
            {
                if (request.HistoryIndex < 0 || request.HistoryIndex >= recentRegions.Count)
                {
                    CaptureFailed?.Invoke(this, "还没有历史截图选区，请先完成一张截图。");
                    return;
                }

                var previousRegion = recentRegions[request.HistoryIndex];
                overlay.Close();
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    () => OpenCaptureOverlay(previousRegion, request.HistoryIndex));
            };
            overlay.Closed += (_, _) =>
            {
                if (!longCaptureStarted)
                {
                    _isCapturing = false;
                }
            };
            overlay.Show();
        }
        catch (Exception exception)
        {
            _isCapturing = false;
            CaptureFailed?.Invoke(this, exception.Message);
        }
    }

    private async Task CompleteCaptureAsync(
        System.Windows.Media.Imaging.BitmapSource image,
        DrawingRectangle region,
        bool forceCopy = false,
        Task<OcrRecognitionResult>? prefetchedOcr = null,
        bool isLongCapture = false,
        CaptureHistoryContext? sourceMetadata = null,
        IReadOnlyList<string>? tags = null)
    {
        var (imagePath, savedRegion) = SaveCaptureToHistory(
            image,
            region,
            prefetchedOcr,
            isLongCapture,
            sourceMetadata,
            tags);
        var copyRequested = forceCopy || _settingsService.Current.AutoCopy;
        var copiedToClipboard = false;
        if (copyRequested)
        {
            try
            {
                await _clipboardService.CopyCaptureImageAsync(image, savedRegion);
                copiedToClipboard = true;
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error("Clipboard", exception, "截图已保存，但复制到剪贴板失败。");
            }
        }
        CaptureCompleted?.Invoke(
            this,
            new CaptureResult(
                savedRegion,
                imagePath,
                image.PixelWidth,
                image.PixelHeight,
                copyRequested,
                copiedToClipboard));
    }

    private async Task HandleOverlayActionAsync(
        CaptureOverlayActionEventArgs request,
        DrawingRectangle globalRegion,
        CaptureHistoryContext sourceMetadata)
    {
        switch (request.Action)
        {
            case CaptureOverlayAction.Ocr:
                {
                    var (imagePath, _) = SaveCaptureToHistory(
                        request.Image,
                        globalRegion,
                        request.PrefetchedOcr,
                        tags: request.Tags,
                        sourceMetadata: sourceMetadata);
                    RecognizeImage(imagePath, request.Image, request.PrefetchedOcr);
                    break;
                }
            case CaptureOverlayAction.QrCode:
                {
                    var (imagePath, _) = SaveCaptureToHistory(
                        request.Image,
                        globalRegion,
                        tags: request.Tags,
                        sourceMetadata: sourceMetadata);
                    RecognizeQrCode(request.Image, imagePath);
                    break;
                }
            case CaptureOverlayAction.Pin:
                {
                    var (imagePath, _) = SaveCaptureToHistory(
                        request.Image,
                        globalRegion,
                        request.PrefetchedOcr,
                        tags: request.Tags,
                        sourceMetadata: sourceMetadata);
                    PinImage(imagePath, globalRegion, request.Image, request.PrefetchedOcr);
                    break;
                }
            case CaptureOverlayAction.Copy:
                await CompleteCaptureAsync(
                    request.Image,
                    globalRegion,
                    true,
                    request.PrefetchedOcr,
                    sourceMetadata: sourceMetadata,
                    tags: request.Tags);
                break;
            case CaptureOverlayAction.Confirm:
                await CompleteCaptureAsync(
                    request.Image,
                    globalRegion,
                    prefetchedOcr: request.PrefetchedOcr,
                    sourceMetadata: sourceMetadata,
                    tags: request.Tags);
                break;
            case CaptureOverlayAction.Save:
                SaveImageAs(request.Image);
                break;
        }
    }

    private (string ImagePath, CaptureRegion Region) SaveCaptureToHistory(
        System.Windows.Media.Imaging.BitmapSource image,
        DrawingRectangle region,
        Task<OcrRecognitionResult>? prefetchedOcr = null,
        bool isLongCapture = false,
        CaptureHistoryContext? sourceMetadata = null,
        IReadOnlyList<string>? tags = null)
    {
        var captureMetadata = sourceMetadata ?? CaptureSourceMetadataService.Create(region, isLongCapture);
        var imagePath = _historyService.Save(image, captureMetadata);
        if (tags is { Count: > 0 })
        {
            _ = AttachCaptureTagsAsync(imagePath, tags);
        }
        _historyOcrIndexer.EnqueueCapture(imagePath, prefetchedOcr);
        var savedRegion = CaptureRegion.FromRectangle(region);
        _stateStore.SaveLastRegion(savedRegion);
        _pinHistory.AddCapture(image, imagePath, savedRegion);
        _historyWindow?.RefreshHistory();
        DiagnosticLog.Info(
            "Capture",
            $"Saved {image.PixelWidth}x{image.PixelHeight} capture to history; format={_settingsService.Current.OutputFormat}.");
        return (imagePath, savedRegion);
    }

    private async Task AttachCaptureTagsAsync(string imagePath, IReadOnlyList<string> tags)
    {
        try
        {
            await _historyService.AddTagsAsync(imagePath, tags).ConfigureAwait(false);
            if (_historyWindow is not null)
            {
                await _historyWindow.Dispatcher.InvokeAsync(_historyWindow.RefreshHistory);
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error(
                "HistoryTags",
                exception,
                $"截图已正常保存，但快速标签写入失败：{Path.GetFileName(imagePath)}");
        }
    }

    private void SaveImageAs(System.Windows.Media.Imaging.BitmapSource image)
    {
        var format = _settingsService.Current.OutputFormat;
        var extension = format == "JPG" ? ".jpg" : format == "BMP" ? ".bmp" : ".png";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = format == "JPG"
                ? "JPEG 图片 (*.jpg)|*.jpg"
                : format == "BMP"
                    ? "BMP 图片 (*.bmp)|*.bmp"
                    : "PNG 图片 (*.png)|*.png",
            DefaultExt = extension,
            AddExtension = true,
            FileName = $"QingSnap_{DateTime.Now:yyyyMMdd_HHmmss}{extension}"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        System.Windows.Media.Imaging.BitmapEncoder encoder = format switch
        {
            "JPG" => new System.Windows.Media.Imaging.JpegBitmapEncoder
            {
                QualityLevel = _settingsService.Current.JpegQuality
            },
            "BMP" => new System.Windows.Media.Imaging.BmpBitmapEncoder(),
            _ => new System.Windows.Media.Imaging.PngBitmapEncoder()
        };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
    }

    private void OpenLongCaptureSession(
        DrawingRectangle region,
        LongCaptureMode mode,
        nint targetWindow,
        IReadOnlyList<string>? tags = null)
    {
        LongCaptureOverlayWindow? captureOverlay = null;
        try
        {
            var sourceMetadata = CaptureSourceMetadataService.Create(
                region,
                isLongCapture: true,
                targetWindow);
            captureOverlay = new LongCaptureOverlayWindow(region, mode);
            var session = new LongCaptureControlWindow(
                region,
                _captureService,
                mode,
                targetWindow,
                _settingsService.Current);
            session.CaptureCompleted += async (_, result) =>
            {
                try
                {
                    await CompleteCaptureAsync(
                        result.Image,
                        region,
                        isLongCapture: true,
                        sourceMetadata: sourceMetadata,
                        tags: tags);
                }
                catch (Exception exception)
                {
                    CaptureFailed?.Invoke(this, exception.Message);
                }
                finally
                {
                    session.Close();
                }
            };
            session.CaptureCancelled += (_, _) => session.Close();
            session.Closed += (_, _) =>
            {
                captureOverlay.Close();
                _isCapturing = false;
            };
            captureOverlay.Show();
            session.Show();
        }
        catch (Exception exception)
        {
            captureOverlay?.Close();
            _isCapturing = false;
            CaptureFailed?.Invoke(this, $"长截图启动失败：{exception.Message}");
        }
    }

    private static nint FindTargetWindow(DrawingRectangle region)
    {
        var targetPoint = new NativeMethods.NativePoint(
            region.Left + region.Width / 2,
            region.Top + region.Height / 2);
        return NativeMethods.GetAncestor(
            NativeMethods.WindowFromPoint(targetPoint),
            NativeMethods.GetAncestorRoot);
    }

    private static DrawingRectangle? ToLocalRegion(CaptureRegion? region, DrawingRectangle screenBounds)
    {
        if (region is null)
        {
            return null;
        }

        var globalRegion = region.ToRectangle();
        if (!screenBounds.Contains(globalRegion))
        {
            return null;
        }

        return new DrawingRectangle(
            globalRegion.X - screenBounds.X,
            globalRegion.Y - screenBounds.Y,
            globalRegion.Width,
            globalRegion.Height);
    }

    private static IReadOnlyList<DrawingRectangle?> ToLocalRegions(
        IReadOnlyList<CaptureRegion> regions,
        DrawingRectangle screenBounds) =>
        regions.Select(region => ToLocalRegion(region, screenBounds)).ToArray();

    private void EnsurePinHistorySeeded()
    {
        if (_pinHistory.Count > 0)
        {
            return;
        }

        var paths = _historyService.FindRecentImagePaths(PinHistoryBuffer.Capacity);
        var latestRegion = _stateStore.LoadLastRegion();
        for (var index = paths.Count - 1; index >= 0; index--)
        {
            try
            {
                var image = _historyService.LoadFullImage(paths[index]);
                _pinHistory.AddSavedImage(image, paths[index], index == 0 ? latestRegion : null);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                DiagnosticLog.Warning("PinHistory", $"无法载入贴图历史 {paths[index]}：{exception.Message}");
            }
        }
    }

    private void ShowPinHistoryItem(PinHistoryItem item)
    {
        var image = item.Image ??
                    (item.ImagePath is not null
                        ? _historyService.LoadFullImage(item.ImagePath)
                        : throw new InvalidOperationException("贴图历史中的图片数据已不可用。"));
        System.Drawing.Point? initialPosition = null;
        if (item.PreferredRegion is null && NativeMethods.GetCursorPos(out var cursor))
        {
            initialPosition = new System.Drawing.Point(cursor.X, cursor.Y);
        }

        var stickyWindow = new StickyImageWindow(
            image,
            item.SourceName,
            _clipboardService,
            _ocrService,
            _settingsService.Current,
            item.PreferredRegion?.ToRectangle(),
            initialPosition,
            qrCodeService: _qrCodeService);
        stickyWindow.Show();
    }

    private void PinImage(
        string imagePath,
        DrawingRectangle? initialRegion = null,
        System.Windows.Media.Imaging.BitmapSource? sourceImage = null,
        Task<OcrRecognitionResult>? prefetchedOcr = null,
        Action? firstFrameReady = null)
    {
        try
        {
            var image = sourceImage ?? _historyService.LoadFullImage(imagePath);
            var stickyWindow = new StickyImageWindow(
                image,
                imagePath,
                _clipboardService,
                _ocrService,
                _settingsService.Current,
                initialRegion,
                prefetchedOcr: prefetchedOcr,
                qrCodeService: _qrCodeService);
            if (firstFrameReady is not null)
            {
                stickyWindow.FirstFramePresented += (_, _) => firstFrameReady();
            }

            stickyWindow.Show();
        }
        catch (Exception exception)
        {
            firstFrameReady?.Invoke();
            CaptureFailed?.Invoke(this, $"贴图失败：{exception.Message}");
        }
    }

    private void RecognizeImage(string imagePath) => RecognizeImage(imagePath, null, null);

    private void RecognizeImage(
        string imagePath,
        System.Windows.Media.Imaging.BitmapSource? sourceImage,
        Task<OcrRecognitionResult>? prefetchedOcr)
    {
        if (!_ocrService.IsOcrAvailable)
        {
            CaptureFailed?.Invoke(this, "OCR 组件尚未安装。请在设置的“OCR / 文字识别”中安装运行库并选择模型。");
            return;
        }

        try
        {
            var image = sourceImage ?? _historyService.LoadFullImage(imagePath);
            var ocrWindow = new OcrResultWindow(
                imagePath,
                image,
                _ocrService,
                _clipboardService,
                prefetchedOcr,
                text =>
                {
                    _historyService.SaveOcrText(imagePath, text);
                    _historyWindow?.RefreshHistory();
                });
            ocrWindow.Show();
        }
        catch (Exception exception)
        {
            CaptureFailed?.Invoke(this, $"OCR 启动失败：{exception.Message}");
        }
    }

    private void RecognizeQrImage(string imagePath)
    {
        try
        {
            RecognizeQrCode(_historyService.LoadFullImage(imagePath), imagePath);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("QrCode", exception, $"读取二维码识别图片失败：{Path.GetFileName(imagePath)}");
            CaptureFailed?.Invoke(this, "无法读取这张截图，请刷新历史记录后重试。");
        }
    }

    private async void RecognizeQrCode(
        System.Windows.Media.Imaging.BitmapSource image,
        string sourceName)
    {
        try
        {
            var results = await _qrCodeService.RecognizeAsync(image);
            if (results.Count == 0)
            {
                CaptureFailed?.Invoke(this, "未检测到二维码");
                return;
            }

            var resultWindow = new QrCodeResultWindow(
                results,
                _clipboardService,
                Path.GetFileName(sourceName));
            resultWindow.Show();
            resultWindow.Activate();
            DiagnosticLog.Info("QrCode", $"本地二维码识别完成：{results.Count:N0} 个结果。");
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("QrCode", exception, "本地二维码识别失败。");
            CaptureFailed?.Invoke(this, "二维码识别失败，请换一张更清晰的图片重试。");
        }
    }
}
