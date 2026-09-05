using System.Diagnostics;
using System.IO;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace QingSnap.Tests;

public sealed class HistoryMetadataPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(100)]
    [InlineData(1_000)]
    [InlineData(5_000)]
    [InlineData(10_000)]
    [Trait("Category", "Performance")]
    public async Task MetadataStore_ScalesToRequestedHistorySize(int itemCount)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "QingSnap-history-stress",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new string[itemCount];
        var beforeMemory = GC.GetTotalMemory(forceFullCollection: false);
        try
        {
            using var store = new HistoryMetadataStore(directory);
            var writeTimer = Stopwatch.StartNew();
            for (var index = 0; index < itemCount; index++)
            {
                var path = Path.Combine(directory, $"capture-{index:D5}.png");
                paths[index] = path;
                store.QueueUpsert(CreateMetadata(path, index));
            }

            await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
            writeTimer.Stop();

            var readTimer = Stopwatch.StartNew();
            var records = await store.LoadByPathsAsync(paths).WaitAsync(TimeSpan.FromSeconds(20));
            readTimer.Stop();
            var memoryGrowth = Math.Max(0, GC.GetTotalMemory(forceFullCollection: false) - beforeMemory);

            Assert.Equal(itemCount, records.Count);
            Assert.True(writeTimer.Elapsed < TimeSpan.FromSeconds(30));
            Assert.True(readTimer.Elapsed < TimeSpan.FromSeconds(20));
            Assert.True(memoryGrowth < 256L * 1024L * 1024L);
            output.WriteLine(
                "items={0}; writeMs={1:0.0}; readMs={2:0.0}; managedGrowthMB={3:0.0}",
                itemCount,
                writeTimer.Elapsed.TotalMilliseconds,
                readTimer.Elapsed.TotalMilliseconds,
                memoryGrowth / (1024D * 1024D));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task TenThousandItems_SupportFullDatabasePagingSearchAndFilters()
    {
        const int itemCount = 10_000;
        var directory = Path.Combine(Path.GetTempPath(), "QingSnap-history-query", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new HistoryMetadataStore(directory);
            var paths = new string[itemCount];
            for (var index = 0; index < itemCount; index++)
            {
                paths[index] = Path.Combine(directory, $"capture-{index:D5}.png");
                store.QueueUpsert(CreateMetadata(paths[index], index));
            }

            await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await store.AddTagsAsync(paths[8_000], ["工作"]);

            var timer = Stopwatch.StartNew();
            var first = await store.QueryHistoryAsync(new HistoryQuery(0, 80));
            var firstMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var page50 = await store.QueryHistoryAsync(new HistoryQuery(49 * 80, 80));
            var page50Ms = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var ocr = await store.QueryHistoryAsync(new HistoryQuery(0, 80, "OCR 搜索样本 8001"));
            var ocrMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var source = await store.QueryHistoryAsync(new HistoryQuery(0, 80, "压力测试窗口 8001"));
            var sourceMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var tag = await store.QueryHistoryAsync(new HistoryQuery(0, 80, Tag: "工作"));
            var tagMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var favorite = await store.QueryHistoryAsync(new HistoryQuery(0, 80, Filter: HistoryFilterKind.Favorite));
            var favoriteMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var longCapture = await store.QueryHistoryAsync(new HistoryQuery(0, 80, Filter: HistoryFilterKind.LongCapture));
            var longMs = timer.Elapsed.TotalMilliseconds;

            Assert.Equal(itemCount, first.TotalCount);
            Assert.Equal(80, first.Items.Count);
            Assert.True(first.HasMore);
            Assert.Equal(80, page50.Items.Count);
            Assert.True(first.Items[0].CaptureTime > first.Items[^1].CaptureTime);
            Assert.True(page50.Items[0].CaptureTime > page50.Items[^1].CaptureTime);
            Assert.Single(ocr.Items);
            Assert.EndsWith("capture-08001.png", ocr.Items[0].FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.Single(source.Items);
            Assert.Single(tag.Items);
            Assert.Contains("工作", tag.Items[0].Tags);
            Assert.Equal((itemCount - 1) / 11 + 1, favorite.TotalCount);
            Assert.Equal((itemCount - 1) / 17 + 1, longCapture.TotalCount);

            output.WriteLine(
                "query10k first={0:0.0}ms; page50={1:0.0}ms; ocr={2:0.0}ms; source={3:0.0}ms; tag={4:0.0}ms; favorite={5:0.0}ms; long={6:0.0}ms",
                firstMs, page50Ms, ocrMs, sourceMs, tagMs, favoriteMs, longMs);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task OneHundredPages_AreUniqueSortedAndCancelable()
    {
        const int itemCount = 10_000;
        const int pageSize = 80;
        var directory = Path.Combine(Path.GetTempPath(), "QingSnap-history-pages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new HistoryMetadataStore(directory);
            for (var index = 0; index < itemCount; index++)
            {
                store.QueueUpsert(CreateMetadata(Path.Combine(directory, $"page-{index:D5}.png"), index));
            }

            await store.FlushAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var loaded = new List<HistorySummary>(100 * pageSize);
            for (var pageIndex = 0; pageIndex < 100; pageIndex++)
            {
                var page = await store.QueryHistoryAsync(new HistoryQuery(
                    pageIndex * pageSize,
                    pageSize,
                    IncludeStatistics: pageIndex == 0));
                loaded.AddRange(page.Items);
            }

            Assert.Equal(8_000, loaded.Count);
            Assert.Equal(loaded.Count, loaded.Select(item => item.Id).Distinct().Count());
            Assert.True(loaded.Zip(loaded.Skip(1)).All(pair => pair.First.CaptureTime >= pair.Second.CaptureTime));

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.QueryHistoryAsync(new HistoryQuery(0, pageSize, "old query"), canceled.Token));
            var current = await store.QueryHistoryAsync(new HistoryQuery(0, pageSize, "OCR 搜索样本 9000"));
            Assert.Single(current.Items);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static HistoryMetadata CreateMetadata(string path, int index)
    {
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(-index);
        return new HistoryMetadata(
            0,
            path,
            timestamp,
            1920,
            1080,
            180_000,
            "PNG",
            index % 17 == 0,
            index % 11 == 0,
            index % 3 == 0 ? $"OCR 搜索样本 {index}" : string.Empty,
            index % 3 == 0 ? HistoryOcrIndexState.Indexed : HistoryOcrIndexState.NotIndexed,
            index % 2 == 0 ? "chrome.exe" : "explorer.exe",
            $"QingSnap 压力测试窗口 {index}",
            "DISPLAY1",
            "\\\\.\\DISPLAY1",
            -1920 + index % 1920,
            index % 1080,
            1920,
            1080,
            index.ToString("X64"),
            timestamp,
            timestamp);
    }
}
