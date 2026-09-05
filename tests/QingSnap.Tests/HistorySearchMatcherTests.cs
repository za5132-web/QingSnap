using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class HistorySearchMatcherTests
{
    [Theory]
    [InlineData("chrome")]
    [InlineData("chrome.exe")]
    [InlineData("github")]
    [InlineData("QINGSNAP - GITHUB")]
    public void SearchMatchesSourceProcessAndWindowTitle(string query)
    {
        var item = CreateItem("chrome.exe", "QingSnap - GitHub - Google Chrome");

        Assert.True(HistorySearchMatcher.IsMatch(item, query));
    }

    [Fact]
    public void LegacyItemWithoutSourceHasNoPlaceholderAndStillSearchesNormally()
    {
        var item = CreateItem(null, null);

        Assert.False(item.HasSource);
        Assert.Equal(string.Empty, item.SourceDisplay);
        Assert.True(HistorySearchMatcher.IsMatch(item, "sample"));
        Assert.False(HistorySearchMatcher.IsMatch(item, "chrome"));
    }

    [Fact]
    public void PlainAndReservedTagQueriesMatchAllTaggedScreenshots()
    {
        var items = Enumerable.Range(0, 3)
            .Select(_ => CreateItem("chrome.exe", "项目页面", ["工作", "客户A"]))
            .ToArray();

        Assert.Equal(3, items.Count(item => HistorySearchMatcher.IsMatch(item, "工作")));
        Assert.Equal(3, items.Count(item => HistorySearchMatcher.IsMatch(item, "tag:工作")));
        Assert.Equal(3, items.Count(item => HistorySearchMatcher.IsMatch(item, "chrome tag:工作")));
        Assert.DoesNotContain(items, item => HistorySearchMatcher.IsMatch(item, "tag:生活"));
    }

    [Theory]
    [InlineData("chrome", "chrome.exe")]
    [InlineData("CHROME.EXE", "CHROME.EXE")]
    [InlineData("  msedge  ", "msedge.exe")]
    public void ProcessNamesAreStoredWithExecutableSuffix(string processName, string expected)
    {
        Assert.Equal(expected, CaptureSourceMetadataService.NormalizeProcessName(processName));
    }

    private static HistoryItem CreateItem(
        string? process,
        string? title,
        IReadOnlyList<string>? tags = null)
    {
        var thumbnail = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 0, 0, 0, 255 },
            4);
        thumbnail.Freeze();
        return new HistoryItem(
            "C:\\History\\sample.png",
            "sample.png",
            DateTime.Now,
            1,
            1,
            4,
            thumbnail,
            false,
            "OCR text",
            process,
            title,
            tags);
    }
}
