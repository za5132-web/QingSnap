using QingSnap.App.Models;

namespace QingSnap.App.Services;

// Two recent language/scope results per pin; no full-size bitmap or global cache.
internal sealed class PinTranslationCache
{
    internal const int Capacity = 2;
    private readonly LinkedList<Entry> _entries = new();
    private sealed record Entry(string Scope, OcrRecognitionResult Original, PinTranslationDocument Document);
    internal int Count => _entries.Count;

    public bool TryGet(string target, string scope, OcrRecognitionResult original, out PinTranslationDocument? document)
    {
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            var entry = node.Value;
            if (entry.Scope != scope || entry.Document.TargetLanguage != target || !ReferenceEquals(entry.Original, original)) continue;
            _entries.Remove(node);
            _entries.AddFirst(node);
            document = entry.Document;
            return true;
        }
        document = null;
        return false;
    }

    public void Store(string scope, OcrRecognitionResult original, PinTranslationDocument document)
    {
        for (var node = _entries.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.Scope == scope && node.Value.Document.TargetLanguage == document.TargetLanguage) _entries.Remove(node);
            node = next;
        }
        _entries.AddFirst(new Entry(scope, original, document));
        while (_entries.Count > Capacity) _entries.RemoveLast();
    }

    public void Clear() => _entries.Clear();
}
