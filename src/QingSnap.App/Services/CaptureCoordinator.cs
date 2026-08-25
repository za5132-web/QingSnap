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
    private readonly AppSettingsService _settingsService;
    private HistoryWindow? _historyWindow;
    private bool _isCapturing;

    public CaptureCoordinator(
        ScreenCaptureService captureService,
        ClipboardService clipboardService,
        AppStateStore stateStore,
        CaptureHistoryService historyService,
        HistoryOcrIndexingService historyOcrIndexer,
        OcrService ocrService,
        AppSettingsService settingsService)
    {
        _captureService = captureService;
        _clipboardService = clipboardService;
        _stateStore = stateStore;
        _historyService = historyService;
        _historyOcrIndexer = historyOcrIndexer;
        _ocrService = ocrService;
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

    private void StartLongCapture(LongCaptureMode mode, CaptureRegion? initialRegion = null)
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
            var recallLocalRegion = ToLocalRegion(_stateStore.LoadLastRegion(), snapshot.Bounds);
            var overlay = new CaptureOverlayWindow(
                snapshot,
                initialLocalRegion,
                mode == LongCaptureMode.Automatic
                    ? "双击 / ENTER 开始自动长截图  ·  ESC 取消"
                    : "双击 / ENTER 开始手动长截图  ·  ESC 取消",
                false,
                recallLocalRegion,
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
            overlay.PreviousSelectionRequested += (_, _) =>
            {
                var previousRegion = _stateStore.LoadLastRegion();
                if (previousRegion is null)
                {
                    CaptureFailed?.Invoke(this, "还没有上一次截图选区，请先完成一张截图。");
                    return;
                }

                overlay.Close();
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    () => StartLongCapture(mode, previousRegion));
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
        var lastRegion = _stateStore.LoadLastRegion();
        if (lastRegion is null)
        {
            CaptureFailed?.Invoke(this, "还没有可重复的截图范围，请先按 F1 截图。");
            return;
        }

        OpenCaptureOverlay(lastRegion);
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
            RecognizeImage);
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
            var latestImagePath = _historyService.FindLatestImagePath();
            if (latestImagePath is null)
            {
                CaptureFailed?.Invoke(this, "还没有可以贴出的截图，请先按 F1 截图。");
                return;
            }

            PinImage(latestImagePath, _stateStore.LoadLastRegion()?.ToRectangle());
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

        try
        {
            var clipboardImage = await _clipboardService.TryGetImageAsync();
            if (clipboardImage is null)
            {
                PinLatestCapture();
                return;
            }

            System.Drawing.Point? initialPosition = null;
            if (clipboardImage.PreferredRegion is null && NativeMethods.GetCursorPos(out var cursor))
            {
                initialPosition = new System.Drawing.Point(cursor.X, cursor.Y);
            }

            var stickyWindow = new StickyImageWindow(
                clipboardImage.Image,
                clipboardImage.SourceName,
                _clipboardService,
                _ocrService,
                _settingsService.Current,
                clipboardImage.PreferredRegion?.ToRectangle(),
                initialPosition);
            stickyWindow.Show();
        }
        catch (Exception exception)
        {
            CaptureFailed?.Invoke(this, $"贴图失败：{exception.Message}");
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

    private void OpenCaptureOverlay(CaptureRegion? initialRegion)
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
            DrawingRectangle? initialLocalRegion = initialRegion is null
                ? null
                : new DrawingRectangle(
                    initialRegion.X - snapshot.Bounds.X,
                    initialRegion.Y - snapshot.Bounds.Y,
                    initialRegion.Width,
                    initialRegion.Height);
            var recallLocalRegion = ToLocalRegion(_stateStore.LoadLastRegion(), snapshot.Bounds);
            var overlay = new CaptureOverlayWindow(
                snapshot,
                initialLocalRegion,
                recallLocalRegion: recallLocalRegion,
                settings: _settingsService.Current,
                ocrService: _ocrService,
                clipboardService: _clipboardService);
            var longCaptureStarted = false;

            overlay.ActionRequested += async (_, request) =>
            {
                var globalRegion = new DrawingRectangle(
                    snapshot.Bounds.X + request.LocalRegion.X,
                    snapshot.Bounds.Y + request.LocalRegion.Y,
                    request.LocalRegion.Width,
                    request.LocalRegion.Height);

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
                            targetWindow));
                    return;
                }

                if (request.Action == CaptureOverlayAction.Pin)
                {
                    try
                    {
                        var (imagePath, _) = SaveCaptureToHistory(
                            request.Image,
                            globalRegion,
                            request.PrefetchedOcr);
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
                    await HandleOverlayActionAsync(request, globalRegion);
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

                try
                {
                    var image = overlay.CreateSelectedImage();
                    overlay.Close();
                    await CompleteCaptureAsync(image, globalRegion);
                }
                catch (Exception exception)
                {
                    overlay.Close();
                    CaptureFailed?.Invoke(this, exception.Message);
                }
            };

            overlay.SelectionCancelled += (_, _) => overlay.Close();
            overlay.PreviousSelectionRequested += (_, _) =>
            {
                var previousRegion = _stateStore.LoadLastRegion();
                if (previousRegion is null)
                {
                    CaptureFailed?.Invoke(this, "还没有上一次截图选区，请先完成一张截图。");
                    return;
                }

                overlay.Close();
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    () => OpenCaptureOverlay(previousRegion));
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
        Task<OcrRecognitionResult>? prefetchedOcr = null)
    {
        var (imagePath, savedRegion) = SaveCaptureToHistory(image, region, prefetchedOcr);
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
        DrawingRectangle globalRegion)
    {
        switch (request.Action)
        {
            case CaptureOverlayAction.Ocr:
                {
                    var (imagePath, _) = SaveCaptureToHistory(
                        request.Image,
                        globalRegion,
                        request.PrefetchedOcr);
                    RecognizeImage(imagePath, request.Image, request.PrefetchedOcr);
                    break;
                }
            case CaptureOverlayAction.Pin:
                {
                    var (imagePath, _) = SaveCaptureToHistory(
                        request.Image,
                        globalRegion,
                        request.PrefetchedOcr);
                    PinImage(imagePath, globalRegion, request.Image, request.PrefetchedOcr);
                    break;
                }
            case CaptureOverlayAction.Copy:
                await CompleteCaptureAsync(
                    request.Image,
                    globalRegion,
                    true,
                    request.PrefetchedOcr);
                break;
            case CaptureOverlayAction.Confirm:
                await CompleteCaptureAsync(
                    request.Image,
                    globalRegion,
                    prefetchedOcr: request.PrefetchedOcr);
                break;
            case CaptureOverlayAction.Save:
                SaveImageAs(request.Image);
                break;
        }
    }

    private (string ImagePath, CaptureRegion Region) SaveCaptureToHistory(
        System.Windows.Media.Imaging.BitmapSource image,
        DrawingRectangle region,
        Task<OcrRecognitionResult>? prefetchedOcr = null)
    {
        var imagePath = _historyService.Save(image);
        _historyOcrIndexer.EnqueueCapture(imagePath, prefetchedOcr);
        var savedRegion = CaptureRegion.FromRectangle(region);
        _stateStore.SaveLastRegion(savedRegion);
        _historyWindow?.RefreshHistory();
        DiagnosticLog.Info(
            "Capture",
            $"Saved {image.PixelWidth}x{image.PixelHeight} capture to history; format={_settingsService.Current.OutputFormat}.");
        return (imagePath, savedRegion);
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
        nint targetWindow)
    {
        LongCaptureOverlayWindow? captureOverlay = null;
        try
        {
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
                    await CompleteCaptureAsync(result.Image, region);
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
                prefetchedOcr: prefetchedOcr);
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
}
