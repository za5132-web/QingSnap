using System.Windows.Media.Imaging;

namespace QingSnap.App.Models;

public sealed record HistoryItem(
    string FilePath,
    string FileName,
    DateTime CreatedAt,
    int PixelWidth,
    int PixelHeight,
    long FileSize,
    BitmapSource Thumbnail)
{
    public string DateText => CreatedAt.ToString("yyyy-MM-dd  HH:mm:ss");
    public string DimensionsText => $"{PixelWidth} × {PixelHeight} px";
    public string FileSizeText => FormatBytes(FileSize);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024D * 1024D):0.0} MB";
        }

        return $"{bytes / 1024D:0.0} KB";
    }
}
