using QingSnap.App.Models;

namespace QingSnap.App.Services;

internal static class HistorySearchMatcher
{
    public static bool IsMatch(HistoryItem item, string query)
    {
        var parsed = HistorySearchQuery.Parse(query);
        if (parsed.Terms.Count == 0 && parsed.TagTerms.Count == 0)
        {
            return true;
        }

        var tags = item.Tags ?? [];
        var tagsMatch = parsed.TagTerms.All(tagTerm =>
            tags.Any(tag => tag.Equals(tagTerm, StringComparison.OrdinalIgnoreCase)));
        var termsMatch = parsed.Terms.All(term =>
            Contains(item.FileName, term) ||
            Contains(item.DimensionsText, term) ||
            Contains(item.SearchText, term) ||
            Contains(item.SourceProcess, term) ||
            Contains(item.SourceWindowTitle, term) ||
            tags.Any(tag => Contains(tag, term)));
        return tagsMatch && termsMatch;
    }

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}

internal sealed record HistorySearchQuery(
    IReadOnlyList<string> Terms,
    IReadOnlyList<string> TagTerms)
{
    public static HistorySearchQuery Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new HistorySearchQuery([], []);
        }

        var terms = new List<string>();
        var tagTerms = new List<string>();
        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            {
                var tag = HistoryMetadataStore.NormalizeTagName(token[4..]);
                if (tag.Length > 0)
                {
                    tagTerms.Add(tag);
                }
            }
            else
            {
                terms.Add(token);
            }
        }

        return new HistorySearchQuery(terms, tagTerms);
    }
}
