using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class BaiduTranslationTests
{
    private static TranslationCredentials FakeCredentials => new("2015063000000001", "synthetic-key-not-a-real-credential");

    [Fact]
    public void SignatureUsesRawUtf8BeforeFormEncoding() => Assert.Equal("a1a7461d92e5194c5cae3182b5b24de1",
        BaiduTranslationService.CreateSignature("2015063000000001", "apple", "65478", "1234567890"));

    [Theory]
    [InlineData("apple")]
    [InlineData("空格 & 加号+ /?\n第二行😀")]
    public async Task PostsOnlySignedTextToOfficialHttpsEndpoint(string source)
    {
        using var handler = new FakeHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(BaiduTranslationService.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal(string.Empty, request.RequestUri.Query);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var form = ParseForm(await request.Content.ReadAsStringAsync(token));
            Assert.Equal(source, form["q"]);
            Assert.Equal("auto", form["from"]);
            Assert.Equal("zh", form["to"]);
            Assert.Equal(BaiduTranslationService.CreateSignature(form["appid"], source, form["salt"], FakeCredentials.Secret), form["sign"]);
            Assert.Equal(6, form.Count);
            Assert.DoesNotContain(FakeCredentials.Secret, await request.Content.ReadAsStringAsync(token));
            return Success("苹果");
        });
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client);
        Assert.Equal("苹果", await service.TranslateAsync(source, "zh", FakeCredentials, true, true));
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(false, true, "hello", "zh", "disabled")]
    [InlineData(true, false, "hello", "zh", "consent")]
    [InlineData(true, true, "  ", "zh", "empty")]
    [InlineData(true, true, "hello", "auto", "language")]
    public async Task InvalidOrUnconsentedInputNeverSends(bool enabled, bool consent, string source, string target, string code)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Success("never")));
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client);
        var error = await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync(source, target, FakeCredentials, enabled, consent));
        Assert.Equal(code, error.Code);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task MissingCredentialsAndOversizeAndBrokenUnicodeNeverSend()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Success("never")));
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client);
        Assert.Equal("credentials", (await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync("hello", "zh", new("", ""), true, true))).Code);
        Assert.Equal("length", (await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync(new string('a', 20001), "zh", FakeCredentials, true, true))).Code);
        Assert.Equal("encoding", (await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync("bad\ud83d", "zh", FakeCredentials, true, true))).Code);
        Assert.Equal(0, handler.Count);
    }

    [Theory]
    [InlineData("54001")][InlineData("54003")][InlineData("54004")][InlineData("52003")]
    [InlineData("58000")][InlineData("58001")][InlineData("58002")][InlineData("58003")][InlineData("unknown-secret")]
    public async Task ProviderErrorsAreSanitizedAndNeverRetried(string code)
    {
        const string privateText = "raw private source and secret must not be echoed";
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new { error_code = code, error_msg = privateText })) }));
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client);
        var error = await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync("hello", "zh", FakeCredentials, true, true));
        Assert.DoesNotContain(privateText, error.ToString());
        Assert.DoesNotContain("unknown-secret", error.ToString());
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("[]")][InlineData("not json")][InlineData("{}")][InlineData("{\"trans_result\":[]}")]
    [InlineData("{\"trans_result\":[{\"src\":\"no dst\"}]}")]
    public async Task MalformedResultFailsWithoutLeakingBody(string body)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client);
        Assert.Equal("network", (await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync("hello", "zh", FakeCredentials, true, true))).Code);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RejectsRedirectResponseAndOversizedResponse()
    {
        foreach (var response in new[]
        {
            new HttpResponseMessage(HttpStatusCode.Redirect),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 1024 * 1024 + 1)) }
        })
        {
            using var handler = new FakeHandler((_, _) => Task.FromResult(response));
            using var client = new HttpClient(handler);
            using var service = new BaiduTranslationService(client);
            await Assert.ThrowsAsync<TranslationException>(() => service.TranslateAsync("hello", "zh", FakeCredentials, true, true));
            Assert.Equal(1, handler.Count);
        }
    }

    [Theory]
    [InlineData("paragraphs")][InlineData("emoji")][InlineData("no-spaces")][InlineData("whitespace")]
    public void ChunkingPreservesAllCharactersAndUnicodeBoundaries(string sample)
    {
        var source = sample switch
        {
            "paragraphs" => string.Concat(Enumerable.Repeat("hello 世界 &+\r\n第二行\n\n", 500)),
            "emoji" => string.Concat(Enumerable.Repeat("😀", 2700)),
            "no-spaces" => new string('测', 4500),
            _ => new string(' ', 2100) + "end\r\n"
        };
        var chunks = BaiduTranslationService.SplitSource(source);
        Assert.Equal(source, string.Concat(chunks.Select(chunk => chunk.Text + chunk.Separator)));
        foreach (var chunk in chunks)
        {
            Assert.InRange(chunk.Text.EnumerateRunes().Count(), 0, 1000);
            Assert.InRange(Encoding.UTF8.GetByteCount(chunk.Text), 0, 6000);
            _ = new UTF8Encoding(false, true).GetByteCount(chunk.Text);
        }
    }

    [Fact]
    public async Task SerializesRequestsAndHonorsMinimumInterval()
    {
        var times = new List<long>();
        var concurrent = 0;
        using var handler = new FakeHandler(async (_, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref concurrent));
            times.Add(Stopwatch.GetTimestamp());
            await Task.Delay(10, token);
            Interlocked.Decrement(ref concurrent);
            return Success("ok");
        });
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client, minimumInterval: TimeSpan.FromMilliseconds(90));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => service.TranslateAsync("hello", "zh", FakeCredentials, true, true)));
        Assert.Equal(3, times.Count);
        Assert.True(Stopwatch.GetElapsedTime(times[0], times[1]).TotalMilliseconds >= 80);
        Assert.True(Stopwatch.GetElapsedTime(times[1], times[2]).TotalMilliseconds >= 80);
    }

    [Fact]
    public async Task CancellationAndDisposalStopInFlightAndQueuedWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Success("never");
        });
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client);
        var first = service.TranslateAsync("hello", "zh", FakeCredentials, true, true);
        await started.Task;
        using var cancellation = new CancellationTokenSource();
        var queued = service.TranslateAsync("queued", "zh", FakeCredentials, true, true, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, handler.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TranslateAsync("closed", "zh", FakeCredentials, true, true));
    }

    [Fact]
    public async Task JoinsProviderLinesAndLongInputChunksWithoutAddingWordSpaces()
    {
        using var handler = new FakeHandler(async (request, token) =>
            Success(ParseForm(await request.Content!.ReadAsStringAsync(token))["q"]));
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client, minimumInterval: TimeSpan.Zero);
        var source = new string('x', 1001) + "\n\n" + new string('字', 1300);
        Assert.Equal(source, await service.TranslateAsync(source, "zh", FakeCredentials, true, true));
        Assert.True(handler.Count >= 3);
    }

    internal static Dictionary<string, string> ParseForm(string body) => body.Split('&')
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
    internal static HttpResponseMessage Success(string translated) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { trans_result = new[] { new { dst = translated } } })) };

    internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _count;
        public int Count => _count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref _count); return send(request, token); }
    }
}
