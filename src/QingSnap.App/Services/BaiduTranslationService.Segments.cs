using System.Diagnostics;
using System.Text;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed partial class BaiduTranslationService
{
    private sealed record ProviderLine(string? Source, string Destination);

    private async Task WaitRateAsync(CancellationToken token)
    {
        if (_lastRequestTimestamp != 0)
        {
            var remaining = _minimumInterval - Stopwatch.GetElapsedTime(_lastRequestTimestamp);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
        }
        _lastRequestTimestamp = Stopwatch.GetTimestamp();
    }

    // Stable IDs never go to the provider. Each response's echoed source must
    // match before translated text may be painted onto that source rectangle.
    public async Task<IReadOnlyList<TranslatedSegment>> TranslateSegmentsAsync(
        IReadOnlyList<TranslationSegment> segments, TranslationCredentials credentials,
        bool enabled, bool cloudConsent, IProgress<int>? progress = null, CancellationToken cancellationToken = default,
        string targetLanguage = "zh")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!enabled) throw new TranslationException("disabled", "请先在翻译设置中启用百度翻译。");
        if (!cloudConsent) throw new TranslationException("consent", "请先确认文字云端处理与账户费用提示。");
        if (!credentials.IsValid) throw new TranslationException("credentials", "请先在翻译设置填写自己的 APPID 与密钥。");
        if (!TranslationLanguages.IsSupported(targetLanguage))
            throw new TranslationException("language", "请选择支持的目标语言。");
        if (segments.Count == 0) return [];
        if (segments.Count > 500 || segments.Sum(segment => (long)segment.Text.Length + 1) > MaximumSourceLength)
            throw new TranslationException("length", "这张图文字较多，请先选中一部分再翻译。");
        if (segments.Select(segment => segment.Id).Distinct().Count() != segments.Count ||
            segments.Any(segment => string.IsNullOrWhiteSpace(segment.Text) || segment.Text.IndexOfAny(['\r', '\n']) >= 0))
            throw new TranslationException("segments", "文字区域不完整，请重新识别后再试。");
        try { foreach (var segment in segments) _ = new UTF8Encoding(false, true).GetByteCount(segment.Text); }
        catch (EncoderFallbackException) { throw new TranslationException("encoding", "文字包含不完整字符，请重新选择。"); }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var pieces = segments.SelectMany(segment => SplitSource(segment.Text)
                .Select(chunk => (segment.Id, chunk.Text, chunk.Separator))).ToArray();
            var results = segments.ToDictionary(segment => segment.Id, _ => new StringBuilder());
            var offset = 0;
            while (offset < pieces.Length)
            {
                linked.Token.ThrowIfCancellationRequested();
                var batch = new List<(int Id, string Text, string Separator)>();
                var runes = 0;
                while (offset + batch.Count < pieces.Length)
                {
                    var item = pieces[offset + batch.Count];
                    var size = item.Text.EnumerateRunes().Count();
                    if (batch.Count > 0 && runes + size + 1 > 1000) break;
                    batch.Add(item);
                    runes += size + (batch.Count > 1 ? 1 : 0);
                }
                await WaitRateAsync(linked.Token).ConfigureAwait(false);
                var response = await SendLinesAsync(string.Join('\n', batch.Select(item => item.Text)), targetLanguage, credentials, linked.Token).ConfigureAwait(false);
                // Reordering is safe only when echoed source can identify every
                // item. Duplicate source lines are consumed from a queue.
                var remaining = response.ToList();
                foreach (var item in batch)
                {
                    var index = remaining.FindIndex(line => string.Equals(line.Source?.Trim(), item.Text.Trim(), StringComparison.Ordinal));
                    if (index < 0 || string.IsNullOrWhiteSpace(remaining[index].Destination))
                    {
                        DiagnosticLog.Warning("Translation", "Mapped response did not match source regions; no overlay applied.");
                        throw new TranslationException("mapping", "返回文字与原图区域不匹配，已保留原图；不会自动重试。");
                    }
                    results[item.Id].Append(remaining[index].Destination).Append(item.Separator);
                    remaining.RemoveAt(index);
                }
                if (remaining.Count != 0)
                    throw new TranslationException("mapping", "返回文字数量异常，已保留原图；不会自动重试。");
                offset += batch.Count;
                progress?.Report((int)Math.Round(offset * 100D / pieces.Length));
            }
            linked.Token.ThrowIfCancellationRequested();
            return segments.Select(segment => new TranslatedSegment(segment.Id, results[segment.Id].ToString())).ToArray();
        }
        finally { _gate.Release(); }
    }
}
