using System.IO.Compression;
using System.IO;
using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class LongCaptureAssemblerTests
{
    [Fact]
    public void FirstFrameBuildsPixelEquivalentImage()
    {
        var source = CreatePattern(96, 72, 17);
        var assembler = new LongCaptureAssembler();

        var result = assembler.AddFrame(source);
        var output = assembler.BuildImage();

        Assert.True(result.Accepted);
        Assert.Equal(1, assembler.FrameCount);
        Assert.Equal(source.PixelWidth, output.PixelWidth);
        Assert.Equal(source.PixelHeight, output.PixelHeight);
        Assert.Equal(0, LongCaptureAssembler.MeasureVisualDifference(source, output), 6);
    }

    [Fact]
    public void DuplicateFrameIsReportedAsBottomCandidate()
    {
        var source = CreatePattern(128, 96, 29);
        var assembler = new LongCaptureAssembler();
        assembler.AddFrame(source);

        var duplicate = assembler.AddFrame(source);

        Assert.False(duplicate.Accepted);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(1, assembler.FrameCount);
    }

    [Fact]
    public void SizeChangeIsRejectedWithoutChangingResult()
    {
        var assembler = new LongCaptureAssembler();
        assembler.AddFrame(CreatePattern(120, 90, 5));

        var result = assembler.AddFrame(CreatePattern(121, 90, 5));

        Assert.False(result.Accepted);
        Assert.Equal(LongCaptureFrameFailure.Unmatchable, result.Failure);
        Assert.Equal(1, assembler.FrameCount);
        Assert.Equal(90, assembler.OutputHeight);
    }

    [Fact]
    public void BuildAndReleaseDropsAssemblerPixelBuffers()
    {
        var assembler = new LongCaptureAssembler();
        assembler.AddFrame(CreatePattern(160, 120, 41));
        Assert.True(assembler.EstimatedRetainedBytes > 0);

        var output = assembler.BuildImageAndRelease();

        Assert.Equal(160, output.PixelWidth);
        Assert.Equal(120, output.PixelHeight);
        Assert.Equal(0, assembler.EstimatedRetainedBytes);
    }

    [Fact]
    public void DiagnosticBundleContainsSystemSnapshot()
    {
        DiagnosticLog.Initialize();
        DiagnosticLog.Info("Test", "bundle verification");
        var path = Path.Combine(Path.GetTempPath(), $"QingSnap-test-{Guid.NewGuid():N}.zip");
        try
        {
            DiagnosticLog.ExportBundle(path, new AppSettings());
            using var archive = ZipFile.OpenRead(path);
            Assert.Contains(archive.Entries, entry => entry.FullName == "system.json");
            Assert.Contains(archive.Entries, entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DiagnosticBundleCanIncludeFeedbackWithoutLogs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"QingSnap-feedback-test-{Guid.NewGuid():N}.zip");
        try
        {
            DiagnosticLog.ExportBundle(path, new AppSettings(), "测试反馈内容", includeLogs: false);
            using var archive = ZipFile.OpenRead(path);
            Assert.Contains(archive.Entries, entry => entry.FullName == "system.json");
            Assert.Contains(archive.Entries, entry => entry.FullName == "feedback.txt");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PinDockLayoutKeepsThumbnailInWorkAreaVertically(bool useLeftEdge)
    {
        var workArea = new Rectangle(100, 50, 1200, 800);
        var layout = PinDockLayoutCalculator.Calculate(workArea, 58, 44, -500, useLeftEdge, 1, 1);

        Assert.True(layout.RestingBounds.Top >= workArea.Top);
        Assert.True(layout.RestingBounds.Bottom <= workArea.Bottom);
        Assert.Equal(useLeftEdge ? workArea.Left : workArea.Right - 58, layout.RevealedBounds.Left);
        Assert.Equal(18, useLeftEdge
            ? layout.RestingBounds.Right - workArea.Left
            : workArea.Right - layout.RestingBounds.Left);
    }

    [Fact]
    public void OcrContentCacheReusesEquivalentBitmapInstances()
    {
        var first = CreatePattern(128, 80, 31);
        var second = CreatePattern(128, 80, 31);
        var cache = new OcrResultCache();
        var firstFingerprint = cache.CreateFingerprint(first);
        var secondFingerprint = cache.CreateFingerprint(second);
        var expected = new OcrRecognitionResult(
            "QingSnap",
            "test",
            "test",
            0,
            128,
            80,
            128,
            80,
            []);

        Assert.NotNull(firstFingerprint);
        Assert.Equal(firstFingerprint, secondFingerprint);
        cache.Set(firstFingerprint!.Value, false, expected);
        Assert.Same(expected, cache.TryGet(secondFingerprint!.Value, false));
    }

    [Fact]
    public void ScrollingFramesAppendExpectedDisplacementAndCanUndo()
    {
        var assembler = new LongCaptureAssembler(20);
        var first = CreateScrollingFrame(220, 260, 0);
        var second = CreateScrollingFrame(220, 260, 64);

        Assert.True(assembler.AddFrame(first).Accepted);
        var appended = assembler.AddFrame(second);

        Assert.True(appended.Accepted, appended.Message);
        Assert.InRange(appended.AppendedHeight, 60, 68);
        Assert.Equal(2, assembler.FrameCount);
        Assert.True(assembler.UndoLastFrame());
        Assert.Equal(1, assembler.FrameCount);
        Assert.Equal(260, assembler.OutputHeight);
    }

    [Theory]
    [InlineData(116)] // 55% of the 210px scrolling band, not the 260px viewport.
    [InlineData(126)] // 60%.
    [InlineData(136)] // 65%.
    public void LargeScrollingStepsKeepFixedChromeOnceAndPreserveEveryPixel(int displacement)
    {
        const int width = 220;
        const int viewportHeight = 260;
        var assembler = new LongCaptureAssembler(20);
        for (var frame = 0; frame < 4; frame++)
        {
            var result = assembler.AddFrame(CreateScrollingFrame(width, viewportHeight, frame * displacement));

            Assert.True(result.Accepted, result.Message);
            if (frame > 0)
            {
                Assert.Equal(displacement, result.AppendedHeight);
            }
        }

        var totalDisplacement = 3 * displacement;
        var output = assembler.BuildImage();
        Assert.Equal(4, assembler.FrameCount);
        Assert.Equal(viewportHeight + totalDisplacement, output.PixelHeight);
        Assert.Equal(
            ReadPixels(CreateExpectedScrollingImage(width, viewportHeight, totalDisplacement)),
            ReadPixels(output));

        Assert.True(assembler.UndoLastFrame());
        Assert.Equal(
            ReadPixels(CreateExpectedScrollingImage(width, viewportHeight, 2 * displacement)),
            ReadPixels(assembler.BuildImage()));
    }

    [Fact]
    public void RejectedLargeScrollKeepsCommittedPixelsAndAllowsTheNextReliableFrame()
    {
        var assembler = new LongCaptureAssembler(20);
        Assert.True(assembler.AddFrame(CreateScrollingFrame(220, 260, 0)).Accepted);
        Assert.True(assembler.AddFrame(CreateScrollingFrame(220, 260, 126)).Accepted);
        var acceptedPixels = ReadPixels(assembler.BuildImage());
        var acceptedHeight = assembler.OutputHeight;
        var retainedBytes = assembler.EstimatedRetainedBytes;

        var rejected = assembler.AddFrame(CreatePattern(220, 260, 87));

        Assert.False(rejected.Accepted);
        Assert.False(rejected.IsDuplicate);
        Assert.Equal(LongCaptureFrameFailure.Unmatchable, rejected.Failure);
        Assert.Equal(2, assembler.FrameCount);
        Assert.Equal(acceptedHeight, assembler.OutputHeight);
        Assert.Equal(retainedBytes, assembler.EstimatedRetainedBytes);
        Assert.Equal(acceptedPixels, ReadPixels(assembler.BuildImage()));

        var recovered = assembler.AddFrame(CreateScrollingFrame(220, 260, 252));
        Assert.True(recovered.Accepted, recovered.Message);
        Assert.Equal(126, recovered.AppendedHeight);
        Assert.True(assembler.UndoLastFrame());
        Assert.Equal(acceptedPixels, ReadPixels(assembler.BuildImage()));
    }

    [Fact]
    public void LargeScrollFollowedByShortBottomStepAndDuplicateKeepsExactOutput()
    {
        var assembler = new LongCaptureAssembler(20);
        foreach (var offset in new[] { 0, 126, 252, 269 })
        {
            var result = assembler.AddFrame(CreateScrollingFrame(220, 260, offset));
            Assert.True(result.Accepted, result.Message);
        }

        var duplicate = assembler.AddFrame(CreateScrollingFrame(220, 260, 269));

        Assert.False(duplicate.Accepted);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(4, assembler.FrameCount);
        Assert.Equal(529, assembler.OutputHeight);
        Assert.Equal(
            ReadPixels(CreateExpectedScrollingImage(220, 260, 269)),
            ReadPixels(assembler.BuildImage()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(240)]
    public void FlatFramesDoNotInventASeamWhenThereIsNoVisualEvidence(byte luminance)
    {
        const int width = 160;
        const int height = 180;
        var pixels = Enumerable.Repeat(luminance, width * height * 4).ToArray();
        for (var alpha = 3; alpha < pixels.Length; alpha += 4)
        {
            pixels[alpha] = 255;
        }

        var flat = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        flat.Freeze();
        var assembler = new LongCaptureAssembler();
        Assert.True(assembler.AddFrame(flat).Accepted);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = assembler.AddFrame(flat);
            Assert.False(result.Accepted);
            Assert.True(result.IsDuplicate);
        }

        Assert.Equal(1, assembler.FrameCount);
        Assert.Equal(height, assembler.OutputHeight);
        Assert.Equal(pixels, ReadPixels(assembler.BuildImage()));
    }

    [Theory]
    [InlineData(20, 120)]
    [InlineData(32, 128)]
    public void RepeatingRowsDoNotInventProgressWhenTheViewportLooksUnchanged(int period, int displacement)
    {
        const int width = 220;
        const int height = 260;
        var first = CreateRepeatingRowsFrame(width, height, 0, period);
        var repeated = CreateRepeatingRowsFrame(width, height, displacement, period);
        var assembler = new LongCaptureAssembler();
        Assert.True(assembler.AddFrame(first).Accepted);

        var result = assembler.AddFrame(repeated);

        Assert.False(result.Accepted);
        Assert.True(result.IsDuplicate);
        Assert.Equal(1, assembler.FrameCount);
        Assert.Equal(height, assembler.OutputHeight);
        Assert.Equal(ReadPixels(first), ReadPixels(assembler.BuildImage()));
    }

    [Fact]
    public void LargeStepCaptureStillStopsAtTheExistingEightyFrameLimit()
    {
        var assembler = new LongCaptureAssembler();
        for (var frame = 0; frame < 80; frame++)
        {
            var result = assembler.AddFrame(CreateScrollingFrame(96, 170, frame * 66));
            Assert.True(result.Accepted, $"Frame {frame + 1}: {result.Message}");
        }

        var retainedBytes = assembler.EstimatedRetainedBytes;
        var rejected = assembler.AddFrame(CreateScrollingFrame(96, 170, 80 * 66));

        Assert.False(rejected.Accepted);
        Assert.Equal(LongCaptureFrameFailure.SafetyLimit, rejected.Failure);
        Assert.Equal(80, assembler.FrameCount);
        Assert.Equal(170 + 79 * 66, assembler.OutputHeight);
        Assert.Equal(retainedBytes, assembler.EstimatedRetainedBytes);
    }

    [Theory]
    [InlineData("Balanced", "Balanced")]
    [InlineData("Instant", "Instant")]
    [InlineData("unexpected", "Instant")]
    public void SettingsNormalizeOcrPerformanceMode(string input, string expected)
    {
        var normalized = AppSettingsService.Normalize(new AppSettings
        {
            OcrPerformanceMode = input
        });

        Assert.Equal(expected, normalized.OcrPerformanceMode);
    }

    [Fact]
    public void NewSettingsStartWithoutOcr()
    {
        var normalized = AppSettingsService.Normalize(new AppSettings());

        Assert.Equal("None", normalized.OcrEngine);
        Assert.Equal(OcrModelManager.NoModel, normalized.OcrModel);
    }

    [Fact]
    public void LegacyAdvancedOcrSettingMigratesToSmallModel()
    {
        var normalized = AppSettingsService.Normalize(new AppSettings
        {
            OcrEngine = "Advanced",
            OcrModel = string.Empty
        });

        Assert.Equal("Advanced", normalized.OcrEngine);
        Assert.Equal(OcrModelManager.SmallModel, normalized.OcrModel);
    }

    [Theory]
    [InlineData("Tiny", "Tiny")]
    [InlineData("tiny", "Tiny")]
    [InlineData("Small", "Small")]
    [InlineData("invalid", "None")]
    public void SettingsNormalizeOcrModel(string input, string expected)
    {
        var normalized = AppSettingsService.Normalize(new AppSettings { OcrModel = input });

        Assert.Equal(expected, normalized.OcrModel);
    }

    [Fact]
    public async Task OcrRuntimeModuleCanBeInstalledAndRemoved()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"QingSnap-ocr-runtime-test-{Guid.NewGuid():N}");
        var packagePath = Path.Combine(Path.GetTempPath(), $"QingSnap-OCR-Module-test-{Guid.NewGuid():N}.zip");
        string[] requiredFiles =
        [
            "QingSnap.AdvancedOcr.dll",
            "QingSnap.AdvancedOcr.deps.json",
            "Clipper2Lib.dll",
            "RapidOcrNet.dll",
            "Microsoft.ML.OnnxRuntime.dll",
            "onnxruntime.dll",
            "onnxruntime_providers_shared.dll",
            "SkiaSharp.dll",
            "libSkiaSharp.dll",
            "System.Numerics.Tensors.dll"
        ];
        try
        {
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                foreach (var file in requiredFiles)
                {
                    archive.CreateEntry(file);
                }
            }

            var manager = new OcrRuntimeManager(dataDirectory);
            await manager.InstallAsync(packagePath);

            Assert.True(manager.IsInstalled);
            Assert.All(requiredFiles, file =>
                Assert.True(File.Exists(Path.Combine(manager.RuntimeDirectory, file))));

            await manager.DeleteAsync();
            Assert.False(Directory.Exists(manager.RuntimeDirectory));
        }
        finally
        {
            File.Delete(packagePath);
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, true);
            }
        }
    }

    [Fact]
    public void OcrModelsExposeIndependentDownloadSizes()
    {
        var manager = new OcrModelManager(Path.Combine(Path.GetTempPath(), $"QingSnap-ocr-model-test-{Guid.NewGuid():N}"));

        Assert.InRange(manager.GetDownloadSize(OcrModelManager.TinyModel), 7_000_000, 8_000_000);
        Assert.InRange(manager.GetDownloadSize(OcrModelManager.SmallModel), 32_000_000, 33_000_000);
        Assert.True(manager.GetDownloadSize(OcrModelManager.TinyModel) < manager.GetDownloadSize(OcrModelManager.SmallModel));
    }

    [Fact]
    public void OcrTextSelectionPreservesEnglishWordsFromCharacterBoxes()
    {
        const string expected = "Tiny 不能共用 Small 的字典。RapidOcrNet";
        var words = expected
            .Where(character => !char.IsWhiteSpace(character))
            .Select((character, index) => new OcrTextWord(
                index,
                0,
                character.ToString(),
                new OcrTextBounds(index * 8, 0, 7, 18)))
            .ToArray();
        var result = new OcrRecognitionResult(
            expected,
            "test",
            "test",
            1,
            500,
            100,
            500,
            100,
            [new OcrTextLine(0, expected, new OcrTextBounds(0, 0, 500, 20), words)]);

        var selected = words.Select(word => word.Index).ToHashSet();

        Assert.Equal(expected, OcrTextSelectionBuilder.Build(result, selected));
    }

    [Fact]
    public void OcrTextSelectionUsesGeometryWithoutSpacingAdjacentLettersOnFallback()
    {
        OcrTextWord[] words =
        [
            new(0, 0, "T", new OcrTextBounds(0, 0, 7, 18)),
            new(1, 0, "i", new OcrTextBounds(8, 0, 4, 18)),
            new(2, 0, "n", new OcrTextBounds(13, 0, 7, 18)),
            new(3, 0, "y", new OcrTextBounds(21, 0, 7, 18)),
            new(4, 0, "OCR", new OcrTextBounds(42, 0, 28, 18))
        ];
        var result = new OcrRecognitionResult(
            "unmatched line",
            "test",
            "test",
            1,
            100,
            30,
            100,
            30,
            [new OcrTextLine(0, "unmatched line", new OcrTextBounds(0, 0, 100, 20), words)]);

        Assert.Equal("Tiny OCR", OcrTextSelectionBuilder.Build(result, words.Select(word => word.Index).ToHashSet()));
    }

    [Fact]
    public void HistorySnapshotIncludesCachedOcrSearchText()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"QingSnap-history-test-{Guid.NewGuid():N}");
        try
        {
            var settings = new AppSettingsService(dataDirectory);
            using var history = new CaptureHistoryService(settings);
            var imagePath = history.Save(CreatePattern(80, 60, 9));

            history.SaveOcrText(imagePath, "可搜索的截屏文字 QingSnap");
            var snapshot = history.LoadSnapshot(10, CancellationToken.None);

            var item = Assert.Single(snapshot.Items);
            Assert.Contains("可搜索的截屏文字", item.SearchText, StringComparison.Ordinal);
            Assert.True(item.MetadataId > 0);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, true);
            }
        }
    }


    [Fact]
    public void HistoryCanSetFavoriteStateForMultipleItemsAtOnce()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"QingSnap-history-favorites-{Guid.NewGuid():N}");
        try
        {
            var settings = new AppSettingsService(dataDirectory);
            using var history = new CaptureHistoryService(settings);
            var firstPath = history.Save(CreatePattern(40, 30, 41));
            Thread.Sleep(20);
            var secondPath = history.Save(CreatePattern(40, 30, 42));

            history.SetFavoriteState([firstPath, secondPath], isFavorite: true);

            Assert.All(history.LoadSnapshot(10, CancellationToken.None).Items, item => Assert.True(item.IsFavorite));

            history.SetFavoriteState([firstPath, secondPath], isFavorite: false);

            Assert.All(history.LoadSnapshot(10, CancellationToken.None).Items, item => Assert.False(item.IsFavorite));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, true);
            }
        }
    }

    [Fact]
    public void HistoryBlankOcrResultStillMarksImageAsIndexed()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"QingSnap-history-empty-index-{Guid.NewGuid():N}");
        try
        {
            var settings = new AppSettingsService(dataDirectory);
            using var history = new CaptureHistoryService(settings);
            var imagePath = history.Save(CreatePattern(48, 36, 12));

            Assert.False(history.HasOcrIndex(imagePath));
            Assert.Contains(imagePath, history.FindImagesWithoutOcrIndex(CancellationToken.None));

            history.SaveOcrText(imagePath, "   ");

            Assert.True(history.HasOcrIndex(imagePath));
            Assert.DoesNotContain(imagePath, history.FindImagesWithoutOcrIndex(CancellationToken.None));
            Assert.Equal(string.Empty, Assert.Single(history.LoadSnapshot(10, CancellationToken.None).Items).SearchText);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, true);
            }
        }
    }

    [Fact]
    public void HistoryBackfillOnlyReturnsImagesWithoutOcrIndex()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"QingSnap-history-backfill-{Guid.NewGuid():N}");
        try
        {
            var settings = new AppSettingsService(dataDirectory);
            using var history = new CaptureHistoryService(settings);
            var indexedPath = history.Save(CreatePattern(64, 40, 21));
            Thread.Sleep(20);
            var missingPath = history.Save(CreatePattern(64, 40, 22));
            history.SaveOcrText(indexedPath, "indexed text");

            var missing = history.FindImagesWithoutOcrIndex(CancellationToken.None);

            Assert.Equal([missingPath], missing);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, true);
            }
        }
    }

    [Fact]
    public async Task LegacyFavoriteAndOcrFilesAreImportedIntoMetadataDatabase()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"QingSnap-history-migration-{Guid.NewGuid():N}");
        string imagePath;
        try
        {
            var settings = new AppSettingsService(dataDirectory);
            using (var original = new CaptureHistoryService(settings))
            {
                imagePath = original.Save(CreatePattern(72, 54, 31));
                Assert.True(original.ToggleFavorite(imagePath));
                original.SaveOcrText(imagePath, "旧版 OCR 索引");
            }

            foreach (var databaseFile in Directory.EnumerateFiles(dataDirectory, "history-metadata.db*"))
            {
                File.Delete(databaseFile);
            }

            string databasePath;
            using (var migrated = new CaptureHistoryService(settings))
            {
                var item = Assert.Single(migrated.LoadSnapshot(10, CancellationToken.None).Items);
                Assert.True(item.IsFavorite);
                Assert.Equal("旧版 OCR 索引", item.SearchText);
                databasePath = migrated.MetadataDatabasePath;
            }

            Assert.True(File.Exists(databasePath));
            using var store = new HistoryMetadataStore(dataDirectory);
            var metadata = Assert.Single(await store.LoadByPathsAsync([imagePath])).Value;
            Assert.True(metadata.IsFavorite);
            Assert.Equal(HistoryOcrIndexState.Indexed, metadata.OcrIndexState);
            Assert.Equal("旧版 OCR 索引", metadata.OcrText);
            Assert.False(string.IsNullOrWhiteSpace(metadata.ImageHash));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, true);
            }
        }
    }

    private static BitmapSource CreatePattern(int width, int height, int seed)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = (byte)((x * 13 + y * 7 + seed) % 251);
                pixels[offset + 1] = (byte)((x * 3 + y * 17 + seed * 2) % 253);
                pixels[offset + 2] = (byte)((x * 19 + y * 5 + seed * 3) % 255);
                pixels[offset + 3] = 255;
            }
        }

        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        source.Freeze();
        return source;
    }

    private static BitmapSource CreateScrollingFrame(int width, int height, int scrollOffset)
    {
        const int headerHeight = 26;
        const int footerHeight = 24;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                var worldY = y < headerHeight || y >= height - footerHeight
                    ? y
                    : y + scrollOffset;
                var fixedBand = y < headerHeight || y >= height - footerHeight;
                pixels[offset] = fixedBand
                    ? (byte)(20 + y % 17)
                    : (byte)((x * 7 + worldY * 11) % 251);
                pixels[offset + 1] = fixedBand
                    ? (byte)(60 + x % 23)
                    : (byte)((x * 13 + worldY * 5) % 253);
                pixels[offset + 2] = fixedBand
                    ? (byte)(90 + (x + y) % 31)
                    : (byte)((x * 3 + worldY * 17) % 255);
                pixels[offset + 3] = 255;
            }
        }

        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        source.Freeze();
        return source;
    }

    private static BitmapSource CreateExpectedScrollingImage(int width, int viewportHeight, int totalScrollOffset)
    {
        const int footerHeight = 24;
        var stride = width * 4;
        // The scrolling content's world coordinates equal output coordinates.
        // Only the fixed footer must retain its original viewport-local rows.
        var outputHeight = viewportHeight + totalScrollOffset;
        var pixels = ReadPixels(CreateScrollingFrame(width, outputHeight, 0));
        var firstFrame = ReadPixels(CreateScrollingFrame(width, viewportHeight, 0));
        Buffer.BlockCopy(
            firstFrame,
            (viewportHeight - footerHeight) * stride,
            pixels,
            (outputHeight - footerHeight) * stride,
            footerHeight * stride);
        var image = BitmapSource.Create(width, outputHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    private static byte[] ReadPixels(BitmapSource image)
    {
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static BitmapSource CreateRepeatingRowsFrame(int width, int height, int scrollOffset, int period)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var fixedBand = y < 26 || y >= height - 24;
            var row = fixedBand ? y : (y + scrollOffset) % period;
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = (byte)((row * 13 + x * 3) % 251);
                pixels[offset + 1] = (byte)((row * 7 + x * 5) % 253);
                pixels[offset + 2] = (byte)((row * 17 + x * 11) % 255);
                pixels[offset + 3] = 255;
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }
}
