using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;

namespace QingSnap.App.Views;

public partial class OcrResultWindow : Window
{
    private readonly string _imagePath;
    private readonly BitmapSource _sourceImage;
    private readonly OcrService _ocrService;
    private readonly ClipboardService _clipboardService;
    private readonly Action<string>? _recognizedTextAvailable;
    private Task<OcrRecognitionResult>? _prefetchedRecognition;
    private CancellationTokenSource? _recognitionCancellation;
    private string _recognitionStats = "等待识别";
    private OcrRecognitionResult? _rawResult;

    public OcrResultWindow(
        string imagePath,
        BitmapSource sourceImage,
        OcrService ocrService,
        ClipboardService clipboardService,
        Task<OcrRecognitionResult>? prefetchedRecognition = null,
        Action<string>? recognizedTextAvailable = null)
    {
        _imagePath = imagePath;
        _sourceImage = sourceImage;
        _ocrService = ocrService;
        _clipboardService = clipboardService;
        _recognizedTextAvailable = recognizedTextAvailable;
        _prefetchedRecognition = prefetchedRecognition;
        InitializeComponent();

        SourceImage.Source = sourceImage;
        SourceNameText.Text = Path.GetFileName(imagePath);
        SourceSizeText.Text = $"{sourceImage.PixelWidth} × {sourceImage.PixelHeight} px";
        Loaded += (_, _) => _ = RecognizeAsync();
        Closed += (_, _) =>
        {
            _recognitionCancellation?.Cancel();
            _recognitionCancellation?.Dispose();
            _recognitionCancellation = null;
            SourceImage.Source = null;
            ResourceDiagnostics.Sample("OcrWindowClosed");
        };
    }

    private async Task RecognizeAsync()
    {
        _recognitionCancellation?.Cancel();
        _recognitionCancellation?.Dispose();
        _recognitionCancellation = new CancellationTokenSource();
        var cancellationToken = _recognitionCancellation.Token;

        LoadingPanel.Visibility = Visibility.Visible;
        _rawResult = null;
        CopyRawMenuItem.IsEnabled = false;
        CopyAllButton.IsEnabled = false;
        TitleStatusText.Text = "本地识别 · 正在分析图片";
        FooterStatusText.Text = "正在准备本地 OCR…";

        try
        {
            var progress = new Progress<OcrProgress>(value =>
                FooterStatusText.Text = value.Message);
            var accurateTask = _prefetchedRecognition ?? _ocrService.RecognizeAsync(
                _sourceImage,
                cancellationToken,
                progress,
                includeWordBoxes: false);
            _prefetchedRecognition = null;
            ApplyResult(await accurateTask);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("OCR", exception, "OCR result window recognition failed.");
            _recognitionStats = "识别失败";
            OcrTextBox.Text = string.Empty;
            TitleStatusText.Text = "本地识别 · 未完成";
            FooterStatusText.Text = exception.Message;
            EditorStatsText.Text = "识别失败";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ApplyResult(OcrRecognitionResult result)
    {
        _rawResult = result;
        if (!string.IsNullOrWhiteSpace(result.Text))
        {
            _recognizedTextAvailable?.Invoke(result.Text);
        }

        OcrTextBox.Text = OcrTextLayoutService.Get(result).Text;
        CopyRawMenuItem.IsEnabled = !string.IsNullOrEmpty(result.Text);
        _recognitionStats = $"{result.LanguageName} · {result.LineCount:N0} 行 · {result.ElapsedMilliseconds / 1000:0.00} 秒";
        TitleStatusText.Text = $"本地识别 · {result.LanguageTag}";
        FooterStatusText.Text = result.Text.Length == 0
            ? "没有识别到文字，可以换一张更清晰的截图后重试。"
            : result.SourceWidth == result.RecognitionWidth && result.SourceHeight == result.RecognitionHeight
                ? "已整理阅读顺序和段落，结果可以直接校对和编辑。"
                : $"识别完成；原图已等比缩放至 {result.RecognitionWidth} × {result.RecognitionHeight} px。";
        CopyAllButton.IsEnabled = result.Text.Length > 0;
        UpdateEditorStats();

        if (result.Text.Length > 0)
        {
            OcrTextBox.Focus();
            OcrTextBox.CaretIndex = 0;
        }
    }

    private void OnOcrTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateEditorStats();
        CopyAllButton.IsEnabled = !string.IsNullOrEmpty(OcrTextBox.Text);
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = RecognizeAsync();

    private async void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OcrTextBox.Text))
        {
            return;
        }

        try
        {
            await _clipboardService.CopyTextAsync(OcrTextBox.Text);
            FooterStatusText.Text = "全文已复制到剪贴板。";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("OCR", exception, "复制 OCR 全文失败。");
            FooterStatusText.Text = exception.Message;
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && OcrTextBox.IsKeyboardFocusWithin)
        {
            if (OcrTextBox.SelectionLength > 0) CopyEditorSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private async void CopyEditorSelection()
    {
        try { await _clipboardService.CopyTextAsync(OcrTextBox.SelectedText); }
        catch (Exception exception)
        {
            DiagnosticLog.Error("OCR", exception, "复制 OCR 编辑区选中文字失败。");
            FooterStatusText.Text = "复制未完成，选区已保留，请重试。";
        }
    }

    private async void OnCopyRawClick(object sender, RoutedEventArgs e)
    {
        if (_rawResult is null || _rawResult.Text.Length == 0) return;
        try
        {
            await _clipboardService.CopyTextAsync(_rawResult.Text);
            FooterStatusText.Text = "已按原始识别顺序和换行复制全文。";
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("OCR", exception, "复制原始 OCR 全文失败。");
            FooterStatusText.Text = "复制未完成，请重试。";
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateEditorStats()
    {
        if (!IsInitialized)
        {
            return;
        }

        EditorStatsText.Text = $"{_recognitionStats} · {OcrTextBox.Text.Length:N0} 字符";
    }
}
