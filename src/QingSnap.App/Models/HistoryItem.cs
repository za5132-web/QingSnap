using System.Windows.Media.Imaging;

namespace QingSnap.App.Models;

public sealed record HistoryItem(
    string FilePath,
    string FileName,
    DateTime CreatedAt,
    int PixelWidth,
    int PixelHeight,
    long FileSize,
    BitmapSource? Thumbnail,
    bool IsFavorite,
    string SearchText,
    string? SourceProcess = null,
    string? SourceWindowTitle = null,
    IReadOnlyList<string>? Tags = null,
    long MetadataId = 0,
    bool IsLongCapture = false)
{
    public string DateText => CreatedAt.ToString("yyyy-MM-dd  HH:mm:ss");
    public string DimensionsText => $"{PixelWidth} × {PixelHeight} px";
    public string FileSizeText => FormatBytes(FileSize);
    public string FavoriteText => IsFavorite ? "已收藏" : "收藏";
    public bool HasSource => !string.IsNullOrWhiteSpace(SourceProcess) ||
                             !string.IsNullOrWhiteSpace(SourceWindowTitle);
    public string SourceDisplay => BuildSourceDisplay(SourceProcess, SourceWindowTitle);
    public bool HasTags => Tags is { Count: > 0 };
    public string TagsDisplay => Tags is { Count: > 0 }
        ? "标签  " + string.Join(" · ", Tags)
        : string.Empty;

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024D * 1024D):0.0} MB";
        }

        return $"{bytes / 1024D:0.0} KB";
    }

    private static string BuildSourceDisplay(string? process, string? title)
    {
        if (string.IsNullOrWhiteSpace(process))
        {
            return string.IsNullOrWhiteSpace(title) ? string.Empty : $"来源  {title.Trim()}";
        }

        return string.IsNullOrWhiteSpace(title)
            ? $"来源  {process.Trim()}"
            : $"来源  {process.Trim()} · {title.Trim()}";
    }
}
