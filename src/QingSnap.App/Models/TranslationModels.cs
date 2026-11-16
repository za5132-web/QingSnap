namespace QingSnap.App.Models;

public sealed record TranslationLanguage(string Code, string Name)
{
    public override string ToString() => Name;
}

public static class TranslationLanguages
{
    public static IReadOnlyList<TranslationLanguage> All { get; } =
    [
        new("zh", "简体中文"), new("en", "英语"), new("cht", "繁体中文"),
        new("jp", "日语"), new("kor", "韩语"), new("fra", "法语"),
        new("de", "德语"), new("ru", "俄语"), new("spa", "西班牙语")
    ];

    public static string Normalize(string? code) => All.Any(language => language.Code == code) ? code! : "zh";

    public static bool IsSupported(string? code) => All.Any(language => language.Code == code);
    public static string GetName(string? code) => All.First(language => language.Code == Normalize(code)).Name;
    public static string GetCultureName(string? code) => Normalize(code) switch
    {
        "en" => "en-US", "cht" => "zh-TW", "jp" => "ja-JP", "kor" => "ko-KR",
        "fra" => "fr-FR", "de" => "de-DE", "ru" => "ru-RU", "spa" => "es-ES", _ => "zh-CN"
    };

    public static string GetFontFamily(string? code) => Normalize(code) switch
    {
        "jp" => "Yu Gothic UI, Microsoft YaHei UI, Segoe UI",
        "kor" => "Malgun Gothic, Microsoft YaHei UI, Segoe UI",
        "zh" or "cht" => "Microsoft YaHei UI", _ => "Segoe UI, Microsoft YaHei UI"
    };
}

// Do not put credentials into AppSettings, diagnostic messages or ToString().
public sealed class TranslationCredentials(string appId, string secret)
{
    public string AppId { get; } = appId;
    public string Secret { get; } = secret;
    public bool IsValid => AppId is { Length: >= 6 and <= 40 } && AppId.All(char.IsAsciiDigit) &&
                           !string.IsNullOrWhiteSpace(Secret) && Secret.Length <= 256;
    public override string ToString() => "[Baidu credentials redacted]";
}

public sealed class TranslationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record TranslationSegment(int Id, string Text);
public sealed record TranslatedSegment(int Id, string Text);
