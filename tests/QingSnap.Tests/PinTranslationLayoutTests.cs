using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

[Collection("WpfWindowTests")]
public sealed class PinTranslationLayoutTests
{
    [Theory]
    [InlineData("Hello world", true)] [InlineData("日本語の翻訳テスト", true)]
    [InlineData("안녕하세요", true)] [InlineData("Привет мир", true)]
    [InlineData("这里已经是中文", false)] [InlineData("2026 1100 800", false)]
    [InlineData("https://example.com", false)] [InlineData("图像识别 QingSnap", false)]
    public void DetectsForeignTextWithoutSendingChineseNumbersOrUrls(string text, bool expected) =>
        Assert.Equal(expected, PinTranslationLayout.NeedsTranslation(text));

    [Fact]
    public void ChineseCanBePlannedForEnglishAndTraditionalChinese()
    {
        var source = Ocr(800, 600, Line(0, "你好世界", new(20, 20, 600, 40)));
        Assert.Empty(PinTranslationLayout.Plan(source));
        Assert.Single(PinTranslationLayout.Plan(source, targetLanguage: "en"));
        Assert.Single(PinTranslationLayout.Plan(source, targetLanguage: "cht"));
        Assert.Single(PinTranslationLayout.Plan(source, new HashSet<int> { 0 }, targetLanguage: "zh"));
        Assert.Throws<TranslationException>(() => PinTranslationLayout.Plan(source, targetLanguage: "bad"));
    }

    [Theory]
    [InlineData("en", "Hello world, nice to meet you.")]
    [InlineData("jp", "こんにちは、世界。")]
    [InlineData("kor", "안녕하세요 세계.")]
    [InlineData("fra", "Bonjour le monde.")]
    [InlineData("de", "Hallo Welt.")]
    [InlineData("ru", "Привет мир.")]
    [InlineData("spa", "Hola mundo.")]
    [InlineData("cht", "這是翻譯後的文字。")]
    public void RendersChosenLanguageAndPreservesSelectableWordSpaces(string target, string text) => Run(() =>
    {
        var image = Image(800, 200, 255);
        var source = Ocr(800, 200, Line(0, "这是原始中文", new(20, 20, 720, 50)));
        var blocks = PinTranslationLayout.Plan(source, targetLanguage: target);
        var result = PinTranslationLayout.Render(image, source, blocks, [new(0, text)], targetLanguage: target);
        Assert.Equal(target, result.TargetLanguage);
        Assert.Equal(target, result.SelectionResult.LanguageTag);
        Assert.Single(result.Patches);
        var ids = result.SelectionResult.Lines.SelectMany(line => line.Words).Select(word => word.Index).ToHashSet();
        Assert.Equal(text, OcrTextSelectionBuilder.Build(result.SelectionResult, ids));
        Assert.True(result.Overlay.IsFrozen);
    });

    [Fact]
    public void IdenticalProviderResultLeavesOriginalPixelsAndTextUntouched() => Run(() =>
    {
        const string text = "Already English, with original spaces.";
        var image = Image(800, 200, 255);
        var source = Ocr(800, 200, Line(0, text, new(20, 20, 720, 40)));
        var result = PinTranslationLayout.Render(image, source, PinTranslationLayout.Plan(source, targetLanguage: "en"),
            [new(0, text)], targetLanguage: "en");
        Assert.Empty(result.Patches);
        Assert.Equal(Pixels(image), Pixels(Compose(image, result)));
        Assert.Equal(text, Assert.Single(result.SelectionResult.Lines).Text);
    });

    [Fact]
    public void GroupsAdjacentParagraphLinesButKeepsSeparateColumnsAndHeadings()
    {
        var source = Ocr(800, 600,
            Line(0, "First English line", new(20, 20, 230, 20)),
            Line(1, "Second English line", new(20, 46, 220, 20)),
            Line(2, "Right column", new(440, 20, 240, 20)),
            Line(3, "Large heading", new(20, 160, 300, 36)),
            Line(4, "Small body text", new(20, 205, 230, 20)),
            Line(5, "已有中文", new(20, 270, 230, 20)));
        var blocks = PinTranslationLayout.Plan(source);
        Assert.Contains(blocks, block => block.Text == "First English line Second English line");
        Assert.DoesNotContain(blocks, block => block.Text.Contains("中文"));
        Assert.DoesNotContain(blocks, block => block.Text.Contains("heading Small"));
        Assert.All(blocks, block => Assert.True(block.Bounds.Width < 400));
    }

    [Fact]
    public void SelectionPlansOnlySelectedWordGeometry()
    {
        var words = new[] { new OcrTextWord(0, 0, "Hello", new(10, 20, 60, 20)), new OcrTextWord(1, 0, "world", new(80, 20, 60, 20)) };
        var source = Ocr(400, 200, new OcrTextLine(0, "Hello world", new(10, 20, 130, 20), words));
        var block = Assert.Single(PinTranslationLayout.Plan(source, new HashSet<int> { 1 }));
        Assert.Equal("world", block.Text);
        Assert.Equal(new Rect(80, 20, 60, 20), block.Bounds);
    }

    [Theory]
    [InlineData(255)] [InlineData(25)]
    public void OverlayPreservesOriginalAndUsesNativePixelsWithFrozenDrawing(int shade) => Run(() =>
    {
        var image = Image(400, 240, (byte)shade, 192);
        var source = Ocr(400, 240, Line(0, "Hello world", new(30, 50, 220, 32)),
            Line(1, "原有中文", new(30, 150, 220, 25)));
        var before = Pixels(image);
        var blocks = PinTranslationLayout.Plan(source);
        var document = PinTranslationLayout.Render(image, source, blocks, [new(0, "你好，世界")]);
        Assert.True(document.Overlay.IsFrozen);
        Assert.Equal(400, document.Overlay.Width);
        Assert.Equal(240, document.Overlay.Height);
        Assert.Equal(before, Pixels(image));
        Assert.Equal(new Rect(30, 50, 220, 32), Assert.Single(document.Patches).Bounds);
        Assert.Contains(document.SelectionResult.Lines, line => line.Text.Contains("原有中文"));
        Assert.Contains(document.SelectionResult.Lines.SelectMany(line => line.Words), word => word.Text == "你");
        Assert.Equal(document.SelectionResult.Lines.SelectMany(line => line.Words).Count(),
            document.SelectionResult.Lines.SelectMany(line => line.Words).Select(word => word.Index).Distinct().Count());
        var output = Compose(image, document);
        Assert.NotEqual(before, Pixels(output));
        Assert.Equal(Pixel(image, 0, 0), Pixel(output, 0, 0));
    });

    [Fact]
    public void LongTranslationFitsOrExplicitlyMarksTruncationWithoutEscapingRegion() => Run(() =>
    {
        var image = Image(200, 120, 255);
        var source = Ocr(200, 120, Line(0, "English", new(30, 20, 28, 12)));
        var blocks = PinTranslationLayout.Plan(source);
        var target = new string('字', 300);
        var result = PinTranslationLayout.Render(image, source, blocks, [new(0, target)]);
        Assert.True(Assert.Single(result.Patches).IsTruncated);
        Assert.Equal(target, result.Text);
        Assert.All(result.SelectionResult.Lines.SelectMany(line => line.Words), word =>
            Assert.True(blocks[0].Bounds.Contains(new Rect(word.Bounds.X, word.Bounds.Y, word.Bounds.Width, word.Bounds.Height))));
        Assert.Equal(Pixel(image, 100, 50), Pixel(Compose(image, result), 100, 50));
    });

    [Fact]
    public void InvalidGeometryAndCancelledRenderingCannotProduceAnOverlay() => Run(() =>
    {
        var image = Image(200, 100, 255);
        var source = Ocr(200, 100, Line(0, "Bad", new(double.NaN, 10, 100, 20)), Line(1, "Good", new(10, 40, 100, 20)));
        var blocks = PinTranslationLayout.Plan(source);
        Assert.Single(blocks);
        Assert.Throws<TranslationException>(() => PinTranslationLayout.Render(image, source, blocks, []));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PinTranslationLayout.Render(image, source, blocks, [new(0, "好")], cancellation.Token));
    });

    internal static OcrTextLine Line(int index, string text, OcrTextBounds bounds) =>
        new(index, text, bounds, [new OcrTextWord(index * 100, index, text, bounds)]);
    internal static OcrRecognitionResult Ocr(int width, int height, params OcrTextLine[] lines) =>
        new(string.Join('\n', lines.Select(line => line.Text)), "synthetic", "en", lines.Length, width, height, width, height, lines);
    internal static BitmapSource Image(int width, int height, byte shade, double dpi = 96)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        { pixels[index] = pixels[index + 1] = pixels[index + 2] = shade; pixels[index + 3] = 255; }
        var image = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }
    internal static BitmapSource Compose(BitmapSource image, PinTranslationDocument document)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        { context.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight)); context.DrawImage(document.Overlay, new Rect(0, 0, image.PixelWidth, image.PixelHeight)); }
        var result = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }
    internal static byte[] Pixels(BitmapSource image)
    { var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes; }
    internal static uint Pixel(BitmapSource image, int x, int y)
    { var bytes = new byte[4]; image.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0); return BitConverter.ToUInt32(bytes); }

    private static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
