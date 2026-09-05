namespace QingSnap.App.Models;

public sealed class QrCodeResult
{
    private const int MaximumDisplayLength = 600;

    public QrCodeResult(string text, string format, double centerX = 0, double centerY = 0)
    {
        Text = text ?? string.Empty;
        Format = string.IsNullOrWhiteSpace(format) ? "QR Code" : format;
        CenterX = centerX;
        CenterY = centerY;
        SafeUrl = TryCreateSafeUrl(Text);
        DisplayText = Text.Length <= MaximumDisplayLength
            ? Text
            : $"{Text[..MaximumDisplayLength]}…\n\n（内容较长，界面仅展示前 {MaximumDisplayLength} 个字符，复制时仍会复制完整内容）";
    }

    public string Text { get; }

    public string DisplayText { get; }

    public string Format { get; }

    public double CenterX { get; }

    public double CenterY { get; }

    public Uri? SafeUrl { get; }

    public bool IsUrl => SafeUrl is not null;

    public string TypeText => IsUrl ? "网址" : "文本";

    private static Uri? TryCreateSafeUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }
}
