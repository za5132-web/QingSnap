using System.IO;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class HistoryMetadataStoreTests
{
    [Fact]
    public async Task SchemaAndMetadataRoundTripPreserveCaptureFields()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var imagePath = Path.Combine(dataDirectory, "sample.png");
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3, 4]);
            using var store = new HistoryMetadataStore(dataDirectory);
            var now = DateTimeOffset.UtcNow;
            var metadata = new HistoryMetadata(
                0,
                imagePath,
                now,
                800,
                600,
                4,
                "PNG",
                false,
                true,
                "QingSnap Metadata",
                HistoryOcrIndexState.Indexed,
                "notepad",
                "测试窗口",
                "DISPLAY1",
                "\\\\.\\DISPLAY1",
                -120,
                80,
                800,
                600,
                "ABCDEF",
                now,
                now);

            store.QueueUpsert(metadata);
            await store.FlushAsync();
            var result = await store.LoadByPathsAsync([imagePath]);

            Assert.Equal(HistoryMetadataStore.CurrentSchemaVersion, await store.GetSchemaVersionAsync());
            var actual = Assert.Single(result).Value;
            Assert.Equal(Path.GetFullPath(imagePath), actual.FilePath);
            Assert.Equal((800, 600), (actual.Width, actual.Height));
            Assert.True(actual.IsFavorite);
            Assert.Equal(HistoryOcrIndexState.Indexed, actual.OcrIndexState);
            Assert.Equal("QingSnap Metadata", actual.OcrText);
            Assert.Equal("notepad", actual.SourceProcess);
            Assert.Equal(-120, actual.CaptureX);
            Assert.Equal("ABCDEF", actual.ImageHash);
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task RemoveMissingPrunesOnlyUnavailableFiles()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var keptPath = Path.Combine(dataDirectory, "kept.png");
            var removedPath = Path.Combine(dataDirectory, "removed.png");
            await File.WriteAllBytesAsync(keptPath, [1]);
            await File.WriteAllBytesAsync(removedPath, [2]);
            using var store = new HistoryMetadataStore(dataDirectory);
            store.QueueUpsert(CreateMetadata(keptPath));
            store.QueueUpsert(CreateMetadata(removedPath));
            store.QueueRemoveMissing(new HashSet<string>([keptPath], StringComparer.OrdinalIgnoreCase));
            await store.FlushAsync();

            var result = await store.LoadByPathsAsync([keptPath, removedPath]);

            Assert.True(result.ContainsKey(Path.GetFullPath(keptPath)));
            Assert.False(result.ContainsKey(Path.GetFullPath(removedPath)));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task CorruptDatabaseIsMovedAsideAndRecreated()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var databasePath = Path.Combine(dataDirectory, "history-metadata.db");
            await File.WriteAllTextAsync(databasePath, "not a sqlite database");
            using var store = new HistoryMetadataStore(dataDirectory);

            Assert.Equal(HistoryMetadataStore.CurrentSchemaVersion, await store.GetSchemaVersionAsync());
            Assert.NotEmpty(Directory.EnumerateFiles(dataDirectory, "history-metadata.db.corrupt-*"));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task TagsAreManyToManyDeduplicatedAndRemovalKeepsScreenshot()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var imagePath = Path.Combine(dataDirectory, "tagged.png");
            await File.WriteAllBytesAsync(imagePath, [1]);
            using var store = new HistoryMetadataStore(dataDirectory);
            store.QueueUpsert(CreateMetadata(imagePath));
            await store.AddTagsAsync(imagePath, ["工作", " 工作 ", "项目", "WORK"]);
            await store.AddTagsAsync(imagePath, ["work", "工作"]);

            var tags = Assert.Single(await store.LoadTagsByPathsAsync([imagePath])).Value;
            Assert.Equal(["WORK", "工作", "项目"], tags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase));

            await store.RemoveTagAsync(imagePath, "工作");

            var remaining = Assert.Single(await store.LoadTagsByPathsAsync([imagePath])).Value;
            Assert.Equal(["WORK", "项目"], remaining.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase));
            Assert.Single(await store.LoadByPathsAsync([imagePath]));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task DeletingScreenshotMetadataCascadesTagRelationships()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var imagePath = Path.Combine(dataDirectory, "deleted.png");
            await File.WriteAllBytesAsync(imagePath, [1]);
            using var store = new HistoryMetadataStore(dataDirectory);
            store.QueueUpsert(CreateMetadata(imagePath));
            await store.AddTagsAsync(imagePath, ["工作"]);

            await store.DeleteAsync(imagePath);

            Assert.Empty(await store.LoadByPathsAsync([imagePath]));
            Assert.Empty(await store.LoadTagsByPathsAsync([imagePath]));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    private static HistoryMetadata CreateMetadata(string filePath)
    {
        var now = DateTimeOffset.UtcNow;
        return new HistoryMetadata(
            0,
            filePath,
            now,
            1,
            1,
            1,
            "PNG",
            false,
            false,
            string.Empty,
            HistoryOcrIndexState.NotIndexed,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"QingSnap-metadata-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
