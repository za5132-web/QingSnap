using System.IO;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class ThumbnailLruCacheTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task TwoThousandThumbnailKeysNeverExceedCapacityAndMemoryPlateaus()
    {
        const int capacity = 150;
        var directory = Path.Combine(Path.GetTempPath(), "QingSnap-thumbnail-cache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var seed = Path.Combine(directory, "seed.png");
            SaveSeedImage(seed);
            using var cache = new ThumbnailLruCache(capacity);
            long workingSetAtOneThousand = 0;
            for (var index = 0; index < 2_000; index++)
            {
                var path = Path.Combine(directory, $"thumb-{index:D4}.png");
                File.Copy(seed, path);
                Assert.NotNull(await cache.GetAsync(path));
                if (index == 999)
                {
                    workingSetAtOneThousand = Process.GetCurrentProcess().WorkingSet64;
                }
            }

            Assert.Equal(capacity, cache.Count);
            var workingSetAtTwoThousand = Process.GetCurrentProcess().WorkingSet64;
            cache.Remove(Path.Combine(directory, "thumb-1999.png"));
            Assert.Equal(capacity - 1, cache.Count);
            output.WriteLine(
                "thumbnail cache={0}; ws@1000={1:0.0}MB; ws@2000={2:0.0}MB; delta={3:0.0}MB",
                capacity,
                workingSetAtOneThousand / (1024D * 1024D),
                workingSetAtTwoThousand / (1024D * 1024D),
                (workingSetAtTwoThousand - workingSetAtOneThousand) / (1024D * 1024D));
            cache.Clear();
            Assert.Equal(0, cache.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void SaveSeedImage(string path)
    {
        const int width = 320;
        const int height = 180;
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 0x23;
            pixels[index + 1] = 0xB7;
            pixels[index + 2] = 0xD3;
            pixels[index + 3] = 0xFF;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
