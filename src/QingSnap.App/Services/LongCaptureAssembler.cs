using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class LongCaptureAssembler
{
    private const int MaximumFrames = 80;
    private const int MaximumUndoCheckpoints = 3;
    private const int MaximumOutputHeight = 60_000;
    private const long MaximumOutputBytes = 256L * 1024L * 1024L;

    private readonly LongCaptureFrameAnalyzer _analyzer;
    private readonly List<FrameSegment> _segments = [];
    private readonly List<AssemblerCheckpoint> _checkpoints = [];
    private LongCaptureFrame? _firstFrame;
    private LongCaptureFrame? _lastFrame;
    private FrameSegment? _fixedBottom;
    private PixelRegion? _scrollRegion;
    private bool _layoutInitialized;

    public int FrameCount { get; private set; }

    public int OutputWidth { get; private set; }

    public int OutputHeight { get; private set; }

    public bool CanUndo => _checkpoints.Count > 0;

    public long EstimatedRetainedBytes =>
        _segments.Sum(segment => (long)segment.Pixels.Length) +
        (_fixedBottom?.Pixels.LongLength ?? 0) +
        (_firstFrame?.Pixels.LongLength ?? 0) +
        (_lastFrame?.Pixels.LongLength ?? 0) +
        _checkpoints.Sum(checkpoint =>
            checkpoint.LastFrame.Pixels.LongLength +
            (checkpoint.FixedBottom?.Pixels.LongLength ?? 0));

    public LongCaptureAssembler(int minimumOverlapPercent = 20)
    {
        _analyzer = new LongCaptureFrameAnalyzer(minimumOverlapPercent);
    }

    public LongCaptureFrameResult AddFrame(BitmapSource source)
    {
        var frame = LongCaptureFrame.FromBitmapSource(source);
        if (_lastFrame is null)
        {
            if (frame.Pixels.LongLength > MaximumOutputBytes)
            {
                return Failed("首屏尺寸过大，无法在安全内存范围内创建长截图。", LongCaptureFrameFailure.SafetyLimit);
            }

            OutputWidth = frame.Width;
            OutputHeight = frame.Height;
            FrameCount = 1;
            _segments.Add(new FrameSegment(frame.Pixels, frame.Height));
            _firstFrame = frame;
            _lastFrame = frame;
            return new LongCaptureFrameResult(
                true,
                false,
                frame.Height,
                OutputHeight,
                0,
                "首屏已记录，正在识别可滚动区域。");
        }

        if (FrameCount >= MaximumFrames)
        {
            return Failed("已达到 80 屏安全上限，请先完成当前长截图。", LongCaptureFrameFailure.SafetyLimit);
        }

        if (frame.Width != OutputWidth || frame.Height != _lastFrame.Height)
        {
            return Failed("截图区域尺寸发生变化，请取消后重新选择固定区域。", LongCaptureFrameFailure.Unmatchable);
        }

        var analysis = _analyzer.Analyze(_lastFrame, frame, _scrollRegion);
        if (analysis.IsDuplicate)
        {
            return new LongCaptureFrameResult(
                false,
                true,
                0,
                OutputHeight,
                analysis.Score,
                "当前画面与上一屏相同，可能已经到达页面底部。")
            {
                MatchConfidence = analysis.Confidence
            };
        }

        if (!analysis.IsReliable || analysis.Displacement <= 0)
        {
            return Failed(
                "未找到可靠的滚动重叠区域，将保留现有结果并切换为手动补截。",
                LongCaptureFrameFailure.Unmatchable,
                analysis.Score,
                analysis.Confidence,
                analysis.UsedRobustFallback);
        }

        if (OutputHeight + analysis.Displacement > MaximumOutputHeight)
        {
            return Failed(
                "拼接高度将超过 60000 px 安全上限，请先完成当前长截图。",
                LongCaptureFrameFailure.SafetyLimit,
                analysis.Score,
                analysis.Confidence,
                analysis.UsedRobustFallback);
        }

        var futureBytes = (long)OutputWidth * (OutputHeight + analysis.Displacement) * 4;
        if (futureBytes > MaximumOutputBytes)
        {
            return Failed(
                "拼接结果将超过 256 MB 内存上限，请先完成当前长截图。",
                LongCaptureFrameFailure.SafetyLimit,
                analysis.Score,
                analysis.Confidence,
                analysis.UsedRobustFallback);
        }

        _checkpoints.Add(new AssemblerCheckpoint(
            [.. _segments],
            _lastFrame,
            _fixedBottom,
            _scrollRegion,
            _layoutInitialized,
            FrameCount,
            OutputHeight));
        if (_checkpoints.Count > MaximumUndoCheckpoints)
        {
            _checkpoints.RemoveAt(0);
        }

        if (!_layoutInitialized)
        {
            InitializeLayout(frame, analysis.Region, analysis.Displacement);
            _firstFrame = null;
        }
        else
        {
            AppendScrollingRows(frame, analysis.Region, analysis.Displacement);
        }

        _scrollRegion = analysis.Region;
        _lastFrame = frame;
        FrameCount++;
        OutputHeight += analysis.Displacement;

        var fallbackLabel = analysis.UsedRobustFallback ? "（鲁棒匹配）" : string.Empty;
        return new LongCaptureFrameResult(
            true,
            false,
            analysis.Displacement,
            OutputHeight,
            analysis.Score,
            $"第 {FrameCount} 屏已拼接，新增 {analysis.Displacement} px{fallbackLabel}。")
        {
            MatchConfidence = analysis.Confidence,
            UsedRobustFallback = analysis.UsedRobustFallback
        };
    }

    public bool UndoLastFrame()
    {
        if (_checkpoints.Count == 0)
        {
            return false;
        }

        var checkpointIndex = _checkpoints.Count - 1;
        var checkpoint = _checkpoints[checkpointIndex];
        _checkpoints.RemoveAt(checkpointIndex);

        _segments.Clear();
        _segments.AddRange(checkpoint.Segments);
        _lastFrame = checkpoint.LastFrame;
        _firstFrame = checkpoint.LayoutInitialized ? null : checkpoint.LastFrame;
        _fixedBottom = checkpoint.FixedBottom;
        _scrollRegion = checkpoint.ScrollRegion;
        _layoutInitialized = checkpoint.LayoutInitialized;
        FrameCount = checkpoint.FrameCount;
        OutputHeight = checkpoint.OutputHeight;
        return true;
    }

    public BitmapSource BuildImage()
    {
        return BuildImageCore(false);
    }

    public BitmapSource BuildImageAndRelease()
    {
        return BuildImageCore(true);
    }

    private BitmapSource BuildImageCore(bool releaseBuffers)
    {
        if (_segments.Count == 0 || OutputWidth <= 0 || OutputHeight <= 0)
        {
            throw new InvalidOperationException("还没有可拼接的长截图画面。");
        }

        var stride = checked(OutputWidth * 4);
        var output = new WriteableBitmap(
            OutputWidth,
            OutputHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        var top = 0;
        foreach (var segment in _segments)
        {
            output.WritePixels(
                new System.Windows.Int32Rect(0, top, OutputWidth, segment.Height),
                segment.Pixels,
                stride,
                0);
            top += segment.Height;
        }

        if (_fixedBottom is not null)
        {
            output.WritePixels(
                new System.Windows.Int32Rect(0, top, OutputWidth, _fixedBottom.Height),
                _fixedBottom.Pixels,
                stride,
                0);
            top += _fixedBottom.Height;
        }

        if (top != OutputHeight)
        {
            throw new InvalidOperationException("长截图内部布局不一致，请重新截取。");
        }

        output.Freeze();
        if (releaseBuffers)
        {
            _segments.Clear();
            _checkpoints.Clear();
            _firstFrame = null;
            _lastFrame = null;
            _fixedBottom = null;
            _scrollRegion = null;
        }

        return output;
    }

    public static double MeasureVisualDifference(BitmapSource first, BitmapSource second)
    {
        var firstFrame = LongCaptureFrame.FromBitmapSource(first);
        var secondFrame = LongCaptureFrame.FromBitmapSource(second);
        return LongCaptureFrameAnalyzer.MeasureDifference(firstFrame, secondFrame);
    }

    private void InitializeLayout(LongCaptureFrame current, PixelRegion region, int displacement)
    {
        if (_firstFrame is null)
        {
            throw new InvalidOperationException("缺少长截图首屏。");
        }

        _segments.Clear();
        AddRows(_segments, _firstFrame, 0, region.Top);
        AddRows(_segments, _firstFrame, region.Top, region.Height);
        AppendScrollingRows(current, region, displacement);
        _layoutInitialized = true;
    }

    private void AppendScrollingRows(LongCaptureFrame frame, PixelRegion region, int displacement)
    {
        var appendStart = region.Bottom - displacement;
        AddRows(_segments, frame, appendStart, displacement);
        _fixedBottom = ExtractRows(frame, region.Bottom, frame.Height - region.Bottom);
    }

    private static void AddRows(List<FrameSegment> target, LongCaptureFrame frame, int top, int height)
    {
        var segment = ExtractRows(frame, top, height);
        if (segment is not null)
        {
            target.Add(segment);
        }
    }

    private static FrameSegment? ExtractRows(LongCaptureFrame frame, int top, int height)
    {
        if (height <= 0)
        {
            return null;
        }

        var pixels = new byte[checked(frame.Stride * height)];
        Buffer.BlockCopy(frame.Pixels, checked(frame.Stride * top), pixels, 0, pixels.Length);
        return new FrameSegment(pixels, height);
    }

    private LongCaptureFrameResult Failed(
        string message,
        LongCaptureFrameFailure failure,
        double score = double.PositiveInfinity,
        double confidence = 0,
        bool usedRobustFallback = false) => new(
            false,
            false,
            0,
            OutputHeight,
            score,
            message)
        {
            Failure = failure,
            MatchConfidence = confidence,
            UsedRobustFallback = usedRobustFallback
        };

    private sealed record FrameSegment(byte[] Pixels, int Height);

    private sealed record AssemblerCheckpoint(
        List<FrameSegment> Segments,
        LongCaptureFrame LastFrame,
        FrameSegment? FixedBottom,
        PixelRegion? ScrollRegion,
        bool LayoutInitialized,
        int FrameCount,
        int OutputHeight);
}
