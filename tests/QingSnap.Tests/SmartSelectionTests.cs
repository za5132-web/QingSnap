using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class SmartSelectionTests
{
    private sealed class ProbeNode(string name, bool containsPoint = true)
    {
        public string Name { get; } = name;
        public bool ContainsPoint { get; } = containsPoint;
        public ProbeNode? FirstChild { get; set; }
        public ProbeNode? NextSibling { get; set; }
    }

    private static SmartElementRequest Request(long version = 1, Guid? session = null) =>
        new(session ?? Guid.NewGuid(), version, (nint)10, 123,
            new Rectangle(-1200, -100, 1200, 900), new Point(-300, 200),
            new Rectangle(-1000, 0, 1000, 800));

    [Fact]
    public void NormalizeUsesPhysicalNegativeCoordinatesAndSmallestUsefulRegionFirst()
    {
        var values = new[]
        {
            new SmartSelectionCandidate(new Rectangle(-1200, -100, 1200, 900), SmartSelectionKind.Window),
            new SmartSelectionCandidate(new Rectangle(-500, 100, 400, 300), SmartSelectionKind.Element),
            new SmartSelectionCandidate(new Rectangle(-500, 100, 400, 300), SmartSelectionKind.NativeChild),
            new SmartSelectionCandidate(new Rectangle(10, 10, 200, 100), SmartSelectionKind.Element),
            new SmartSelectionCandidate(new Rectangle(-301, 199, 2, 2), SmartSelectionKind.Element)
        };
        var actual = SmartSelectionCandidates.Normalize(values, new Point(-300, 200), new Rectangle(-1000, 0, 1000, 800));
        Assert.Equal(2, actual.Count);
        Assert.Equal(new Rectangle(-500, 100, 400, 300), actual[0].Bounds);
        Assert.Equal(new Rectangle(-1000, 0, 1000, 800), actual[1].Bounds);
    }

    [Fact]
    public void RequestIdentityRejectsOldVersionDifferentSessionAndReusedWindowHandle()
    {
        var request = Request();
        Assert.True(SmartSelectionCandidates.IsCurrent(request, request.SessionId, 1, 10, 123, request.RootBounds));
        Assert.False(SmartSelectionCandidates.IsCurrent(request, request.SessionId, 2, 10, 123, request.RootBounds));
        Assert.False(SmartSelectionCandidates.IsCurrent(request, Guid.NewGuid(), 1, 10, 123, request.RootBounds));
        Assert.False(SmartSelectionCandidates.IsCurrent(request, request.SessionId, 1, 10, 124, request.RootBounds));
        Assert.False(SmartSelectionCandidates.IsCurrent(request, request.SessionId, 1, 11, 123, request.RootBounds));
        Assert.False(SmartSelectionCandidates.IsCurrent(request, request.SessionId, 1, 10, 123,
            request.RootBounds with { X = -1199 }));
    }

    [Theory]
    [InlineData(0, 3, 120, 1)]
    [InlineData(1, 3, -120, 0)]
    [InlineData(0, 3, -120, 0)]
    [InlineData(2, 3, 120, 2)]
    [InlineData(0, 0, 120, -1)]
    public void AltWheelMovesAlongHierarchyWithoutWrapping(int index, int count, int delta, int expected) =>
        Assert.Equal(expected, SmartSelectionCandidates.CycleIndex(index, count, delta));

    [Fact]
    public void AutomationBoundsAreRoundedOutwardsInPhysicalPixels()
    {
        Assert.True(SmartElementProbeWorker.TryPhysicalBounds(new System.Windows.Rect(-300.25, -20.75, 100.5, 20.5),
            out var bounds));
        Assert.Equal(Rectangle.FromLTRB(-301, -21, -199, 0), bounds);
        Assert.False(SmartElementProbeWorker.TryPhysicalBounds(System.Windows.Rect.Empty, out _));
        Assert.False(SmartElementProbeWorker.TryPhysicalBounds(new System.Windows.Rect(double.NaN, 0, 20, 20), out _));
    }

    [Fact]
    public void ObsoleteSmartSelectionSwitchesAreIgnoredWithoutLosingOtherOldSettings()
    {
        var old = JsonSerializer.Deserialize<AppSettings>(
            "{\"SmartWindowSelection\":false,\"SmartElementSelection\":false,\"AutoCopy\":false}")!;
        var normalized = AppSettingsService.Normalize(old);
        Assert.False(normalized.AutoCopy);
        var serialized = JsonSerializer.Serialize(normalized);
        Assert.DoesNotContain("SmartWindowSelection", serialized);
        Assert.DoesNotContain("SmartElementSelection", serialized);
    }

    [Fact]
    public void WorkerCoalescesOneHundredRequestsAndUsesMtaWithoutReturningStaleResult()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var allMta = true;
        using var worker = new SmartElementProbeWorker((request, _) =>
        {
            allMta &= Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA;
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
            return [new SmartSelectionCandidate(request.RootBounds, SmartSelectionKind.Element)];
        });
        var first = Request();
        try
        {
            Assert.True(worker.Submit(first));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            for (var version = 2; version <= 101; version++)
                Assert.True(worker.Submit(first with { Version = version }));
            Assert.Equal(1, worker.PendingCount);
            release.Set();
            SmartElementResult? result = null;
            Assert.True(SpinWait.SpinUntil(() => (result = worker.TakeResult(first.SessionId, 101)) is not null,
                TimeSpan.FromSeconds(3)));
            Assert.Equal(101, result!.Request.Version);
            Assert.Equal(2, calls);
            Assert.True(allMta);
            Assert.Null(worker.TakeResult(first.SessionId, 1));
            Assert.Equal(0, worker.PendingCount);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void SessionCancellationDiscardsResultsAndDisposeDoesNotWaitForBlockedProvider()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        using var worker = new SmartElementProbeWorker((_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            completed.Set();
            return [];
        });
        var request = Request();
        try
        {
            Assert.True(worker.Submit(request));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            worker.CancelSession(request.SessionId);
            var watch = Stopwatch.StartNew();
            worker.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500));
            Assert.Null(worker.TakeResult(request.SessionId, request.Version));
            Assert.False(worker.Submit(request));
        }
        finally
        {
            release.Set();
            Assert.True(completed.Wait(TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public void SlowProviderDisablesFineRecognitionInsteadOfSpawningReplacementThreads()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        var calls = 0;
        using var worker = new SmartElementProbeWorker((_, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            completed.Set();
            return [];
        }, TimeSpan.FromMilliseconds(30));
        try
        {
            Assert.True(worker.Submit(Request()));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(SpinWait.SpinUntil(() => !worker.IsAvailable, TimeSpan.FromSeconds(3)));
            for (var index = 0; index < 100; index++) Assert.False(worker.Submit(Request(index)));
            Assert.Equal(1, calls);
            Assert.Equal(0, worker.PendingCount);
        }
        finally
        {
            release.Set();
            Assert.True(completed.Wait(TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public void ProviderExceptionDoesNotStopWorkerOrEscapeIntoUi()
    {
        var calls = 0;
        using var entered = new ManualResetEventSlim();
        using var worker = new SmartElementProbeWorker((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                throw new InvalidOperationException("synthetic provider failure");
            }
            return [];
        });
        var request = Request();
        Assert.True(worker.Submit(request));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(worker.Submit(request with { Version = 2 }));
        Assert.True(SpinWait.SpinUntil(() => worker.TakeResult(request.SessionId, 2) is not null,
            TimeSpan.FromSeconds(3)));
        Assert.True(worker.IsAvailable);
    }

    [Fact]
    public void RepeatedProviderExceptionsOnlyDisableTheFailingRoot()
    {
        var failures = 0;
        using var worker = new SmartElementProbeWorker((request, _) =>
        {
            if (request.RootWindow != 10) return [];
            Interlocked.Increment(ref failures);
            throw new InvalidOperationException("synthetic repeatedly failing provider");
        });
        var request = Request();
        for (var version = 1; version <= 3; version++)
        {
            Assert.True(worker.Submit(request with { Version = version }));
            var expected = version;
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref failures) >= expected, TimeSpan.FromSeconds(3)));
        }
        Assert.True(SpinWait.SpinUntil(() => !worker.IsRootAvailable(10, 123), TimeSpan.FromSeconds(3)));
        Assert.True(worker.IsAvailable);
        Assert.False(worker.Submit(request with { Version = 4 }));
        Assert.Equal(3, failures);
        var otherRoot = request with { RootWindow = 11, Version = 5 };
        Assert.True(worker.Submit(otherRoot));
        Assert.True(SpinWait.SpinUntil(() => worker.TakeResult(request.SessionId, 5) is not null,
            TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void TraversalRefinesHitBranchBeforeSpendingBudgetOnOtherSiblings()
    {
        var root = new ProbeNode("root");
        var hit = root.FirstChild = new ProbeNode("hit-pane");
        hit.FirstChild = new ProbeNode("document");
        var tail = hit;
        for (var index = 0; index < 100; index++) tail = tail.NextSibling = new ProbeNode($"unrelated-{index}", false);
        var visited = new List<string>();
        var count = SmartElementProbeWorker.TraversePointPath(root, node => node.FirstChild, node => node.NextSibling,
            node => { visited.Add(node.Name); return node.ContainsPoint; }, () => true, CancellationToken.None,
            maxNodes: 2);
        Assert.Equal(2, count);
        Assert.Equal(["hit-pane", "document"], visited);
    }

    [Fact]
    public void TraversalKeepsDepthAndNodeBoundsAndSkipsNonHitSubtrees()
    {
        var root = new ProbeNode("root");
        var excluded = root.FirstChild = new ProbeNode("excluded", false);
        excluded.FirstChild = new ProbeNode("must-not-visit");
        var path = excluded.NextSibling = new ProbeNode("pane");
        var document = path.FirstChild = new ProbeNode("document");
        document.FirstChild = new ProbeNode("beyond-depth");
        var visited = new List<string>();
        Assert.Equal(3, SmartElementProbeWorker.TraversePointPath(root,
            node => node.FirstChild, node => node.NextSibling,
            node => { visited.Add(node.Name); return node.ContainsPoint; }, () => true,
            CancellationToken.None, maxDepth: 2));
        Assert.Equal(["excluded", "pane", "document"], visited);
    }

    [Fact]
    public void TraversalDoesNotStartAnotherProviderCallAfterSoftBudgetExpires()
    {
        var root = new ProbeNode("root") { FirstChild = new ProbeNode("pane") };
        var budgetAvailable = true;
        var calls = 0;
        Assert.Equal(1, SmartElementProbeWorker.TraversePointPath(root,
            node => { calls++; return node.FirstChild; },
            node => { calls++; return node.NextSibling; },
            _ => { budgetAvailable = false; return true; }, () => budgetAvailable, CancellationToken.None));
        Assert.Equal(1, calls);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SmartElementProbeWorker.TraversePointPath(root,
            node => node.FirstChild, node => node.NextSibling, _ => true, () => true, cancellation.Token));
    }
}
