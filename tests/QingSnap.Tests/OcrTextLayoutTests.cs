using System.Diagnostics;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace QingSnap.Tests;

public sealed class OcrTextLayoutTests(ITestOutputHelper output)
{
    internal static OcrTextLine Line(int index, string text, double x = 0, double y = 0, double width = 600, double height = 20) =>
        new(index, text, new(x, y, width, height), [new(index, index, text, new(x, y, width, height))]);

    internal static OcrRecognitionResult Result(params OcrTextLine[] lines) =>
        new(string.Join(Environment.NewLine, lines.Select(line => line.Text)), "test", "test", lines.Length,
            1000, 1000, 1000, 1000, lines);

    private const string Prose = "这是一段因页面宽度限制而发生自然折行的较长中文说明文字";

    [Fact]
    public void WrappedChineseProseJoinsButRawResultIsUntouched()
    {
        var result = Result(Line(0, Prose), Line(1, "接下来的内容应当跟随同一段落。", y: 28));
        var raw = result.Text;
        var words = result.Lines[0].Words;
        var layout = OcrTextLayoutService.Get(result);
        Assert.Equal(Prose + "接下来的内容应当跟随同一段落。", layout.Text);
        Assert.Equal(raw, result.Text);
        Assert.Same(words, result.Lines[0].Words);
        Assert.Same(layout, OcrTextLayoutService.Get(result));
    }

    [Theory]
    [InlineData("滚滚长江东逝水，", "浪花淘尽英雄。")]
    [InlineData("滚滚长江东逝水，浪花淘尽英雄，是非成败转头空，", "青山依旧在，几度夕阳红。")]
    [InlineData("1. 第一项说明", "2. 第二项说明")]
    [InlineData("• 第一项", "• 第二项")]
    [InlineData("一、第一节", "二、第二节")]
    [InlineData("const value = 10;", "return value;")]
    [InlineData("C:\\Users\\test\\a.txt", "C:\\Users\\test\\b.txt")]
    [InlineData("https://example.com/test", "https://example.com/next")]
    [InlineData("产品\t数量\t单价", "桌子\t10\t200")]
    [InlineData("名称  数量  价格", "产品  10  100")]
    [InlineData("ppocr6_tiny_dict.txt", "ppocr6_small_rec.onnx")]
    public void PoetryListsCodeAndTablesKeepLineBreaks(string a, string b)
    {
        var result = Result(Line(0, a), Line(1, b, y: 28));
        Assert.Equal(result.Text, OcrTextLayoutService.Get(result).Text);
    }

    [Theory]
    [InlineData(0, 50, 20)] // paragraph gap
    [InlineData(30, 28, 20)] // indentation
    [InlineData(0, 28, 32)] // larger heading
    public void GapIndentationAndHeadingPreventJoin(double x, double y, double height)
    {
        var result = Result(Line(0, Prose), Line(1, "新的段落内容", x, y, height: height));
        Assert.Equal(result.Text, OcrTextLayoutService.Get(result).Text);
    }

    [Theory]
    [InlineData("。")]
    [InlineData("！")]
    [InlineData("：")]
    [InlineData(".")]
    [InlineData("?")]
    public void SentenceAndSectionEndingsRemainConservative(string ending)
    {
        var result = Result(Line(0, Prose + ending), Line(1, "下一段的说明", y: 28));
        Assert.Equal(result.Text, OcrTextLayoutService.Get(result).Text);
    }

    [Fact]
    public void EnglishWrapAddsOneWordSpaceWithoutChangingExistingWordSpaces()
    {
        var a = "This is a sufficiently long paragraph that wraps naturally";
        var b = "onto the following line without any rewriting.";
        var result = Result(Line(0, a), Line(1, b, y: 28));
        Assert.Equal(a + " " + b, OcrTextLayoutService.Get(result).Text);
    }

    [Fact]
    public void ColumnsAreReadTopToBottomBeforeMovingRight()
    {
        var result = Columns();
        var layout = OcrTextLayoutService.Get(result);
        Assert.Equal(new[] { 0, 2, 1, 3 }, layout.Lines.Select(line => line.Source.Index));
        Assert.Equal(string.Join(Environment.NewLine, "左一", "左二", "右一", "右二"), layout.Text);
    }

    internal static OcrRecognitionResult Columns() => Result(Line(0, "左一", 0, 0, 280),
        Line(1, "右一", 400, 0, 280), Line(2, "左二", 0, 28, 280), Line(3, "右二", 400, 28, 280));

    [Fact]
    public void SpanningHeadingAndFooterStayOutsideColumns()
    {
        var result = Result(Line(0, "文章标题", 0, 0, 680, 25),
            Line(1, "左一", 0, 40, 280), Line(2, "右一", 400, 40, 280),
            Line(3, "左二", 0, 68, 280), Line(4, "右二", 400, 68, 280),
            Line(5, "底部说明", 0, 120, 680));
        Assert.Equal(new[] { 0, 1, 3, 2, 4, 5 }, OcrTextLayoutService.Get(result).Lines.Select(line => line.Source.Index));
    }

    [Fact]
    public void FalseGutterWithOverlappingWideContentDoesNotForceColumns()
    {
        var result = Result(Line(0, "宽行", 0, 0, 680),
            Line(1, "左一", 0, 0, 280), Line(2, "右一", 400, 0, 280),
            Line(3, "左二", 0, 28, 280), Line(4, "右二", 400, 28, 280));
        Assert.Single(OcrTextLayoutService.Get(result).Lines.Select(line => line.Block).Distinct());
    }

    [Fact]
    public void FullSelectionFormatsButRawCopyPreservesEngineOrder()
    {
        var result = Columns();
        var selected = result.Lines.SelectMany(line => line.Words).Select(word => word.Index).ToHashSet();
        Assert.Equal(OcrTextLayoutService.Get(result).Text, OcrTextSelectionBuilder.Build(result, selected));
        Assert.Equal(result.Text, OcrTextSelectionBuilder.Build(result, selected, true));
    }

    [Fact]
    public void UnselectedMiddleLineAndPartialSelectionsAreNeverFilledIn()
    {
        var result = Result(Line(0, Prose), Line(1, "绝不能复制这一行", y: 28), Line(2, "选中的第三行", y: 56));
        var copied = OcrTextSelectionBuilder.Build(result, new HashSet<int> { 0, 2 });
        Assert.Equal(Prose + Environment.NewLine + "选中的第三行", copied);
        Assert.DoesNotContain("绝不能", copied);
        Assert.Equal(string.Empty, OcrTextSelectionBuilder.Build(result, new HashSet<int>()));
    }

    [Fact]
    public void RawSelectionPreservesWhitespaceIndentationButNotUnselectedText()
    {
        OcrTextWord[] words = [new(0, 0, "value", new(30, 0, 50, 20)), new(1, 0, "next", new(100, 0, 40, 20))];
        var result = Result(new OcrTextLine(0, "    value next  ", new(0, 0, 160, 20), words));
        Assert.Equal("    value next  ", OcrTextSelectionBuilder.Build(result, new HashSet<int> { 0, 1 }, true));
        Assert.Equal("    value", OcrTextSelectionBuilder.Build(result, new HashSet<int> { 0 }, true));
        Assert.Equal("next  ", OcrTextSelectionBuilder.Build(result, new HashSet<int> { 1 }, true));
    }

    [Fact]
    public void PartialWordSelectionKeepsExactlySelectedContent()
    {
        var line = new OcrTextLine(0, "alpha beta gamma", new(0, 0, 300, 20),
            [new(0, 0, "alpha", new(0, 0, 45, 20)), new(1, 0, "beta", new(70, 0, 40, 20)),
             new(2, 0, "gamma", new(140, 0, 45, 20))]);
        var result = Result(line);
        Assert.Equal("alpha gamma", OcrTextSelectionBuilder.Build(result, new HashSet<int> { 0, 2 }));
        Assert.Equal("beta", OcrTextSelectionBuilder.Build(result, new HashSet<int> { 1 }));
    }

    [Fact]
    public void TightFragmentedCharactersCanBeJoinedButRealEnglishWordSpacesStay()
    {
        const string text = "T i n y OCR";
        OcrTextWord[] words = [new(0, 0, "T", new(0, 0, 8, 20)), new(1, 0, "i", new(9, 0, 4, 20)),
            new(2, 0, "n", new(14, 0, 8, 20)), new(3, 0, "y", new(23, 0, 8, 20)), new(4, 0, "OCR", new(45, 0, 30, 20))];
        Assert.Equal("Tiny OCR", OcrTextLayoutService.NormalizeSpacing(text, words));
        Assert.Equal(text, OcrTextLayoutService.NormalizeSpacing(text,
            words.Select(word => word with { Bounds = word.Bounds with { X = word.Bounds.X * 2 } }).ToArray()));
        Assert.Equal("I a", OcrTextLayoutService.NormalizeSpacing("I a",
            [new(0, 0, "I", new(0, 0, 5, 20)), new(1, 0, "a", new(6, 0, 8, 20))]));
    }

    [Fact]
    public void ChineseSpacingRequiresGeometryAndShortLabelsRemainUntouched()
    {
        const string text = "这 是 一 段 正 文";
        var words = text.Where(c => c != ' ').Select((c, i) => new OcrTextWord(i, 0, c.ToString(), new(i * 20, 0, 19, 20))).ToArray();
        Assert.Equal("这是一段正文", OcrTextLayoutService.NormalizeSpacing(text, words));
        Assert.Equal(text, OcrTextLayoutService.NormalizeSpacing(text, []));
        Assert.Equal("姓 名", OcrTextLayoutService.NormalizeSpacing("姓 名",
            [new(0, 0, "姓", new(0, 0, 19, 20)), new(1, 0, "名", new(20, 0, 19, 20))]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void GeometryScalingDoesNotChangeReadingOrderOrParagraphs(double scale)
    {
        var original = Columns();
        var scaled = Result(original.Lines.Select(line => line with { Bounds = new(line.Bounds.X * scale,
            line.Bounds.Y * scale, line.Bounds.Width * scale, line.Bounds.Height * scale) }).ToArray());
        Assert.Equal(OcrTextLayoutService.Get(original).Text, OcrTextLayoutService.Get(scaled).Text);
    }

    [Fact]
    public async Task CancellationDoesNotPublishPartialLayout()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = Columns();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OcrTextLayoutService.PrepareAsync(result, cancellation.Token));
        Assert.Equal(4, OcrTextLayoutService.Get(result).Lines.Count);
    }

    [Fact]
    public void MissingOrInvalidGeometryKeepsRawResult()
    {
        var result = Result(Line(0, "一"), Line(1, "二", height: 0));
        Assert.Equal(result.Text, OcrTextLayoutService.Get(result).Text);
        Assert.Equal("raw only", OcrTextLayoutService.Get(Result() with { Text = "raw only" }).Text);
    }

    [Fact]
    public void TenThousandLinesAreBoundedAndCached()
    {
        var result = Result(Enumerable.Range(0, 10_000).Select(i => Line(i, "这是用于验证排版性能的普通文字。", y: i * 28)).ToArray());
        var timer = Stopwatch.StartNew();
        var layout = OcrTextLayoutService.Get(result);
        timer.Stop();
        output.WriteLine($"10,000 lines layout: {timer.Elapsed.TotalMilliseconds:0.0} ms; text length {layout.Text.Length}");
        Assert.Equal(10_000, layout.Lines.Count);
        Assert.Same(layout, OcrTextLayoutService.Get(result));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
    }
}
