namespace QingSnap.App.Models;

public sealed record AppSettings
{
    public string CaptureHotkey { get; init; } = "F1";

    public string PinHotkey { get; init; } = "F3";

    public string RepeatHotkey { get; init; } = "Shift+F1";

    public bool StartWithWindows { get; init; }

    public string HistoryDirectory { get; init; } = string.Empty;

    public int HistoryRetentionDays { get; init; }

    public string OutputFormat { get; init; } = "PNG";

    public int JpegQuality { get; init; } = 92;

    public bool AutoCopy { get; init; } = true;

    public int CaptureDelaySeconds { get; init; }

    public bool SmartWindowSelection { get; init; } = true;

    public bool ShowMagnifier { get; init; } = true;

    public string CloseInteraction { get; init; } = "Escape";

    public string OcrEngine { get; init; } = "Advanced";

    public int LongScrollWheelDelta { get; init; } = 720;

    public int LongMatchRetryCount { get; init; } = 3;

    public int LongMinimumOverlapPercent { get; init; } = 20;

    public string AnnotationColor { get; init; } = "#FFFF4E5B";

    public double AnnotationThickness { get; init; } = 3;

    public double AnnotationFontSize { get; init; } = 18;
}
