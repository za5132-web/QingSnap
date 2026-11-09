using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class LongCaptureScrollDriverTests
{
    [Theory]
    [InlineData(-120)]
    [InlineData(-360)]
    [InlineData(-720)]
    [InlineData(-1200)]
    [InlineData(17)]
    [InlineData(720)]
    [InlineData(1200)]
    public void PulsePlanConservesExactSignedWheelInput(int delta)
    {
        var pulses = LongCaptureScrollDriver.CreatePulses(delta);

        Assert.Equal(delta, pulses.Sum(pulse => pulse.WheelDelta));
        Assert.All(pulses, pulse =>
        {
            Assert.Equal(Math.Sign(delta), Math.Sign(pulse.WheelDelta));
            Assert.InRange(Math.Abs(pulse.WheelDelta), 1, 119);
            Assert.True(pulse.DelayBefore >= TimeSpan.Zero);
        });
        Assert.True(pulses.Count > 1);
        if (Math.Abs(delta) >= 120)
        {
            Assert.Equal(TimeSpan.Zero, pulses[0].DelayBefore);
        }
        Assert.All(pulses.Skip(1), pulse => Assert.True(pulse.DelayBefore >= TimeSpan.FromMilliseconds(12)));
    }

    [Fact]
    public void EverySupportedMagnitudeIsConservedAndReverseIsSymmetric()
    {
        for (var magnitude = 1; magnitude <= 1200; magnitude++)
        {
            var forward = LongCaptureScrollDriver.CreatePulses(-magnitude);
            var reverse = LongCaptureScrollDriver.CreatePulses(magnitude);
            Assert.Equal(-magnitude, forward.Sum(pulse => pulse.WheelDelta));
            Assert.Equal(magnitude, reverse.Sum(pulse => pulse.WheelDelta));
            Assert.Equal(forward.Select(pulse => -pulse.WheelDelta), reverse.Select(pulse => pulse.WheelDelta));
            Assert.Equal(forward.Select(pulse => pulse.DelayBefore), reverse.Select(pulse => pulse.DelayBefore));
        }
    }

    [Fact]
    public void TypicalMotionHasShortEasedRampWithoutDetentSizedJumps()
    {
        var pulses = LongCaptureScrollDriver.CreatePulses(-720);
        var durations = pulses.Sum(pulse => pulse.DelayBefore.TotalMilliseconds);

        Assert.InRange(durations, 220, 320);
        Assert.True(Math.Abs(pulses[0].WheelDelta) < Math.Abs(pulses[pulses.Count / 2].WheelDelta));
        Assert.True(Math.Abs(pulses[^1].WheelDelta) < Math.Abs(pulses[pulses.Count / 2].WheelDelta));
        Assert.InRange(pulses.Count, 18, 26);
    }

    [Fact]
    public void WholeDetentFallbackConservesEveryMagnitudeAndSpacesInputs()
    {
        for (var magnitude = 1; magnitude <= 1200; magnitude++)
        {
            var forward = LongCaptureScrollDriver.CreatePulses(-magnitude, useWholeDetents: true);
            var reverse = LongCaptureScrollDriver.CreatePulses(magnitude, useWholeDetents: true);
            Assert.Equal(-magnitude, forward.Sum(pulse => pulse.WheelDelta));
            Assert.Equal(magnitude, reverse.Sum(pulse => pulse.WheelDelta));
            Assert.All(forward.Take(forward.Count - 1), pulse => Assert.Equal(-120, pulse.WheelDelta));
            Assert.InRange(Math.Abs(forward[^1].WheelDelta), 1, 120);
            Assert.Equal(TimeSpan.Zero, forward[0].DelayBefore);
            Assert.Equal(forward.Select(pulse => -pulse.WheelDelta), reverse.Select(pulse => pulse.WheelDelta));
            Assert.All(forward.Skip(1), pulse => Assert.True(pulse.DelayBefore > TimeSpan.Zero));
            if (forward.Count > 1)
            {
                Assert.InRange(forward.Sum(pulse => pulse.DelayBefore.TotalMilliseconds), 239.9, 240.1);
            }
        }

        Assert.Empty(LongCaptureScrollDriver.CreatePulses(0, useWholeDetents: true));
    }

    [Fact]
    public async Task WholeDetentFallbackStillUsesDelayAndReadinessGatePerPacket()
    {
        var events = new List<string>();
        var delivered = new List<int>();
        var result = await LongCaptureScrollDriver.ScrollAsync(-360,
            delta =>
            {
                events.Add("send");
                delivered.Add(delta);
                return true;
            }, delayAsync: (_, _) =>
            {
                events.Add("wait");
                return Task.CompletedTask;
            }, waitUntilReadyAsync: _ =>
            {
                events.Add("ready");
                return Task.CompletedTask;
            }, useWholeDetents: true);

        Assert.True(result.Completed);
        Assert.Equal(-360, result.SentDelta);
        Assert.Equal(new[] { -120, -120, -120 }, delivered);
        Assert.Equal(new[] { "ready", "send", "wait", "ready", "send", "wait", "ready", "send" }, events);
    }

    [Fact]
    public async Task WholeDetentCancellationReportsOnlyDeliveredDetents()
    {
        using var cancellation = new CancellationTokenSource();
        var delivered = new List<int>();
        var result = await LongCaptureScrollDriver.ScrollAsync(-720,
            delta =>
            {
                delivered.Add(delta);
                cancellation.Cancel();
                return true;
            }, cancellation.Token, (_, _) => throw new InvalidOperationException("No subsequent wait"),
            useWholeDetents: true);

        Assert.True(result.Stopped);
        Assert.Equal(-120, result.SentDelta);
        Assert.Single(delivered);
    }

    [Fact]
    public void ZeroAndOutOfRangeInputsCannotCreateUnexpectedWheelEvents()
    {
        Assert.Empty(LongCaptureScrollDriver.CreatePulses(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LongCaptureScrollDriver.CreatePulses(1201));
        Assert.Throws<ArgumentOutOfRangeException>(() => LongCaptureScrollDriver.CreatePulses(int.MinValue));
    }

    [Fact]
    public async Task DriverAwaitsEveryIntervalRatherThanDumpingInputsInOneBatch()
    {
        var events = new List<string>();
        var delivered = new List<int>();
        var result = await LongCaptureScrollDriver.ScrollAsync(-720,
            delta =>
            {
                events.Add("send");
                delivered.Add(delta);
                return true;
            },
            delayAsync: (delay, _) =>
            {
                Assert.True(delay > TimeSpan.Zero);
                events.Add("wait");
                return Task.CompletedTask;
            });

        Assert.True(result.Completed);
        Assert.False(result.InputFailed);
        Assert.False(result.Stopped);
        Assert.Equal(-720, result.SentDelta);
        Assert.Equal(-720, delivered.Sum());
        Assert.Equal(delivered.Count * 2 - 1, events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            Assert.Equal(index % 2 == 0 ? "send" : "wait", events[index]);
        }
    }

    [Fact]
    public async Task PreCancelledMotionDoesNotSendAnyInput()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await LongCaptureScrollDriver.ScrollAsync(-720,
            _ => throw new InvalidOperationException("Unexpected input"), cancellation.Token);

        Assert.True(result.Stopped);
        Assert.Equal(0, result.SentDelta);
    }

    [Fact]
    public async Task StopBetweenPulsesReportsOnlyDeliveredInput()
    {
        using var cancellation = new CancellationTokenSource();
        var delivered = new List<int>();
        var result = await LongCaptureScrollDriver.ScrollAsync(-720,
            delta =>
            {
                delivered.Add(delta);
                cancellation.Cancel();
                return true;
            }, cancellation.Token, (_, _) => throw new InvalidOperationException("No further wait"));

        Assert.True(result.Stopped);
        Assert.Single(delivered);
        Assert.Equal(delivered.Sum(), result.SentDelta);
        Assert.InRange(Math.Abs(result.SentDelta), 1, 119);
    }

    [Fact]
    public async Task StopDuringWaitPreventsNextPulse()
    {
        using var cancellation = new CancellationTokenSource();
        var delivered = new List<int>();
        var result = await LongCaptureScrollDriver.ScrollAsync(-720,
            delta =>
            {
                delivered.Add(delta);
                return true;
            }, cancellation.Token, (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        Assert.True(result.Stopped);
        Assert.Single(delivered);
        Assert.Equal(delivered.Sum(), result.SentDelta);
    }

    [Fact]
    public async Task FailedInputStopsImmediatelyWithoutCountingFailedPacket()
    {
        var calls = 0;
        var successful = new List<int>();
        var result = await LongCaptureScrollDriver.ScrollAsync(720,
            delta =>
            {
                calls++;
                if (calls == 3)
                {
                    return false;
                }

                successful.Add(delta);
                return true;
            }, delayAsync: (_, _) => Task.CompletedTask);

        Assert.True(result.InputFailed);
        Assert.Equal(3, calls);
        Assert.Equal(2, successful.Count);
        Assert.Equal(successful.Sum(), result.SentDelta);
    }

    [Fact]
    public async Task ReadinessGateRunsAfterTheIntervalBeforeEachActualSend()
    {
        var events = new List<string>();
        var result = await LongCaptureScrollDriver.ScrollAsync(-720,
            _ =>
            {
                events.Add("send");
                return true;
            }, delayAsync: (_, _) =>
            {
                events.Add("wait");
                return Task.CompletedTask;
            }, waitUntilReadyAsync: _ =>
            {
                events.Add("ready");
                return Task.CompletedTask;
            });

        Assert.True(result.Completed);
        Assert.Equal(-720, result.SentDelta);
        Assert.Equal("ready", events[0]);
        Assert.Equal("send", events[1]);
        for (var index = 2; index < events.Count; index += 3)
        {
            Assert.Equal("wait", events[index]);
            Assert.Equal("ready", events[index + 1]);
            Assert.Equal("send", events[index + 2]);
        }
    }

    [Fact]
    public async Task CancellationWhileWaitingForInitialReadinessSendsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var pending = LongCaptureScrollDriver.ScrollAsync(-720,
            _ =>
            {
                sends++;
                return true;
            }, cancellation.Token, (_, _) => Task.CompletedTask,
            async token =>
            {
                gateEntered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        await gateEntered.Task;
        Assert.Equal(0, sends);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        var result = await pending;
        Assert.True(result.Stopped);
        Assert.Equal(0, result.SentDelta);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task CancellationWhileHoveringHudCountsOnlyAlreadyDeliveredPackets()
    {
        using var cancellation = new CancellationTokenSource();
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<int>();
        var gates = 0;
        var pending = LongCaptureScrollDriver.ScrollAsync(-720,
            delta =>
            {
                delivered.Add(delta);
                return true;
            }, cancellation.Token, (_, _) => Task.CompletedTask,
            async token =>
            {
                if (++gates == 2)
                {
                    gateEntered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            });

        await gateEntered.Task;
        Assert.Single(delivered);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        var result = await pending;
        Assert.True(result.Stopped);
        Assert.Equal(delivered.Sum(), result.SentDelta);
        Assert.Single(delivered);
    }

    [Fact]
    public async Task ReadinessReleaseResumesRemainingPacketsWithoutDuplication()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new List<int>();
        var gates = 0;
        var pending = LongCaptureScrollDriver.ScrollAsync(-720,
            delta =>
            {
                delivered.Add(delta);
                return true;
            }, delayAsync: (_, _) => Task.CompletedTask,
            waitUntilReadyAsync: async _ =>
            {
                if (++gates == 2)
                {
                    gateEntered.SetResult();
                    await release.Task;
                }
            });

        await gateEntered.Task;
        Assert.Single(delivered);
        release.SetResult();
        var result = await pending;
        Assert.True(result.Completed);
        Assert.Equal(-720, result.SentDelta);
        Assert.Equal(LongCaptureScrollDriver.CreatePulses(-720).Select(pulse => pulse.WheelDelta), delivered);
    }

    [Fact]
    public async Task DriverDoesNotSwallowUnexpectedFailuresNeededBySessionLogging()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => LongCaptureScrollDriver.ScrollAsync(-120,
            _ => throw new InvalidOperationException("Synthetic send failure")));
    }
}
