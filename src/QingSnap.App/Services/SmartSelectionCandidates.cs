using System.Drawing;

namespace QingSnap.App.Services;

internal enum SmartSelectionKind { Window, NativeChild, Element }

internal sealed record SmartSelectionCandidate(Rectangle Bounds, SmartSelectionKind Kind);

internal sealed record SmartSelectionWindowHit(
    nint RootWindow,
    uint RootProcessId,
    Rectangle RootBounds,
    IReadOnlyList<SmartSelectionCandidate> Candidates);

internal sealed record SmartElementRequest(
    Guid SessionId,
    long Version,
    nint RootWindow,
    uint RootProcessId,
    Rectangle RootBounds,
    Point Pointer,
    Rectangle CaptureBounds);

internal sealed record SmartElementResult(
    SmartElementRequest Request,
    IReadOnlyList<SmartSelectionCandidate> Candidates);

internal static class SmartSelectionCandidates
{
    // Global, physical pixels throughout. WPF conversion belongs only at the presentation boundary.
    public static IReadOnlyList<SmartSelectionCandidate> Normalize(
        IEnumerable<SmartSelectionCandidate> candidates, Point pointer, Rectangle captureBounds) =>
        candidates
            .Select(candidate => candidate with { Bounds = Rectangle.Intersect(candidate.Bounds, captureBounds) })
            .Where(candidate => candidate.Bounds.Width >= 20 && candidate.Bounds.Height >= 20 &&
                                candidate.Bounds.Contains(pointer))
            .DistinctBy(candidate => candidate.Bounds)
            .OrderBy(candidate => (long)candidate.Bounds.Width * candidate.Bounds.Height)
            .ThenBy(candidate => candidate.Bounds.X)
            .ThenBy(candidate => candidate.Bounds.Y)
            .ToArray();

    public static int CycleIndex(int current, int count, int wheelDelta) => count == 0
        ? -1
        : Math.Clamp(current + (wheelDelta > 0 ? 1 : -1), 0, count - 1);

    public static bool IsCurrent(SmartElementRequest request, Guid sessionId, long version,
        nint rootWindow, uint rootProcessId, Rectangle rootBounds) =>
        request.SessionId == sessionId && request.Version == version &&
        request.RootWindow == rootWindow && request.RootProcessId == rootProcessId && request.RootBounds == rootBounds;
}
