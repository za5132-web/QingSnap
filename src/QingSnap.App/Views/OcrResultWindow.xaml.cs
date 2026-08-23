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
    private Task<OcrRecognitionResult>? _prefetchedRecognition;
    private CancellationTokenSource? _recognitionCancellation;
    private string _recognitionStats = "等待识别";
    private bool _isApplyingResult;
    private bool _userEditedResult;

    public OcrResultWindow(
        string imagePath,
        BitmapSource sourceImage,
        OcrService ocrService,
        ClipboardService clipboardService,
        Task<OcrRecognitionResult>? prefetchedRecognition = null)
    {
        _imagePath = imagePath;
        _sourceImage = sourceImage;
        _ocrService = ocrService;
        _clipboardService = clipboardService;
        _prefetchedRecognition = prefetchedRecognition;
        InitializeComponent();
        _userEditedResult = false;

        SourceImage.Source = sourceImage;
        SourceNameText.Text = Path.GetFileName(imagePath);
        SourceSizeText.Text = $"{sourceImage.PixelWidth} × {sourceImage.PixelHeight} px";
        Loaded += (_, _) => _ = RecognizeAsync();
        Closed += (_, _) => _recognitionCancellation?.Cancel();
    }

    private async Task RecognizeAsync()
    {
        _userEditedResult = false;
        _recognitionCancellation?.Cancel();
        _recognitionCancellation?.Dispose();
        _recognitionCancellation = new CancellationTokenSource();
        var cancellationToken = _recognitionCancellation.Token;

        LoadingPanel.Visibility = Visibility.Visible;
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
            if (!_ocrService.UsesAdvancedEngine)
            {
                ApplyResult(await accurateTask);
                return;
            }

            var initialWait = Task.Delay(140, cancellationToken);
            if (await Task.WhenAny(accurateTask, initialWait) == accurateTask)
            {
                ApplyResult(await accurateTask);
                return;
            }

            var fastTask = _ocrService.RecognizeFastAsync(_sourceImage, cancellationToken);
            var firstResult = await Task.WhenAny(accurateTask, fastTask);
            if (firstResult == accurateTask)
            {
                ApplyResult(await accurateTask);
                return;
            }

            ApplyResult(await fastTask, refining: true);
            LoadingPanel.Visibility = Visibility.Collapsed;
            var accurateResult = await accurateTask;
            cancellationToken.ThrowIfCancellationRequested();
            if (!_userEditedResult)
            {
                ApplyResult(accurateResult);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
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

    private void ApplyResult(OcrRecognitionResult result, bool refining = false)
    {
        _isApplyingResult = true;
        try
        {
            OcrTextBox.Text = result.Text;
        }
        finally
        {
            _isApplyingResult = false;
        }
        _recognitionStats = $"{result.LanguageName} · {result.LineCount:N0} 行 · {result.ElapsedMilliseconds / 1000:0.00} 秒";
        TitleStatusText.Text = refining
            ? $"快速识别 · {result.LanguageTag}"
            : $"高精度识别 · {result.LanguageTag}";
        FooterStatusText.Text = refining
            ? "文字已显示，正在后台进行高精度校准…"
            : result.Text.Length == 0
                ? "没有识别到文字，可以换一张更清晰的截图后重试。"
                : result.SourceWidth == result.RecognitionWidth && result.SourceHeight == result.RecognitionHeight
                    ? "识别完成，结果可以直接校对和编辑。"
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
        if (!_isApplyingResult)
        {
            _userEditedResult = true;
        }
        UpdateEditorStats();
        CopyAllButton.IsEnabled = !string.IsNullOrEmpty(OcrTextBox.Text);
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = RecognizeAsync();

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(OcrTextBox.Text))
        {
            return;
        }

        try
        {
            _clipboardService.CopyText(OcrTextBox.Text);
            FooterStatusText.Text = "全文已复制到剪贴板。";
        }
        catch (Exception exception)
        {
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
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
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
