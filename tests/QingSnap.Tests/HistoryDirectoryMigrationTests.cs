using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class HistoryDirectoryMigrationTests
{
    [Fact]
    public async Task MovingHistoryDirectoryPreservesMetadataByImageHash()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "QingSnap-history-move", Guid.NewGuid().ToString("N"));
        var firstHistory = Path.Combine(dataDirectory, "History-A");
        var secondHistory = Path.Combine(dataDirectory, "History-B");
        Directory.CreateDirectory(firstHistory);
        Directory.CreateDirectory(secondHistory);
        try
        {
            var settings = new AppSettingsService(dataDirectory);
            settings.Save(settings.Current with { HistoryDirectory = firstHistory, OutputFormat = "PNG" });
            using var history = new CaptureHistoryService(settings);
            await history.WaitForMigrationAsync();

            var source = CreateImage();
            var oldPath = history.Save(source, new CaptureHistoryContext(
                false,
                "chrome.exe",
                "迁移前的窗口标题",
                "DISPLAY1",
                "\\\\.\\DISPLAY1",
                -100,
                40,
                2,
                2));
            await history.AddTagsAsync(oldPath, ["工作"]);
            history.SetFavoriteState([oldPath], true);
            history.SaveOcrText(oldPath, "可迁移的 OCR 文字");

            await WaitForImageHashAsync(dataDirectory, oldPath);
            var newPath = Path.Combine(secondHistory, Path.GetFileName(oldPath));
            File.Move(oldPath, newPath);

            settings.Save(settings.Current with { HistoryDirectory = secondHistory });
            await history.WaitForMigrationAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var item = Assert.Single(history.LoadSnapshot(10, CancellationToken.None).Items);

            Assert.Equal(Path.GetFullPath(newPath), item.FilePath);
            Assert.True(item.IsFavorite);
            Assert.Equal("可迁移的 OCR 文字", item.SearchText);
            Assert.Equal("chrome.exe", item.SourceProcess);
            Assert.Equal("迁移前的窗口标题", item.SourceWindowTitle);
            Assert.Contains("工作", item.Tags ?? []);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static async Task WaitForImageHashAsync(string dataDirectory, string imagePath)
    {
        using var metadata = new HistoryMetadataStore(dataDirectory);
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            var records = await metadata.LoadByPathsAsync([imagePath]);
            if (records.TryGetValue(Path.GetFullPath(imagePath), out var value) &&
                !string.IsNullOrWhiteSpace(value.ImageHash))
            {
                return;
            }

            await Task.Delay(40);
        }

        throw new TimeoutException("截图哈希没有在预期时间内完成。 ");
    }

    private static BitmapSource CreateImage()
    {
        var pixels = new byte[]
        {
            20, 40, 60, 255, 80, 100, 120, 255,
            140, 160, 180, 255, 200, 220, 240, 255
        };
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        image.Freeze();
        return image;
    }
}
