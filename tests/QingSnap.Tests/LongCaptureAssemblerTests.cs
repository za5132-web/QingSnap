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
            var history = new CaptureHistoryService(settings);
            var imagePath = history.Save(CreatePattern(80, 60, 9));

            history.SaveOcrText(imagePath, "可搜索的截屏文字 QingSnap");
            var snapshot = history.LoadSnapshot(10, CancellationToken.None);

            var item = Assert.Single(snapshot.Items);
            Assert.Contains("可搜索的截屏文字", item.SearchText, StringComparison.Ordinal);
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
            var history = new CaptureHistoryService(settings);
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
            var history = new CaptureHistoryService(settings);
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
}
