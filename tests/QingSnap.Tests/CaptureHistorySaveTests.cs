using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class CaptureHistorySaveTests
{
    [Fact]
    public async Task QueuedSavesKeepEveryImageWithoutOverwritingAndDoNotHoldFilesOpen()
    {
        var directory = NewDirectory();
        try
        {
            var settings = new AppSettingsService(directory);
            using var history = new CaptureHistoryService(settings);
            await history.WaitForMigrationAsync();
            var images = Enumerable.Range(1, 20).Select(index => Image((byte)index)).ToArray();
            var paths = await Task.WhenAll(images.Select(image => history.SaveAsync(image)));

            Assert.Equal(20, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            for (var index = 0; index < paths.Length; index++)
            {
                using var stream = File.Open(paths[index], FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var pixel = new byte[4];
                decoder.Frames[0].CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                Assert.Equal(index + 1, pixel[0]);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedSaveDoesNotPoisonFollowingQueuedSave()
    {
        var directory = NewDirectory();
        try
        {
            var settings = new AppSettingsService(directory);
            using var history = new CaptureHistoryService(settings);
            await history.WaitForMigrationAsync();
            var blockedDirectory = Path.Combine(history.HistoryDirectory, DateTime.Now.ToString("yyyy-MM"));
            File.WriteAllText(blockedDirectory, "synthetic obstruction");
            await Assert.ThrowsAsync<IOException>(() => history.SaveAsync(Image(1)));
            File.Delete(blockedDirectory);
            var saved = await history.SaveAsync(Image(2));
            Assert.True(File.Exists(saved));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeDrainsPendingFileWritesAndRejectsNewSaves()
    {
        var directory = NewDirectory();
        try
        {
            var history = new CaptureHistoryService(new AppSettingsService(directory));
            await history.WaitForMigrationAsync();
            var saves = Enumerable.Range(0, 12).Select(index => history.SaveAsync(Image((byte)index))).ToArray();
            history.Dispose();
            foreach (var saved in saves)
            {
                Assert.True(saved.IsCompletedSuccessfully);
                Assert.True(File.Exists(await saved));
            }
            await Assert.ThrowsAsync<ObjectDisposedException>(() => history.SaveAsync(Image(1)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "QingSnap-capture-save", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static BitmapSource Image(byte value)
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { value, 2, 3, 255 }, 4);
        image.Freeze();
        return image;
    }
}
