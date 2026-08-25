using System.Windows.Media.Imaging;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public interface IAdvancedOcrRuntime : IDisposable
{
    void Initialize(OcrModelPaths paths);

    Task WarmUpAsync(CancellationToken cancellationToken = default);

    Task<OcrRecognitionResult> RecognizeAsync(
        BitmapSource source,
        bool includeWordBoxes,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
