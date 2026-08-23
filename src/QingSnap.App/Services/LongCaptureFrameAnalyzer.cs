using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingSnap.App.Services;

internal sealed class LongCaptureFrameAnalyzer
{
    private const double DuplicateThreshold = 2.2;
    private const int MinimumChangedRun = 24;
    private readonly int _minimumOverlapPercent;

    public LongCaptureFrameAnalyzer(int minimumOverlapPercent = 20)
    {
        _minimumOverlapPercent = Math.Clamp(minimumOverlapPercent, 12, 50);
    }

    public LongCaptureAnalysis Analyze(
        LongCaptureFrame previous,
        LongCaptureFrame current,
        PixelRegion? knownRegion = null)
    {
        if (previous.Width != current.Width || previous.Height != current.Height)
        {
            return LongCaptureAnalysis.Unreliable(PixelRegion.Full(current.Width, current.Height));
        }

        var wholeFrameDifference = MeasureDifference(previous, current);
        if (wholeFrameDifference <= DuplicateThreshold)
        {
            return new LongCaptureAnalysis(
                true,
                false,
                0,
                wholeFrameDifference,
                0,
                false,
                knownRegion ?? PixelRegion.Full(current.Width, current.Height));
        }

        var region = knownRegion ?? DetectChangedRegion(previous, current);
        var primary = FindPixelMatch(previous, current, region);
        if (IsPrimaryReliable(primary))
        {
            return new LongCaptureAnalysis(
                false,
                true,
                primary.Displacement,
                primary.Score,
                primary.Confidence,
                false,
                region);
        }

        var robust = FindSignatureMatch(previous, current, region);
        if (IsRobustReliable(robust))
        {
            return new LongCaptureAnalysis(
                false,
                true,
                robust.Displacement,
                robust.Score,
                robust.Confidence,
                true,
                region);
        }

        var failure = primary.Score <= robust.Score ? primary : robust;
        return new LongCaptureAnalysis(
            false,
            false,
            failure.Displacement,
            failure.Score,
            failure.Confidence,
            true,
            region);
    }

    public static double MeasureDifference(LongCaptureFrame first, LongCaptureFrame second)
    {
        if (first.Width != second.Width || first.Height != second.Height)
        {
            return double.PositiveInfinity;
        }

        var stepX = Math.Max(2, first.Width / 120);
        var stepY = Math.Max(2, first.Height / 90);
        long difference = 0;
        var samples = 0;
        for (var y = 0; y < first.Height; y += stepY)
        {
            for (var x = 0; x < first.Width; x += stepX)
            {
                difference += Math.Abs(first.LuminanceAt(x, y) - second.LuminanceAt(x, y));
                samples++;
            }
        }

        return samples == 0 ? double.PositiveInfinity : difference / (double)samples;
    }

    private static PixelRegion DetectChangedRegion(LongCaptureFrame previous, LongCaptureFrame current)
    {
        var changedRows = new bool[previous.Height];
        var rowStepX = Math.Max(1, previous.Width / 160);
        for (var y = 0; y < previous.Height; y++)
        {
            var total = 0;
            var changed = 0;
            var samples = 0;
            for (var x = 0; x < previous.Width; x += rowStepX)
            {
                var difference = Math.Abs(previous.LuminanceAt(x, y) - current.LuminanceAt(x, y));
                total += difference;
                changed += difference >= 8 ? 1 : 0;
                samples++;
            }

            changedRows[y] = samples > 0 &&
                             (total / (double)samples >= 1.2 || changed / (double)samples >= 0.035);
        }

        RemoveShortRuns(changedRows, Math.Max(2, previous.Height / 300));
        var (top, bottom, changedRowCount) = FindEnvelope(changedRows);
        if (changedRowCount < Math.Max(6, previous.Height / 80) ||
            bottom - top < Math.Max(MinimumChangedRun, previous.Height / 6))
        {
            return PixelRegion.Full(previous.Width, previous.Height);
        }

        var changedColumns = new bool[previous.Width];
        var rowStepY = Math.Max(1, (bottom - top) / 120);
        for (var x = 0; x < previous.Width; x++)
        {
            var total = 0;
            var changed = 0;
            var samples = 0;
            for (var y = top; y < bottom; y += rowStepY)
            {
                var difference = Math.Abs(previous.LuminanceAt(x, y) - current.LuminanceAt(x, y));
                total += difference;
                changed += difference >= 8 ? 1 : 0;
                samples++;
            }

            changedColumns[x] = samples > 0 &&
                                (total / (double)samples >= 2.4 || changed / (double)samples >= 0.08);
        }

        FillShortGaps(changedColumns, 4);
        RemoveShortRuns(changedColumns, Math.Max(8, previous.Width / 30));
        var (left, right) = FindLargestRun(changedColumns);
        if (right - left < Math.Max(24, previous.Width / 5))
        {
            left = 0;
            right = previous.Width;
        }
        else
        {
            left = Math.Max(0, left - 2);
            right = Math.Min(previous.Width, right + 2);
        }

        return new PixelRegion(left, top, right, bottom);
    }

    private CandidateMatch FindPixelMatch(
        LongCaptureFrame previous,
        LongCaptureFrame current,
        PixelRegion region)
    {
        var (minimumDisplacement, maximumDisplacement) = GetSearchRange(region);
        if (maximumDisplacement < minimumDisplacement)
        {
            return CandidateMatch.None;
        }

        var scores = new double[maximumDisplacement - minimumDisplacement + 1];
        Parallel.For(minimumDisplacement, maximumDisplacement + 1, displacement =>
        {
            scores[displacement - minimumDisplacement] = CalculatePixelScore(
                previous,
                current,
                region,
                displacement);
        });

        return SelectBest(scores, minimumDisplacement, 6);
    }

    private static double CalculatePixelScore(
        LongCaptureFrame previous,
        LongCaptureFrame current,
        PixelRegion region,
        int displacement)
    {
        var overlap = region.Height - displacement;
        if (overlap <= 0)
        {
            return double.PositiveInfinity;
        }

        var stepX = Math.Max(1, region.Width / 150);
        var stepY = Math.Max(1, overlap / 150);
        var rowScores = new List<double>();
        var sampledRows = 0;

        for (var y = region.Top + stepY; y < region.Bottom - displacement; y += stepY)
        {
            var previousY = y + displacement;
            double rowDifference = 0;
            double rowTexture = 0;
            var rowSamples = 0;
            for (var x = region.Left + stepX; x < region.Right; x += stepX)
            {
                var previousValue = previous.LuminanceAt(x, previousY);
                var currentValue = current.LuminanceAt(x, y);
                var previousHorizontal = Math.Abs(previousValue - previous.LuminanceAt(x - stepX, previousY));
                var currentHorizontal = Math.Abs(currentValue - current.LuminanceAt(x - stepX, y));
                var previousVertical = Math.Abs(previousValue - previous.LuminanceAt(x, previousY - stepY));
                var currentVertical = Math.Abs(currentValue - current.LuminanceAt(x, y - stepY));

                rowDifference += Math.Abs(previousValue - currentValue);
                rowDifference += 0.22 * Math.Abs(previousHorizontal - currentHorizontal);
                rowDifference += 0.22 * Math.Abs(previousVertical - currentVertical);
                rowTexture += previousHorizontal + currentHorizontal + previousVertical + currentVertical;
                rowSamples++;
            }

            sampledRows++;
            if (rowSamples >= 12 && rowTexture / rowSamples >= 2.5)
            {
                rowScores.Add(rowDifference / rowSamples);
            }
        }

        // Settings pages, document readers and code editors can have large flat
        // surfaces with only a few text rows carrying useful seam information.
        // Requiring a quarter of all sampled rows made those sparse layouts fall
        // back to the full frame, which duplicated fixed bottom toolbars.
        var minimumInformativeRows = Math.Max(8, sampledRows / 8);
        if (rowScores.Count < minimumInformativeRows)
        {
            return double.PositiveInfinity;
        }

        // Lazy-loaded pictures, videos and cursor overlays often invalidate only a
        // narrow horizontal band.  Score rows independently and discard the worst
        // quarter so that one dynamic band cannot move an otherwise clear seam.
        rowScores.Sort();
        var retainedRows = Math.Max(minimumInformativeRows, (int)Math.Ceiling(rowScores.Count * 0.75));
        return rowScores.Take(retainedRows).Average();
    }

    private CandidateMatch FindSignatureMatch(
        LongCaptureFrame previous,
        LongCaptureFrame current,
        PixelRegion region)
    {
        const int bins = 16;
        var previousSignatures = BuildRowSignatures(previous, region, bins);
        var currentSignatures = BuildRowSignatures(current, region, bins);
        var (minimumDisplacement, maximumDisplacement) = GetSearchRange(region);
        if (maximumDisplacement < minimumDisplacement)
        {
            return CandidateMatch.None;
        }

        var scores = new double[maximumDisplacement - minimumDisplacement + 1];
        Parallel.For(minimumDisplacement, maximumDisplacement + 1, displacement =>
        {
            var overlap = region.Height - displacement;
            var rowScores = new List<double>(overlap);
            for (var row = 1; row < overlap; row++)
            {
                var previousRow = row + displacement;
                double rowDifference = 0;
                for (var bin = 0; bin < bins; bin++)
                {
                    var previousValue = previousSignatures[previousRow, bin];
                    var currentValue = currentSignatures[row, bin];
                    rowDifference += Math.Abs(previousValue - currentValue);
                    rowDifference += 0.35 * Math.Abs(
                        (previousValue - previousSignatures[previousRow - 1, bin]) -
                        (currentValue - currentSignatures[row - 1, bin]));
                }

                rowScores.Add(rowDifference / bins);
            }

            if (rowScores.Count < 16)
            {
                scores[displacement - minimumDisplacement] = double.PositiveInfinity;
                return;
            }

            rowScores.Sort();
            var retainedRows = Math.Max(16, (int)Math.Ceiling(rowScores.Count * 0.70));
            scores[displacement - minimumDisplacement] = rowScores.Take(retainedRows).Average();
        });

        return SelectBest(scores, minimumDisplacement, 8);
    }

    private static double[,] BuildRowSignatures(LongCaptureFrame frame, PixelRegion region, int bins)
    {
        var signatures = new double[region.Height, bins];
        var binWidth = Math.Max(1, region.Width / bins);
        for (var row = 0; row < region.Height; row++)
        {
            var y = region.Top + row;
            for (var bin = 0; bin < bins; bin++)
            {
                var startX = region.Left + bin * binWidth;
                var endX = bin == bins - 1 ? region.Right : Math.Min(region.Right, startX + binWidth);
                var stepX = Math.Max(1, (endX - startX) / 12);
                var total = 0;
                var samples = 0;
                for (var x = startX; x < endX; x += stepX)
                {
                    total += frame.LuminanceAt(x, y);
                    samples++;
                }

                signatures[row, bin] = samples == 0 ? 0 : total / (double)samples;
            }
        }

        return signatures;
    }

    private static CandidateMatch SelectBest(double[] scores, int minimumDisplacement, int exclusionRadius)
    {
        var bestIndex = -1;
        var bestScore = double.PositiveInfinity;
        for (var index = 0; index < scores.Length; index++)
        {
            if (scores[index] < bestScore)
            {
                bestScore = scores[index];
                bestIndex = index;
            }
        }

        if (bestIndex < 0 || double.IsInfinity(bestScore))
        {
            return CandidateMatch.None;
        }

        var secondScore = double.PositiveInfinity;
        for (var index = 0; index < scores.Length; index++)
        {
            if (Math.Abs(index - bestIndex) <= exclusionRadius)
            {
                continue;
            }

            secondScore = Math.Min(secondScore, scores[index]);
        }

        var confidence = double.IsInfinity(secondScore)
            ? 1
            : Math.Clamp((secondScore - bestScore) / Math.Max(3, secondScore), 0, 1);
        return new CandidateMatch(minimumDisplacement + bestIndex, bestScore, confidence);
    }

    private (int Minimum, int Maximum) GetSearchRange(PixelRegion region)
    {
        var minimumOverlap = Math.Clamp(
            region.Height * _minimumOverlapPercent / 100,
            Math.Min(40, region.Height - 1),
            Math.Max(40, region.Height / 2));
        return (1, region.Height - minimumOverlap);
    }

    private static bool IsPrimaryReliable(CandidateMatch match) =>
        !double.IsInfinity(match.Score) &&
        (match.Score <= 3.2 && match.Confidence >= 0.04 ||
         match.Score <= 18 && match.Confidence >= 0.10);

    private static bool IsRobustReliable(CandidateMatch match) =>
        !double.IsInfinity(match.Score) &&
        (match.Score <= 2.5 && match.Confidence >= 0.06 ||
         match.Score <= 14 && match.Confidence >= 0.14);

    private static void FillShortGaps(bool[] values, int maximumGap)
    {
        var index = 0;
        while (index < values.Length)
        {
            if (values[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < values.Length && !values[index])
            {
                index++;
            }

            if (start > 0 && index < values.Length && index - start <= maximumGap)
            {
                Array.Fill(values, true, start, index - start);
            }
        }
    }

    private static void RemoveShortRuns(bool[] values, int minimumRun)
    {
        var index = 0;
        while (index < values.Length)
        {
            if (!values[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < values.Length && values[index])
            {
                index++;
            }

            if (index - start < minimumRun)
            {
                Array.Fill(values, false, start, index - start);
            }
        }
    }

    private static (int Start, int End) FindLargestRun(bool[] values)
    {
        var bestStart = 0;
        var bestEnd = 0;
        var index = 0;
        while (index < values.Length)
        {
            if (!values[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < values.Length && values[index])
            {
                index++;
            }

            if (index - start > bestEnd - bestStart)
            {
                bestStart = start;
                bestEnd = index;
            }
        }

        return (bestStart, bestEnd);
    }

    private static (int Start, int End, int Count) FindEnvelope(bool[] values)
    {
        var start = Array.FindIndex(values, value => value);
        if (start < 0)
        {
            return (0, 0, 0);
        }

        var end = Array.FindLastIndex(values, value => value) + 1;
        var count = 0;
        for (var index = start; index < end; index++)
        {
            if (values[index])
            {
                count++;
            }
        }

        return (start, end, count);
    }

    private readonly record struct CandidateMatch(int Displacement, double Score, double Confidence)
    {
        public static CandidateMatch None => new(0, double.PositiveInfinity, 0);
    }
}

internal sealed record LongCaptureFrame(int Width, int Height, int Stride, byte[] Pixels)
{
    public int LuminanceAt(int x, int y)
    {
        var index = y * Stride + x * 4;
        return (Pixels[index] * 29 + Pixels[index + 1] * 150 + Pixels[index + 2] * 77) >> 8;
    }

    public static LongCaptureFrame FromBitmapSource(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return new LongCaptureFrame(converted.PixelWidth, converted.PixelHeight, stride, pixels);
    }
}

internal readonly record struct PixelRegion(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public static PixelRegion Full(int width, int height) => new(0, 0, width, height);
}

internal sealed record LongCaptureAnalysis(
    bool IsDuplicate,
    bool IsReliable,
    int Displacement,
    double Score,
    double Confidence,
    bool UsedRobustFallback,
    PixelRegion Region)
{
    public static LongCaptureAnalysis Unreliable(PixelRegion region) =>
        new(false, false, 0, double.PositiveInfinity, 0, false, region);
}
