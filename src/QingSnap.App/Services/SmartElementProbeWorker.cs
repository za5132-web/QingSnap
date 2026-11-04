using System.Diagnostics;
using System.Drawing;
using System.Windows.Automation;
using QingSnap.App.Infrastructure;
using AutomationRect = System.Windows.Rect;

namespace QingSnap.App.Services;

/// <summary>
/// One lazy MTA thread for the process, not one thread per overlay/request. A bad cross-process
/// provider cannot block input or accumulate replacement threads. Requests/results never own a Window.
/// Cancellation and budgets are cooperative: a UIA/COM call already in progress cannot be hard-aborted.
/// </summary>
internal sealed class SmartElementProbeWorker : IDisposable
{
    private static readonly Lazy<SmartElementProbeWorker> SharedWorker = new(() => new());
    private readonly object _sync = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly Func<SmartElementRequest, CancellationToken, IReadOnlyList<SmartSelectionCandidate>> _probe;
    private readonly TimeSpan _blockedThreshold;
    private SmartElementRequest? _pending;
    private SmartElementRequest? _latestRequest;
    private SmartElementResult? _result;
    private CancellationTokenSource? _activeCancellation;
    private long _activeStarted;
    private bool _disposed;
    private bool _blocked;
    private int _failureCount;
    private readonly Dictionary<(nint Window, uint ProcessId), int> _rootFailures = [];

    public static SmartElementProbeWorker Shared => SharedWorker.Value;

    public static void ShutdownShared()
    {
        if (SharedWorker.IsValueCreated) SharedWorker.Value.Dispose();
    }

    internal SmartElementProbeWorker(
        Func<SmartElementRequest, CancellationToken, IReadOnlyList<SmartSelectionCandidate>>? probe = null,
        TimeSpan? blockedThreshold = null)
    {
        _probe = probe ?? Probe;
        _blockedThreshold = blockedThreshold ?? TimeSpan.FromSeconds(3);
        _thread = new Thread(Run) { IsBackground = true, Name = "QingSnap smart selection UIA" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public bool Submit(SmartElementRequest request)
    {
        lock (_sync)
        {
            if (!CheckAvailable() || IsRootBlocked(request.RootWindow, request.RootProcessId)) return false;
            _activeCancellation?.Cancel();
            _latestRequest = _pending = request;
            _result = null;
            _wake.Set();
            return true;
        }
    }

    public SmartElementResult? TakeResult(Guid sessionId, long version)
    {
        lock (_sync)
        {
            if (!CheckAvailable() || _result is not { } result ||
                result.Request.SessionId != sessionId || result.Request.Version != version) return null;
            _result = null;
            return result;
        }
    }

    public bool IsAvailable
    {
        get { lock (_sync) return CheckAvailable(); }
    }

    internal int PendingCount
    {
        get { lock (_sync) return _pending is null ? 0 : 1; }
    }

    internal bool IsRootAvailable(nint rootWindow, uint processId)
    {
        lock (_sync) return CheckAvailable() && !IsRootBlocked(rootWindow, processId);
    }

    private bool IsRootBlocked(nint rootWindow, uint processId) =>
        _rootFailures.TryGetValue((rootWindow, processId), out var failures) && failures >= 3;

    public void CancelSession(Guid sessionId)
    {
        lock (_sync)
        {
            if (_latestRequest?.SessionId != sessionId) return;
            _pending = _latestRequest = null;
            _result = null;
            _activeCancellation?.Cancel();
        }
    }

    private bool CheckAvailable()
    {
        if (_disposed || _blocked) return false;
        if (_activeStarted != 0 && Stopwatch.GetElapsedTime(_activeStarted) > _blockedThreshold)
        {
            _blocked = true;
            _pending = _latestRequest = null;
            _result = null;
            _activeCancellation?.Cancel();
            DiagnosticLog.Warning("SmartSelection", "窗口提供程序响应过慢，已暂停本次运行的精细识别；普通窗口识别仍可使用。");
            return false;
        }
        return true;
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                _wake.WaitOne();
                SmartElementRequest? request;
                CancellationTokenSource cancellation;
                lock (_sync)
                {
                    if (_disposed) return;
                    request = _pending;
                    _pending = null;
                    if (request is null || _blocked) continue;
                    _activeCancellation = cancellation = new CancellationTokenSource();
                    _activeStarted = Stopwatch.GetTimestamp();
                }
                try
                {
                    var candidates = _probe(request, cancellation.Token);
                    lock (_sync)
                    {
                        _rootFailures.Remove((request.RootWindow, request.RootProcessId));
                        if (!_disposed && !_blocked && !cancellation.IsCancellationRequested &&
                            _latestRequest == request &&
                            Stopwatch.GetElapsedTime(_activeStarted) < TimeSpan.FromMilliseconds(500))
                        {
                            _result = new SmartElementResult(request, candidates);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    if (Interlocked.Increment(ref _failureCount) <= 3)
                        DiagnosticLog.Warning("SmartSelection", $"精细识别不可用，保留普通窗口预览：{exception.GetType().Name}。");
                    lock (_sync)
                    {
                        var key = (request.RootWindow, request.RootProcessId);
                        if (!_rootFailures.ContainsKey(key) && _rootFailures.Count >= 64)
                            _rootFailures.Remove(_rootFailures.Keys.First());
                        _rootFailures.TryGetValue(key, out var failures);
                        _rootFailures[key] = failures + 1;
                        if (failures == 2)
                        {
                            if (_pending is { } pending && pending.RootWindow == request.RootWindow &&
                                pending.RootProcessId == request.RootProcessId) _pending = null;
                            DiagnosticLog.Warning("SmartSelection", "该窗口连续无法提供精细识别，保留普通窗口预览；其他窗口仍可自动识别。");
                        }
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        _activeStarted = 0;
                        _activeCancellation = null;
                    }
                    cancellation.Dispose();
                }
            }
        }
        finally { _wake.Dispose(); }
    }

    private static IReadOnlyList<SmartSelectionCandidate> Probe(
        SmartElementRequest request, CancellationToken cancellation)
    {
        if (!RootStillMatches(request)) return [];
        var started = Stopwatch.GetTimestamp();
        var candidates = new List<SmartSelectionCandidate>();
        var root = AutomationElement.FromHandle(request.RootWindow);
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        cache.TreeFilter = Automation.RawViewCondition;
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.ProcessIdProperty);

        // Follow a matching child's subtree before consuming the rest of a wide sibling list.
        // Otherwise the node/time budget could expire before a known hit branch is ever refined.
        TraversePointPath(root,
            element => TreeWalker.RawViewWalker.GetFirstChild(element, cache),
            element => TreeWalker.RawViewWalker.GetNextSibling(element, cache),
            child =>
            {
                var info = child.Cached;
                if (info.IsOffscreen || info.ProcessId == Environment.ProcessId ||
                    !TryPhysicalBounds(info.BoundingRectangle, out var bounds) || !bounds.Contains(request.Pointer))
                    return false;
                bounds = Rectangle.Intersect(bounds, request.RootBounds);
                if (bounds.Width < 20 || bounds.Height < 20) return false;
                if (IsUsefulRegion(info.ControlType))
                    candidates.Add(new SmartSelectionCandidate(bounds, SmartSelectionKind.Element));
                return true;
            },
            () => Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(100), cancellation);
        cancellation.ThrowIfCancellationRequested();
        return RootStillMatches(request)
            ? SmartSelectionCandidates.Normalize(candidates, request.Pointer, request.CaptureBounds)
            : [];
    }

    // Pure bounded traversal used by the UIA probe and synthetic regression tests. Each provider
    // call is still cooperative/uninterruptible; checks prevent starting additional calls afterwards.
    internal static int TraversePointPath<T>(T root, Func<T, T?> firstChild, Func<T, T?> nextSibling,
        Func<T, bool> visitAndDescend, Func<bool> withinBudget, CancellationToken cancellation,
        int maxNodes = 96, int maxDepth = 10) where T : class
    {
        var visited = 0;
        void VisitChildren(T parent, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth >= maxDepth || visited >= maxNodes || !withinBudget()) return;
            var child = firstChild(parent);
            while (child is not null && visited < maxNodes && withinBudget())
            {
                cancellation.ThrowIfCancellationRequested();
                visited++;
                if (visitAndDescend(child)) VisitChildren(child, depth + 1);
                if (visited >= maxNodes || !withinBudget()) break;
                cancellation.ThrowIfCancellationRequested();
                child = nextSibling(child);
            }
        }
        VisitChildren(root, 0);
        return visited;
    }

    private static bool IsUsefulRegion(ControlType type) =>
        type == ControlType.Pane || type == ControlType.Document || type == ControlType.List ||
        type == ControlType.Tree || type == ControlType.Table || type == ControlType.DataGrid ||
        type == ControlType.ToolBar || type == ControlType.Group || type == ControlType.Image;

    internal static bool TryPhysicalBounds(AutomationRect value, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (value.IsEmpty || !double.IsFinite(value.Left) || !double.IsFinite(value.Top) ||
            !double.IsFinite(value.Right) || !double.IsFinite(value.Bottom) ||
            value.Left < int.MinValue || value.Top < int.MinValue ||
            value.Right > int.MaxValue || value.Bottom > int.MaxValue ||
            value.Width <= 0 || value.Height <= 0 ||
            value.Width > int.MaxValue || value.Height > int.MaxValue) return false;
        bounds = Rectangle.FromLTRB((int)Math.Floor(value.Left), (int)Math.Floor(value.Top),
            (int)Math.Ceiling(value.Right), (int)Math.Ceiling(value.Bottom));
        return bounds.Width > 0 && bounds.Height > 0;
    }

    internal static bool RootStillMatches(SmartElementRequest request)
    {
        NativeMethods.GetWindowThreadProcessId(request.RootWindow, out var processId);
        return processId == request.RootProcessId && processId != Environment.ProcessId &&
            NativeMethods.GetWindowRect(request.RootWindow, out var rect) &&
            new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height) == request.RootBounds;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = _latestRequest = null;
            _result = null;
            _activeCancellation?.Cancel();
            _wake.Set();
            // Never Join: cross-process providers are outside our control. The background thread
            // owns/disposes its event after returning, and cannot retain an overlay window.
        }
    }
}
