namespace QingSnap.App.Services;

/// <summary>
/// Moves the real application by spacing wheel input over time, preferring
/// high-resolution pulses with a spaced-detent compatibility path.
/// No desktop image is animated. Applications that accumulate WHEEL_DELTA before
/// scrolling may still move in lines; their rendering policy is not changed.
/// </summary>
internal static class LongCaptureScrollDriver
{
    private const int MaximumWheelDelta = 1200;
    private static readonly TimeSpan PulseInterval = TimeSpan.FromMilliseconds(12);

    internal static IReadOnlyList<LongCaptureWheelPulse> CreatePulses(int wheelDelta, bool useWholeDetents = false)
    {
        if (wheelDelta is < -MaximumWheelDelta or > MaximumWheelDelta)
        {
            throw new ArgumentOutOfRangeException(nameof(wheelDelta));
        }

        if (wheelDelta == 0)
        {
            return Array.Empty<LongCaptureWheelPulse>();
        }

        var magnitude = Math.Abs(wheelDelta);
        var direction = Math.Sign(wheelDelta);
        if (useWholeDetents)
        {
            // Some applications discard fractional wheel packets rather than
            // accumulating them. Space real detents instead of reverting to an
            // instantaneous multi-detent batch. Preserve any final remainder.
            const int detent = 120;
            var count = (magnitude + detent - 1) / detent;
            var interval = count > 1 ? TimeSpan.FromMilliseconds(240D / (count - 1)) : TimeSpan.Zero;
            var detentPulses = new LongCaptureWheelPulse[count];
            for (var index = 0; index < count; index++)
            {
                detentPulses[index] = new LongCaptureWheelPulse(
                    direction * Math.Min(detent, magnitude - index * detent),
                    index == 0 ? TimeSpan.Zero : interval);
            }

            return detentPulses;
        }

        var sampleCount = Math.Clamp((magnitude + 29) / 30, 18, 26);
        var pulses = new List<LongCaptureWheelPulse>(sampleCount);
        var previous = 0;
        var pendingDelay = TimeSpan.Zero;
        for (var sample = 1; sample <= sampleCount; sample++)
        {
            if (sample > 1)
            {
                pendingDelay += PulseInterval;
            }

            var progress = sample / (double)sampleCount;
            var eased = progress * progress * (3 - 2 * progress);
            var cumulative = sample == sampleCount
                ? magnitude
                : (int)Math.Round(magnitude * eased, MidpointRounding.AwayFromZero);
            var delta = cumulative - previous;
            if (delta > 0)
            {
                pulses.Add(new LongCaptureWheelPulse(direction * delta, pendingDelay));
                pendingDelay = TimeSpan.Zero;
                previous = cumulative;
            }
        }

        return pulses;
    }

    internal static async Task<LongCaptureScrollResult> ScrollAsync(
        int wheelDelta,
        Func<int, bool> sendWheel,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<CancellationToken, Task>? waitUntilReadyAsync = null,
        bool useWholeDetents = false)
    {
        ArgumentNullException.ThrowIfNull(sendWheel);
        delayAsync ??= Task.Delay;
        var pulses = CreatePulses(wheelDelta, useWholeDetents);
        var sentDelta = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var pulse in pulses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pulse.DelayBefore > TimeSpan.Zero)
                {
                    // Relative waits deliberately do not catch up after a slow
                    // Dispatcher tick by dumping overdue wheel packets at once.
                    await delayAsync(pulse.DelayBefore, cancellationToken);
                }

                if (waitUntilReadyAsync is not null)
                {
                    // The HUD may temporarily own the pointer. Wait without
                    // sending or counting this packet, so its Stop/Cancel
                    // buttons remain at the same location until clicked.
                    await waitUntilReadyAsync(cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!sendWheel(pulse.WheelDelta))
                {
                    return new LongCaptureScrollResult(sentDelta, LongCaptureScrollStatus.InputFailed);
                }

                sentDelta += pulse.WheelDelta;
            }

            return new LongCaptureScrollResult(sentDelta, LongCaptureScrollStatus.Completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new LongCaptureScrollResult(sentDelta, LongCaptureScrollStatus.Stopped);
        }
    }
}

internal readonly record struct LongCaptureWheelPulse(int WheelDelta, TimeSpan DelayBefore);

internal readonly record struct LongCaptureScrollResult(int SentDelta, LongCaptureScrollStatus Status)
{
    internal bool Completed => Status == LongCaptureScrollStatus.Completed;

    internal bool InputFailed => Status == LongCaptureScrollStatus.InputFailed;

    internal bool Stopped => Status == LongCaptureScrollStatus.Stopped;
}

internal enum LongCaptureScrollStatus
{
    Completed,
    Stopped,
    InputFailed
}
