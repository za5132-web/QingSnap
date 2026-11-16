using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class BaiduTranslationSegmentTests
{
    private static TranslationCredentials Credentials => new("2015063000000001", "synthetic-key-only");

    [Fact]
    public async Task RejectsUnknownTargetBeforeSending()
    {
        using var handler = new BaiduTranslationTests.FakeHandler((_, _) => Task.FromResult(Response([])));
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client);
        var error = await Assert.ThrowsAsync<TranslationException>(() => service.TranslateSegmentsAsync(
            [new(0, "待翻译文字")], Credentials, true, true, targetLanguage: "auto"));
        Assert.Equal("language", error.Code);
        Assert.Equal(0, handler.Count);
    }

    [Theory]
    [InlineData("zh")][InlineData("en")][InlineData("cht")][InlineData("jp")][InlineData("kor")]
    [InlineData("fra")][InlineData("de")][InlineData("ru")][InlineData("spa")]
    public async Task SendsRequestedTargetAndKeepsAutomaticSource(string target)
    {
        using var handler = new BaiduTranslationTests.FakeHandler(async (request, token) =>
        {
            var form = BaiduTranslationTests.ParseForm(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(target, form["to"]);
            Assert.Equal("auto", form["from"]);
            Assert.Equal("待翻译文字", form["q"]);
            return Response([("待翻译文字", "translated-" + target)]);
        });
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client, minimumInterval: TimeSpan.Zero);
        var result = await service.TranslateSegmentsAsync([new(123, "待翻译文字")], Credentials, true, true, targetLanguage: target);
        Assert.Equal(new TranslatedSegment(123, "translated-" + target), Assert.Single(result));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task BatchesSourceLinesAndMapsReorderedResponsesByEchoedSource()
    {
        using var handler = new BaiduTranslationTests.FakeHandler(async (request, token) =>
        {
            var form = BaiduTranslationTests.ParseForm(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("Hello\nWorld", form["q"]);
            Assert.Equal("zh", form["to"]);
            return Response([( "World", "世界"), ("Hello", "你好")]);
        });
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client, minimumInterval: TimeSpan.Zero);
        var result = await service.TranslateSegmentsAsync([new(70, "Hello"), new(20, "World")], Credentials, true, true);
        Assert.Equal([new TranslatedSegment(70, "你好"), new TranslatedSegment(20, "世界")], result);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("missing")][InlineData("wrong")][InlineData("extra")][InlineData("empty")]
    public async Task RejectsMismatchedMappingWithoutRetry(string kind)
    {
        using var handler = new BaiduTranslationTests.FakeHandler((_, _) => Task.FromResult(kind switch
        {
            "missing" => Response([("Hello", "你好")]),
            "wrong" => Response([("Hello", "你好"), ("Wrong source", "错误")]),
            "extra" => Response([("Hello", "你好"), ("World", "世界"), ("extra", "多余")]),
            _ => Response([("Hello", ""), ("World", "世界")])
        }));
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client, minimumInterval: TimeSpan.Zero);
        var error = await Assert.ThrowsAsync<TranslationException>(() => service.TranslateSegmentsAsync(
            [new(0, "Hello"), new(1, "World")], Credentials, true, true));
        Assert.Equal("mapping", error.Code);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task SplitsLargeParagraphWithoutSplittingEmojiAndRetainsStableId()
    {
        using var handler = new BaiduTranslationTests.FakeHandler(async (request, token) =>
        {
            var source = BaiduTranslationTests.ParseForm(await request.Content!.ReadAsStringAsync(token))["q"];
            Assert.True(source.EnumerateRunes().Count() <= 1000);
            _ = new UTF8Encoding(false, true).GetByteCount(source);
            return Response(source.Split('\n').Select(line => (line, line)).ToArray());
        });
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client, minimumInterval: TimeSpan.Zero);
        var text = string.Concat(Enumerable.Repeat("hello😀 ", 600));
        var result = await service.TranslateSegmentsAsync([new(42, text)], Credentials, true, true);
        Assert.Equal(text, Assert.Single(result).Text);
        Assert.Equal(42, result[0].Id);
        Assert.True(handler.Count > 1);
    }

    [Fact]
    public async Task DuplicateSourceLinesAreNotDropped()
    {
        using var handler = new BaiduTranslationTests.FakeHandler((_, _) => Task.FromResult(Response([("Hello", "你好"), ("Hello", "你好")])));
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client, minimumInterval: TimeSpan.Zero);
        var result = await service.TranslateSegmentsAsync([new(0, "Hello"), new(1, "Hello")], Credentials, true, true);
        Assert.Equal(2, result.Count);
        Assert.All(result, line => Assert.Equal("你好", line.Text));
    }

    [Fact]
    public async Task PreflightNeverSendsDisabledUnconsentedOrOversizedInput()
    {
        using var handler = new BaiduTranslationTests.FakeHandler((_, _) => Task.FromResult(Response([])));
        using var client = new HttpClient(handler);
        using var service = new BaiduTranslationService(client);
        foreach (var (enabled, consent) in new[] { (false, true), (true, false) })
            await Assert.ThrowsAsync<TranslationException>(() => service.TranslateSegmentsAsync([new(0, "Hello")], Credentials, enabled, consent));
        await Assert.ThrowsAsync<TranslationException>(() => service.TranslateSegmentsAsync([new(0, new string('a', 20001))], Credentials, true, true));
        await Assert.ThrowsAsync<TranslationException>(() => service.TranslateSegmentsAsync([new(0, "line\nbad")], Credentials, true, true));
        await Assert.ThrowsAsync<TranslationException>(() => service.TranslateSegmentsAsync([new(0, "one"), new(0, "two")], Credentials, true, true));
        Assert.Equal(0, handler.Count);
    }

    internal static HttpResponseMessage Response(IReadOnlyList<(string Source, string Target)> lines) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { trans_result = lines.Select(line => new { src = line.Source, dst = line.Target }) })) };
}
