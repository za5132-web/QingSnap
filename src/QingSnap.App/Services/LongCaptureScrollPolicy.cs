namespace QingSnap.App.Services;

/// <summary>
/// Calibrates wheel units against measured content displacement, never DPI or a
/// presumed number of screen pixels per wheel detent.
/// </summary>
internal sealed class LongCaptureScrollPolicy
{
    private const int Detent = 120;
    private const int MaximumMagnitude = 1200;
    private readonly double _targetFraction;

    internal LongCaptureScrollPolicy(int minimumOverlapPercent)
    {
        var overlap = Math.Clamp(minimumOverlapPercent, 12, 50) / 100D;
        _targetFraction = Math.Min(0.60, 1 - overlap - 0.05);
    }

    internal int NextDelta(
        int currentDelta,
        int appendedHeight,
        int scrollableHeight,
        double confidence,
        bool usedRobustFallback)
    {
        var direction = currentDelta > 0 ? 1 : -1;
        var magnitude = NormalizeMagnitude(currentDelta);
        if (appendedHeight <= 0 || scrollableHeight <= 0 || !double.IsFinite(confidence))
        {
            return direction * magnitude;
        }

        var fraction = appendedHeight / (double)scrollableHeight;
        if (fraction >= _targetFraction - 0.08 && fraction <= _targetFraction + 0.08)
        {
            return direction * magnitude;
        }

        var desired = magnitude * _targetFraction / fraction;
        var nextMagnitude = (int)Math.Clamp(
            Math.Round(desired / Detent, MidpointRounding.AwayFromZero) * Detent,
            Detent,
            MaximumMagnitude);

        if (nextMagnitude > magnitude)
        {
            // A very small final movement is usually a partial scroll at the
            // bottom, not a request for ever larger wheel inputs. Ambiguous or
            // fallback matches also must not calibrate an acceleration.
            if (appendedHeight < 24 || fraction < 0.12 || confidence < 0.15 || usedRobustFallback)
            {
                return direction * magnitude;
            }

            var maximumIncrease = Math.Max(Detent, magnitude / 4 / Detent * Detent);
            nextMagnitude = Math.Min(nextMagnitude, magnitude + maximumIncrease);
        }

        return direction * nextMagnitude;
    }

    internal static int ReduceDelta(int currentDelta)
    {
        var direction = currentDelta > 0 ? 1 : -1;
        var reducedMagnitude = NormalizeMagnitude(currentDelta) / 2 / Detent * Detent;
        return direction * Math.Max(Detent, reducedMagnitude);
    }

    private static int NormalizeMagnitude(int delta) =>
        (int)Math.Clamp(Math.Abs((long)delta) / Detent * Detent, Detent, MaximumMagnitude);
}
