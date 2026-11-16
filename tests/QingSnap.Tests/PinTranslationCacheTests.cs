using System.Windows.Media;
using QingSnap.App.Models;
using QingSnap.App.Services;
using Xunit;

namespace QingSnap.Tests;

public sealed class PinTranslationCacheTests
{
    private static OcrRecognitionResult Source() => new("source", "test", "en", 0, 10, 10, 10, 10, []);
    private static PinTranslationDocument Document(string target, OcrRecognitionResult source) =>
        new(new DrawingImage(), source, [], "translated", false, target);

    [Fact]
    public void SeparatesLanguageScopeAndSourceIdentity()
    {
        var cache = new PinTranslationCache();
        var source = Source();
        var zh = Document("zh", source);
        cache.Store("all", source, zh);
        Assert.True(cache.TryGet("zh", "all", source, out var result));
        Assert.Same(zh, result);
        Assert.False(cache.TryGet("en", "all", source, out _));
        Assert.False(cache.TryGet("zh", "0,1", source, out _));
        Assert.False(cache.TryGet("zh", "all", Source(), out _));
    }

    [Fact]
    public void RetainsAtMostTwoRecentlyUsedDocumentsAndClearsOnClose()
    {
        var cache = new PinTranslationCache();
        var source = Source();
        cache.Store("all", source, Document("zh", source));
        cache.Store("all", source, Document("en", source));
        Assert.True(cache.TryGet("zh", "all", source, out _));
        cache.Store("all", source, Document("jp", source));
        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet("en", "all", source, out _));
        Assert.True(cache.TryGet("zh", "all", source, out _));
        cache.Store("all", source, Document("zh", source));
        Assert.Equal(2, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }
}
