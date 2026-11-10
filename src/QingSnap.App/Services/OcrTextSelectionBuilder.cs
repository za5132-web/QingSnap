using System.Text;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

internal static class OcrTextSelectionBuilder
{
    public static string Build(
        OcrRecognitionResult result,
        IReadOnlySet<int> selectedWordIndices,
        bool preserveOriginalLineBreaks = false)
    {
        if (selectedWordIndices.Count == 0)
        {
            return string.Empty;
        }

        var selectedLines = new List<string>();
        var fragments = new List<OcrTextFragment>();
        var ordered = preserveOriginalLineBreaks
            ? result.Lines.OrderBy(line => line.Index).Select((line, i) => new OcrLayoutLine(line, i, i))
            : OcrTextLayoutService.Get(result).Lines;
        foreach (var item in ordered)
        {
            var line = item.Source;
            var words = line.Words.OrderBy(word => word.Index).ToArray();
            if (!words.Any(word => selectedWordIndices.Contains(word.Index)))
            {
                continue;
            }

            var selectedText = TryBuildFromLineText(line.Text, words, selectedWordIndices, out var text)
                ? text
                : BuildFromGeometry(words, selectedWordIndices);
            selectedLines.Add(selectedText);
            fragments.Add(new(item, OcrTextLayoutService.NormalizeSpacing(selectedText,
                words.Where(word => selectedWordIndices.Contains(word.Index)).ToArray()),
                words.All(word => selectedWordIndices.Contains(word.Index))));
        }

        return preserveOriginalLineBreaks
            ? string.Join(Environment.NewLine, selectedLines.Where(text => text.Length > 0))
            : OcrTextLayoutService.Compose(fragments);
    }

    private static bool TryBuildFromLineText(
        string lineText,
        IReadOnlyList<OcrTextWord> words,
        IReadOnlySet<int> selectedWordIndices,
        out string selectedText)
    {
        selectedText = string.Empty;
        if (string.IsNullOrEmpty(lineText) || words.Count == 0)
        {
            return false;
        }

        var spans = new (int Start, int End)[words.Count];
        var cursor = 0;
        for (var index = 0; index < words.Count; index++)
        {
            var wordText = words[index].Text;
            if (string.IsNullOrEmpty(wordText))
            {
                return false;
            }

            var start = lineText.IndexOf(wordText, cursor, StringComparison.Ordinal);
            if (start < 0)
            {
                return false;
            }

            spans[index] = (start, start + wordText.Length);
            cursor = spans[index].End;
        }

        var runs = new List<string>();
        var runStart = -1;
        for (var index = 0; index <= words.Count; index++)
        {
            var selected = index < words.Count && selectedWordIndices.Contains(words[index].Index);
            if (selected && runStart < 0)
            {
                runStart = index;
            }

            if (selected || runStart < 0)
            {
                continue;
            }

            var runEnd = index - 1;
            var start = spans[runStart].Start;
            var end = spans[runEnd].End;
            // Keep indentation/trailing whitespace when a selection reaches a
            // line edge, but never append unselected non-whitespace characters.
            if (runStart == 0 && string.IsNullOrWhiteSpace(lineText[..start])) start = 0;
            if (runEnd == words.Count - 1 && string.IsNullOrWhiteSpace(lineText[end..])) end = lineText.Length;
            runs.Add(lineText[start..end]);
            runStart = -1;
        }

        selectedText = string.Join(' ', runs);
        return runs.Count > 0;
    }

    private static string BuildFromGeometry(
        IReadOnlyList<OcrTextWord> words,
        IReadOnlySet<int> selectedWordIndices)
    {
        var selected = words.Where(word => selectedWordIndices.Contains(word.Index)).ToArray();
        var text = new StringBuilder();
        for (var index = 0; index < selected.Length; index++)
        {
            if (index > 0)
            {
                var previous = selected[index - 1];
                var current = selected[index];
                var gap = current.Bounds.X - previous.Bounds.Right;
                if (gap > Math.Max(2, Math.Min(previous.Bounds.Height, current.Bounds.Height) * 0.32))
                {
                    text.Append(' ');
                }
            }

            text.Append(selected[index].Text);
        }

        return text.ToString();
    }
}
