namespace QingSnap.App.Models;

public enum HistoryOcrIndexState
{
    NotIndexed = 0,
    Indexed = 1,
    Failed = 2
}

public sealed record HistoryMetadata(
    long Id,
    string FilePath,
    DateTimeOffset CaptureTime,
    int Width,
    int Height,
    long FileSize,
    string Format,
    bool IsLongCapture,
    bool IsFavorite,
    string OcrText,
    HistoryOcrIndexState OcrIndexState,
    string? SourceProcess,
    string? SourceWindowTitle,
    string? MonitorId,
    string? MonitorDeviceName,
    int? CaptureX,
    int? CaptureY,
    int? CaptureWidth,
    int? CaptureHeight,
    string? ImageHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CaptureHistoryContext(
    bool IsLongCapture,
    string? SourceProcess,
    string? SourceWindowTitle,
    string? MonitorId,
    string? MonitorDeviceName,
    int? CaptureX,
    int? CaptureY,
    int? CaptureWidth,
    int? CaptureHeight);
