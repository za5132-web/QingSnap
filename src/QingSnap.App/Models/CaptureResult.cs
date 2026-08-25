namespace QingSnap.App.Models;

public sealed record CaptureResult(
    CaptureRegion Region,
    string ImagePath,
    int ImageWidth,
    int ImageHeight,
    bool CopyRequested,
    bool CopiedToClipboard);
