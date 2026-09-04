using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class AppStateStoreTests
{
    [Fact]
    public void RecentRegionsKeepFiveUniqueItemsInNewestFirstOrder()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new AppStateStore(Path.Combine(directory, "state.json"));
            for (var index = 1; index <= 6; index++)
            {
                store.SaveLastRegion(new CaptureRegion(index, index * 2, 100, 80));
            }

            Assert.Equal([6, 5, 4, 3, 2], store.LoadRecentRegions().Select(region => region.X));

            store.SaveLastRegion(new CaptureRegion(4, 8, 100, 80));
            Assert.Equal([4, 6, 5, 3, 2], store.LoadRecentRegions().Select(region => region.X));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacyLastRegionStateIsStillReadable()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var statePath = Path.Combine(directory, "state.json");
            File.WriteAllText(statePath, """{"LastRegion":{"X":12,"Y":34,"Width":320,"Height":180}}""");
            var store = new AppStateStore(statePath);

            Assert.Equal(new CaptureRegion(12, 34, 320, 180), store.LoadLastRegion());
            Assert.Single(store.LoadRecentRegions());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RegionHistoryCursorLoopsAfterTheFifthSelection()
    {
        var cursor = -1;
        var visited = new List<int>();

        for (var press = 0; press < 6; press++)
        {
            cursor = CaptureRegionHistory.NextIndex(cursor, 5);
            visited.Add(cursor);
        }

        Assert.Equal([0, 1, 2, 3, 4, 0], visited);
    }

    [Fact]
    public void PinHistoryKeepsFiveImagesAndCyclesContinuously()
    {
        var history = new PinHistoryBuffer();
        for (var index = 1; index <= 6; index++)
        {
            history.AddSavedImage(CreateSolidImage((byte)(index * 20)), $"image-{index}.png", null);
        }

        Assert.Equal(5, history.Count);
        Assert.Equal(
            ["image-6.png", "image-5.png", "image-4.png", "image-3.png", "image-2.png", "image-6.png"],
            Enumerable.Range(0, 6).Select(_ => history.SelectNext()!.SourceName));
    }

    [Fact]
    public void PinHistoryPromotesMatchingClipboardImageWithoutDuplicatingIt()
    {
        var history = new PinHistoryBuffer();
        var first = CreateSolidImage(20);
        var second = CreateSolidImage(40);
        history.AddSavedImage(first, "first.png", null);
        history.AddSavedImage(second, "second.png", null);

        history.AddClipboard(new ClipboardImageContent(first, "剪贴板图片", null, 10));

        Assert.Equal(2, history.Count);
        var promoted = history.SelectLatest()!;
        Assert.Equal("剪贴板图片", promoted.SourceName);
        Assert.Null(promoted.PreferredRegion);
        Assert.Null(promoted.Image);
        Assert.Equal("first.png", promoted.ImagePath);
        Assert.Equal("second.png", history.SelectNext()!.SourceName);
    }

    [Fact]
    public void QingSnapClipboardCopyReusesLatestCaptureDespiteEncodingDifferences()
    {
        var history = new PinHistoryBuffer();
        var region = new CaptureRegion(30, 40, 200, 120);
        history.AddCapture(CreateSolidImage(80), "capture.jpg", region);

        history.AddClipboard(new ClipboardImageContent(
            CreateSolidImage(81),
            "QingSnap 截图",
            region,
            11));

        Assert.Equal(1, history.Count);
        var item = history.SelectLatest()!;
        Assert.Equal("capture.jpg", item.ImagePath);
        Assert.Equal(region, item.PreferredRegion);
    }

    private static BitmapSource CreateSolidImage(byte red)
    {
        var pixels = Enumerable.Repeat(new byte[] { 10, 20, red, 255 }, 4)
            .SelectMany(pixel => pixel)
            .ToArray();
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        image.Freeze();
        return image;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"QingSnap-state-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
