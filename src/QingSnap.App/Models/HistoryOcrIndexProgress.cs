namespace QingSnap.App.Models;

public sealed record HistoryOcrIndexProgress(
    int PendingCount,
    int IndexedCount,
    string? CompletedFilePath = null,
    string? RecognizedText = null,
    bool IsOcrAvailable = true);
