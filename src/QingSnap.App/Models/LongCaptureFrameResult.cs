namespace QingSnap.App.Models;

public sealed record LongCaptureFrameResult(
    bool Accepted,
    bool IsDuplicate,
    int AppendedHeight,
    int TotalHeight,
    double MatchScore,
    string Message)
{
    public double MatchConfidence { get; init; }

    public bool UsedRobustFallback { get; init; }

    public LongCaptureFrameFailure Failure { get; init; }
}

public enum LongCaptureFrameFailure
{
    None,
    Unmatchable,
    SafetyLimit
}
