using System.Windows.Media.Imaging;
using DrawingRectangle = System.Drawing.Rectangle;

namespace QingSnap.App.Models;

public sealed class CaptureOverlayActionEventArgs : EventArgs
{
    public CaptureOverlayActionEventArgs(
        CaptureOverlayAction action,
        DrawingRectangle localRegion,
        BitmapSource image,
        Task<OcrRecognitionResult>? prefetchedOcr = null,
        IReadOnlyList<string>? tags = null)
    {
        Action = action;
        LocalRegion = localRegion;
        Image = image;
        PrefetchedOcr = prefetchedOcr;
        Tags = tags ?? [];
    }

    public CaptureOverlayAction Action { get; }

    public DrawingRectangle LocalRegion { get; }

    public BitmapSource Image { get; }

    public Task<OcrRecognitionResult>? PrefetchedOcr { get; }

    public IReadOnlyList<string> Tags { get; }
}
