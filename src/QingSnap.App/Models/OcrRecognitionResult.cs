namespace QingSnap.App.Models;

public sealed record OcrRecognitionResult(
    string Text,
    string LanguageName,
    string LanguageTag,
    int LineCount,
    int SourceWidth,
    int SourceHeight,
    int RecognitionWidth,
    int RecognitionHeight,
    IReadOnlyList<OcrTextLine> Lines)
{
    public double ElapsedMilliseconds { get; init; }
}

public sealed record OcrTextLine(
    int Index,
    string Text,
    OcrTextBounds Bounds,
    IReadOnlyList<OcrTextWord> Words);

public sealed record OcrTextWord(
    int Index,
    int LineIndex,
    string Text,
    OcrTextBounds Bounds);

public sealed record OcrTextBounds(
    double X,
    double Y,
    double Width,
    double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}
