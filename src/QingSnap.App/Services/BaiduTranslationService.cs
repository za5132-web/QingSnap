using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed partial class BaiduTranslationService : IDisposable
{
    internal const string Endpoint = "https://fanyi-api.baidu.com/api/trans/vip/translate";
    public const int MaximumSourceLength = 20_000;
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _minimumInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private long _lastRequestTimestamp;
    private bool _disposed;

    public BaiduTranslationService() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), true) { }

    internal BaiduTranslationService(HttpClient client, bool ownsClient = false, TimeSpan? minimumInterval = null)
    {
        _client = client;
        _ownsClient = ownsClient;
        _minimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(1050);
    }

    public async Task<string> TranslateAsync(string source, string target, TranslationCredentials credentials,
        bool enabled, bool cloudConsent, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!enabled) throw new TranslationException("disabled", "翻译尚未启用，请先打开翻译设置。");
        if (!cloudConsent) throw new TranslationException("consent", "请先确认：待翻译文字会发送至百度翻译。");
        if (!credentials.IsValid) throw new TranslationException("credentials", "请先填写自己的百度翻译 APPID 和密钥。");
        if (string.IsNullOrWhiteSpace(source)) throw new TranslationException("empty", "请先输入或选择要翻译的文字。");
        if (source.Length > MaximumSourceLength) throw new TranslationException("length", "一次最多翻译 20,000 个字符，请分段翻译。");
        if (!TranslationLanguages.All.Any(language => language.Code == target))
            throw new TranslationException("language", "请选择支持的目标语言。");
        try { _ = new UTF8Encoding(false, true).GetByteCount(source); }
        catch (EncoderFallbackException) { throw new TranslationException("encoding", "文字包含不完整字符，请重新选择。"); }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var result = new StringBuilder();
            foreach (var chunk in SplitSource(source))
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(chunk.Text))
                {
                    await WaitRateAsync(linked.Token).ConfigureAwait(false);
                    result.Append(await SendAsync(chunk.Text, target, credentials, linked.Token).ConfigureAwait(false));
                }
                else result.Append(chunk.Text);
                result.Append(chunk.Separator);
            }
            return result.ToString();
        }
        finally { _gate.Release(); }
    }

    private async Task<string> SendAsync(string text, string target, TranslationCredentials credentials, CancellationToken token) =>
        string.Join(Environment.NewLine, (await SendLinesAsync(text, target, credentials, token).ConfigureAwait(false)).Select(line => line.Destination));

    private async Task<IReadOnlyList<ProviderLine>> SendLinesAsync(string text, string target, TranslationCredentials credentials, CancellationToken token)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = text, ["from"] = "auto", ["to"] = target, ["appid"] = credentials.AppId,
                ["salt"] = salt, ["sign"] = CreateSignature(credentials.AppId, text, salt, credentials.Secret)
            })
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new TranslationException("http", "百度翻译暂时无法连接，请检查网络后手动重试。");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new TranslationException("response", "翻译响应过大，已停止读取。");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumResponseBytes) throw new TranslationException("response", "翻译响应过大，已停止读取。");
                buffer.Write(bytes, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (root.TryGetProperty("error_code", out var error))
            {
                var code = error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText();
                if (code != "52000") throw MapError(code);
            }
            if (!root.TryGetProperty("trans_result", out var lines) || lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() == 0)
                throw new JsonException();
            var translations = new List<ProviderLine>();
            foreach (var line in lines.EnumerateArray())
            {
                if (line.ValueKind != JsonValueKind.Object || !line.TryGetProperty("dst", out var destination) ||
                    destination.ValueKind != JsonValueKind.String) throw new JsonException();
                var source = line.TryGetProperty("src", out var original) && original.ValueKind == JsonValueKind.String
                    ? original.GetString() : null;
                translations.Add(new ProviderLine(source, destination.GetString()!));
            }
            DiagnosticLog.Info("Translation", "Baidu text translation completed.");
            return translations;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            DiagnosticLog.Warning("Translation", "Baidu request timed out; not automatically retried.");
            throw new TranslationException("timeout", "翻译请求超时；为避免重复计费，未自动重试。");
        }
        catch (TranslationException exception)
        {
            DiagnosticLog.Warning("Translation", $"Baidu request failed ({exception.Code}).");
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            DiagnosticLog.Warning("Translation", $"Baidu request failed ({exception.GetType().Name}); body omitted.");
            throw new TranslationException("network", "未收到有效翻译结果，请检查网络或账户后手动重试。");
        }
    }

    internal static string CreateSignature(string appId, string text, string salt, string secret) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(appId + text + salt + secret))).ToLowerInvariant();

    internal static IReadOnlyList<(string Text, string Separator)> SplitSource(string source)
    {
        var chunks = new List<(string, string)>();
        var offset = 0;
        while (offset < source.Length)
        {
            var length = 0;
            var count = 0;
            foreach (var rune in source.AsSpan(offset).EnumerateRunes())
            {
                if (count++ >= 1000) break;
                length += rune.Utf16SequenceLength;
            }
            var end = offset + length;
            if (end < source.Length)
            {
                var boundary = source.LastIndexOf('\n', end - 1, length);
                if (boundary < offset) boundary = source.LastIndexOf(' ', end - 1, length);
                if (boundary > offset) end = boundary;
                if (end > offset && source[end - 1] == '\r') end--;
            }
            var next = end;
            while (next < source.Length && char.IsWhiteSpace(source[next])) next++;
            chunks.Add((source[offset..end], source[end..next]));
            offset = next;
        }
        return chunks;
    }

    private static TranslationException MapError(string? code) => code switch
    {
        "52003" => new("52003", "APPID 未授权，请检查账号是否已开通通用文本翻译服务。"),
        "54001" => new("54001", "签名校验失败，请检查 APPID 与密钥是否属于同一个账号。"),
        "54003" => new("54003", "请求过于频繁，请稍后手动重试。"),
        "54004" => new("54004", "百度翻译账户额度不足，请前往控制台查看。"),
        "58000" => new("58000", "此客户端 IP 不在账号白名单中，请检查百度控制台。"),
        "58001" => new("58001", "当前语言方向不受此账号支持。"),
        "58002" => new("58002", "百度翻译服务尚未开通或账号受限，请查看控制台。"),
        "58003" => new("58003", "百度限制了当前 IP 的 APPID 使用，请查看官方说明；不会切换其他账号或服务。"),
        _ => new("provider", "百度暂未返回翻译结果，请查看官方控制台后重试。")
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (_ownsClient) _client.Dispose();
        // Do not dispose the gate before in-flight operations release it. Its
        // WaitHandle is never created; this is only a managed async semaphore.
    }
}
