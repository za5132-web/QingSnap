namespace QingSnap.App.Models;

public sealed record HistorySnapshot(
    int TotalCount,
    long TotalBytes,
    IReadOnlyList<HistoryItem> Items);
