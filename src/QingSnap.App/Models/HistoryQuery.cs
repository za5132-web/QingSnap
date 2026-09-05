namespace QingSnap.App.Models;

public enum HistoryFilterKind
{
    All = 0,
    Today = 1,
    LastSevenDays = 2,
    LongCapture = 3,
    Favorite = 4
}

public enum HistorySortOrder
{
    NewestFirst = 0,
    OldestFirst = 1
}

public sealed record HistoryQuery(
    int Offset,
    int Limit,
    string? SearchText = null,
    HistoryFilterKind Filter = HistoryFilterKind.All,
    string? Tag = null,
    HistorySortOrder SortOrder = HistorySortOrder.NewestFirst,
    bool IncludeStatistics = true);

public sealed record HistorySummary(
    long Id,
    string FilePath,
    DateTimeOffset CaptureTime,
    int Width,
    int Height,
    long FileSize,
    string Format,
    bool IsLongCapture,
    bool IsFavorite,
    HistoryOcrIndexState OcrIndexState,
    string? SourceProcess,
    string? SourceWindowTitle,
    string? ImageHash,
    IReadOnlyList<string> Tags);

public sealed record HistoryQueryPage(
    IReadOnlyList<HistorySummary> Items,
    int TotalCount,
    long TotalBytes,
    bool HasMore);

public sealed record HistoryItemPage(
    IReadOnlyList<HistoryItem> Items,
    int TotalCount,
    long TotalBytes,
    bool HasMore);

public sealed record HistoryMigrationEntry(
    string FilePath,
    long FileSize,
    string? ImageHash,
    DateTimeOffset UpdatedAt);
