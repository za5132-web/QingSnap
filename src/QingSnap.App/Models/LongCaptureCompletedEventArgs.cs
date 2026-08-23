using System.Windows.Media.Imaging;

namespace QingSnap.App.Models;

public sealed class LongCaptureCompletedEventArgs(BitmapSource image) : EventArgs
{
    public BitmapSource Image { get; } = image;
}
