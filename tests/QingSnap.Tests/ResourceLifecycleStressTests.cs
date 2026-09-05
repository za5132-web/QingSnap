using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;
using Xunit.Abstractions;
using ZXing;
using ZXing.Common;

namespace QingSnap.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ResourceLifecycleSerialCollection
{
    public const string Name = "Resource lifecycle serial";
}

[Collection(ResourceLifecycleSerialCollection.Name)]
public sealed class ResourceLifecycleStressTests(ITestOutputHelper output)
{
    [Fact]
    public void ResourceSnapshotContainsManagedAndNativeProcessCounters()
    {
        var snapshot = ResourceDiagnostics.Capture("UnitTest", ("Thumb", 17));

        Assert.True(snapshot.WorkingSetBytes > 0);
        Assert.True(snapshot.PrivateMemoryBytes > 0);
        Assert.True(snapshot.GcHeapBytes >= 0);
        Assert.True(snapshot.TotalAllocatedBytes > 0);
        Assert.True(snapshot.HandleCount > 0);
        Assert.True(snapshot.ThreadCount > 0);
        Assert.True(snapshot.GdiObjectCount >= 0);
        Assert.True(snapshot.UserObjectCount >= 0);
        Assert.Equal(17, snapshot.Gauges["Thumb"]);
        Assert.Contains("WS=", snapshot.ToLogLine(), StringComparison.Ordinal);
        Assert.Contains("GDI=", snapshot.ToLogLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void OneHundredLongCaptureAssemblersReleaseAllRetainedPixelBuffers()
    {
        var start = ResourceDiagnostics.Capture("LongCapture100_Start");
        for (var round = 0; round < 100; round++)
        {
            var assembler = new LongCaptureAssembler();
            assembler.AddFrame(CreatePattern(240, 160, round));
            var result = assembler.BuildImageAndRelease();
            Assert.True(result.IsFrozen);
            Assert.Equal(0, assembler.EstimatedRetainedBytes);
        }

        var end = ResourceDiagnostics.Capture("LongCapture100_End");
        AssertNonLinearNativeGrowth(start, end, maximumHandleDelta: 32, maximumGdiDelta: 16);
        WriteDelta(start, end);
    }

    [Fact]
    public async Task FiftyQrRecognitionsDoNotAccumulateNativeResources()
    {
        var service = new QrCodeService();
        var image = CreateQrCode("https://github.com/za5132-web/QingSnap", 260);
        var start = ResourceDiagnostics.Capture("Qr50_Start");
        for (var round = 0; round < 50; round++)
        {
            var result = await service.RecognizeAsync(image);
            Assert.Single(result);
        }

        var end = ResourceDiagnostics.Capture("Qr50_End");
        AssertNonLinearNativeGrowth(start, end, maximumHandleDelta: 32, maximumGdiDelta: 16);
        WriteDelta(start, end);
    }

    [Fact]
    public async Task OneHundredClipboardOperationsReuseOneStaThread()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var threadsBefore = process.Threads.Count;
        var start = ResourceDiagnostics.Capture("Clipboard100_Start");
        var clipboard = new ClipboardService();
        var queueThreadIds = new HashSet<int>();
        var operationSamples = new List<ResourceSnapshot>();
        for (var round = 0; round < 100; round++)
        {
            queueThreadIds.Add(await clipboard.GetDiagnosticThreadIdAsync());
            if ((round + 1) % 10 == 0)
            {
                operationSamples.Add(ResourceDiagnostics.Capture($"Clipboard_{round + 1}"));
            }
        }

        Assert.Single(queueThreadIds);
        Assert.InRange(operationSamples.Max(sample => sample.HandleCount) - operationSamples.Min(sample => sample.HandleCount), 0, 16);
        Assert.InRange(operationSamples[^1].HandleCount - operationSamples[0].HandleCount, int.MinValue, 16);
        clipboard.Dispose();
        Assert.False(clipboard.IsDiagnosticWorkerAlive);
        process.Refresh();
        Assert.InRange(process.Threads.Count, 1, threadsBefore + 12);
        var end = ResourceDiagnostics.Capture("Clipboard100_End");
        AssertNonLinearNativeGrowth(start, end, maximumHandleDelta: 64, maximumGdiDelta: 16);
        WriteDelta(start, end);
    }

    [Fact]
    public async Task TwentyUpdateChecksReuseClientWithoutHandleGrowth()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "QingSnap-resource-update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                tag_name = "v1.1.0",
                published_at = "2026-09-05T01:30:00Z",
                body = $"SHA-256: {new string('A', 64)}",
                html_url = "https://github.com/za5132-web/QingSnap/releases/tag/v1.1.0",
                assets = new[]
                {
                    new
                    {
                        name = "QingSnap-v1.1.0.zip",
                        browser_download_url = "https://github.com/za5132-web/QingSnap/releases/download/v1.1.0/QingSnap-v1.1.0.zip",
                        size = 1024
                    }
                }
            });
            using var client = new HttpClient(new RepeatingHandler(payload));
            using var service = new UpdateService(dataDirectory, client, new Version(1, 0, 2));
            var start = ResourceDiagnostics.Capture("Update20_Start");
            for (var round = 0; round < 20; round++)
            {
                var result = await service.CheckForUpdatesAsync(force: true);
                Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
            }

            var end = ResourceDiagnostics.Capture("Update20_End");
            AssertNonLinearNativeGrowth(start, end, maximumHandleDelta: 32, maximumGdiDelta: 16);
            WriteDelta(start, end);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void OcrContentCacheRemainsBoundedDuringOneThousandUniqueImages()
    {
        var cache = new OcrResultCache();
        for (var index = 0; index < 1_000; index++)
        {
            var image = CreatePattern(64, 48, index);
            var fingerprint = Assert.IsType<OcrResultCache.OcrImageFingerprint>(cache.CreateFingerprint(image));
            cache.Set(fingerprint, false, new OcrRecognitionResult(
                index.ToString(), "test", "test", 0, 64, 48, 64, 48, []));
        }

        Assert.Equal(24, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    private static void AssertNonLinearNativeGrowth(
        ResourceSnapshot start,
        ResourceSnapshot end,
        int maximumHandleDelta,
        int maximumGdiDelta)
    {
        Assert.InRange(end.HandleCount - start.HandleCount, int.MinValue, maximumHandleDelta);
        Assert.InRange(end.GdiObjectCount - start.GdiObjectCount, int.MinValue, maximumGdiDelta);
        Assert.InRange(end.UserObjectCount - start.UserObjectCount, int.MinValue, 16);
    }

    private void WriteDelta(ResourceSnapshot start, ResourceSnapshot end) =>
        output.WriteLine(
            "{0} -> {1}: Private {2:0.0}MB, Handles {3:+#;-#;0}, GDI {4:+#;-#;0}, USER {5:+#;-#;0}, Threads {6:+#;-#;0}",
            start.Label,
            end.Label,
            (end.PrivateMemoryBytes - start.PrivateMemoryBytes) / (1024D * 1024D),
            end.HandleCount - start.HandleCount,
            end.GdiObjectCount - start.GdiObjectCount,
            end.UserObjectCount - start.UserObjectCount,
            end.ThreadCount - start.ThreadCount);

    private static BitmapSource CreatePattern(int width, int height, int seed)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)((index + seed * 11) % 251);
            pixels[index + 1] = (byte)((index + seed * 17) % 253);
            pixels[index + 2] = (byte)((index + seed * 23) % 255);
            pixels[index + 3] = 255;
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static BitmapSource CreateQrCode(string text, int size)
    {
        var matrix = new MultiFormatWriter().encode(text, BarcodeFormat.QR_CODE, size, size, new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.MARGIN] = 4,
            [EncodeHintType.CHARACTER_SET] = "UTF-8"
        });
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var offset = (y * size + x) * 4;
            var value = matrix[x, y] ? (byte)0 : (byte)255;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }

        var source = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        source.Freeze();
        return source;
    }

    private sealed class RepeatingHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        });
    }
}
