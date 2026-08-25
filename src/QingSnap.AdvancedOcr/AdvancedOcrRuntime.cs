using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using RapidOcrNet;
using SkiaSharp;

namespace QingSnap.AdvancedOcr;

public sealed class AdvancedOcrRuntime : IAdvancedOcrRuntime
{
    private RapidOcr? _engine;
    private string _modelVariant = OcrModelManager.SmallModel;

    public void Initialize(OcrModelPaths paths)
    {
        if (_engine is not null)
        {
            return;
        }

        var engine = new RapidOcr();
        try
        {
            var inferenceThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            using var sessionOptions = RapidOcr.GetDefaultSessionOptions(inferenceThreads);
            sessionOptions.EnableCpuMemArena = false;
            sessionOptions.EnableMemoryPattern = false;
            var modelSet = string.Equals(
                paths.ModelVariant,
                OcrModelManager.TinyModel,
                StringComparison.OrdinalIgnoreCase)
                ? RapidOcrModelSet.PPOCRv6Tiny
                : RapidOcrModelSet.PPOCRv6Small;
            engine.InitModels(modelSet with
            {
                DetModelPath = paths.Detection,
                RecModelPath = paths.Recognition,
                ClsModelPath = paths.Classification,
                KeysPath = paths.Dictionary
            }, sessionOptions);
            _engine = engine;
            _modelVariant = paths.ModelVariant;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        var engine = _engine ?? throw new InvalidOperationException("高精度 OCR 扩展尚未初始化。");
        using var bitmap = new SKBitmap(96, 48, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.White);
        await engine.DetectAsync(bitmap, CreateOptions(false), null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OcrRecognitionResult> RecognizeAsync(
        BitmapSource source,
        bool includeWordBoxes,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var engine = _engine ?? throw new InvalidOperationException("高精度 OCR 扩展尚未初始化。");
        using var bitmap = EncodeSkBitmap(source);
        var lineProgress = progress is null
            ? null
            : new Progress<(int Completed, int Total)>(value =>
                progress.Report(new OcrProgress(
                    value.Total > 0
                        ? $"正在识别文字 · {value.Completed} / {value.Total} 行"
                        : "正在识别文字…")));
        var result = await engine.DetectAsync(
            bitmap,
            CreateOptions(includeWordBoxes),
            lineProgress,
            cancellationToken).ConfigureAwait(false);

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

        return new OcrRecognitionResult(
            string.Join(Environment.NewLine, lines.Select(line => line.Text)).Trim(),
            OcrModelManager.GetDisplayName(_modelVariant),
            "离线 · 多语言",
            lines.Length,
            source.PixelWidth,
            source.PixelHeight,
            source.PixelWidth,
            source.PixelHeight,
            lines);
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
        _modelVariant = OcrModelManager.SmallModel;
    }

    private RapidOcrOptions CreateOptions(bool includeWordBoxes) =>
        RapidOcrOptions.PPOCRv6 with
        {
            LimitSideLen = string.Equals(
                _modelVariant,
                OcrModelManager.TinyModel,
                StringComparison.OrdinalIgnoreCase)
                ? 512
                : 736,
            DoAngle = false,
            ReturnWordBox = includeWordBoxes,
            ReturnSingleCharBox = false
        };

    private static SKBitmap EncodeSkBitmap(BitmapSource source)
    {
        BitmapSource bgraSource = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(bgraSource.PixelWidth * 4);
        var bitmap = new SKBitmap(
            bgraSource.PixelWidth,
            bgraSource.PixelHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        try
        {
            bgraSource.CopyPixels(
                new System.Windows.Int32Rect(0, 0, bgraSource.PixelWidth, bgraSource.PixelHeight),
                bitmap.GetPixels(),
                bitmap.ByteCount,
                stride);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

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
}
