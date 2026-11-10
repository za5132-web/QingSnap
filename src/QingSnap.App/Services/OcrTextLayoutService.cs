using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

internal sealed record OcrLayoutLine(OcrTextLine Source, int Block, int Order);
internal sealed record OcrTextLayout(IReadOnlyList<OcrLayoutLine> Lines, string Text);
internal sealed record OcrTextFragment(OcrLayoutLine Line, string Text, bool Complete);

// A derived view, not a replacement for OCR's raw text, IDs or image coordinates.
internal static class OcrTextLayoutService
{
    private static readonly ConditionalWeakTable<OcrRecognitionResult, OcrTextLayout> Cache = new();
    private static readonly Regex ListStart = new(@"^\s*(?:[-*•●▪▶]|\d+[.)、]|[（(]?[一二三四五六七八九十]+[、.）)])", RegexOptions.CultureInvariant);
    private static readonly Regex CodeStart = new(@"^\s*(?://|#|```|(?:using|import|const|var|function|class|return|SELECT|INSERT)\b)", RegexOptions.CultureInvariant);

    public static Task PrepareAsync(OcrRecognitionResult result, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layout = AnalyzeSafely(result, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Cache.GetValue(result, _ => layout);
        }, cancellationToken);

    public static OcrTextLayout Get(OcrRecognitionResult result) => Cache.GetValue(result, value => AnalyzeSafely(value));

    private static OcrTextLayout AnalyzeSafely(OcrRecognitionResult result, CancellationToken token = default)
    {
        try { return Analyze(result, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            DiagnosticLog.Error("OcrLayout", exception, "整理 OCR 排版失败，保留原始识别结果。");
            return new(result.Lines.OrderBy(line => line.Index)
                .Select((line, i) => new OcrLayoutLine(line, i, i)).ToArray(), result.Text);
        }
    }

    internal static OcrTextLayout Analyze(OcrRecognitionResult result, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lines = result.Lines.ToArray();
        if (lines.Length == 0) return new([], result.Text);
        // Bad geometry must not scramble otherwise usable raw recognition.
        if (lines.Any(line => !Valid(line.Bounds)))
        {
            var rawOrder = lines.OrderBy(line => line.Index).Select((line, i) => new OcrLayoutLine(line, i, i)).ToArray();
            return new(rawOrder, result.Text);
        }
        var ordered = new List<OcrLayoutLine>(lines.Length);
        var block = 0;
        OrderRegion(lines, ordered, ref block, 0, cancellationToken);
        var fragments = ordered.Select(line => new OcrTextFragment(line,
            NormalizeSpacing(line.Source.Text, line.Source.Words), true)).ToArray();
        return new(ordered, Compose(fragments));
    }

    private static bool Valid(OcrTextBounds b) => double.IsFinite(b.X) && double.IsFinite(b.Y) &&
        double.IsFinite(b.Width) && double.IsFinite(b.Height) && b.Width > 0 && b.Height > 0 &&
        double.IsFinite(b.Right) && double.IsFinite(b.Bottom);

    private static void OrderRegion(OcrTextLine[] lines, List<OcrLayoutLine> output, ref int block,
        int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var height = lines.Select(line => line.Bounds.Height).Order().ElementAt(lines.Length / 2);
        if (depth < 8 && lines.Length >= 4 && TryGutter(lines, height, out var split))
        {
            var crossing = lines.Where(line => line.Bounds.X < split && line.Bounds.Right > split)
                .OrderBy(line => line.Bounds.Y).ToArray();
            if (crossing.Length == 0)
            {
                OrderRegion(lines.Where(line => line.Bounds.Right <= split).ToArray(), output, ref block, depth + 1, cancellationToken);
                OrderRegion(lines.Where(line => line.Bounds.X >= split).ToArray(), output, ref block, depth + 1, cancellationToken);
                return;
            }
            // Full-width headings/footers separate column sections. Never cut
            // through a line whose vertical span overlaps the supposed columns.
            var remaining = lines.Except(crossing).ToList();
            foreach (var heading in crossing)
            {
                var before = remaining.Where(line => line.Bounds.Bottom <= heading.Bounds.Y).ToArray();
                if (before.Length > 0) OrderRegion(before, output, ref block, depth + 1, cancellationToken);
                var beforeSet = before.ToHashSet();
                remaining.RemoveAll(beforeSet.Contains);
                output.Add(new(heading, block++, output.Count));
            }
            if (remaining.Count > 0) OrderRegion(remaining.ToArray(), output, ref block, depth + 1, cancellationToken);
            return;
        }
        var currentBlock = block++;
        foreach (var line in lines.OrderBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X).ThenBy(line => line.Index))
            output.Add(new(line, currentBlock, output.Count));
    }

    private static bool TryGutter(OcrTextLine[] lines, double height, out double split)
    {
        split = 0;
        var left = lines.Min(line => line.Bounds.X);
        var right = lines.Max(line => line.Bounds.Right);
        var narrow = lines.Where(line => line.Bounds.Width <= (right - left) * 0.72)
            .OrderBy(line => line.Bounds.X).ToArray();
        if (narrow.Length < 4) return false;
        var candidates = new List<(double Width, double Middle)>();
        var end = narrow[0].Bounds.Right;
        foreach (var line in narrow.Skip(1))
        {
            var gap = line.Bounds.X - end;
            if (gap >= Math.Max(height * 1.8, (right - left) * 0.025))
                candidates.Add((gap, (line.Bounds.X + end) / 2));
            end = Math.Max(end, line.Bounds.Right);
        }
        foreach (var candidate in candidates.OrderByDescending(item => item.Width).Take(8))
        {
            var middle = candidate.Middle;
            var a = lines.Where(line => line.Bounds.Right <= middle).ToArray();
            var b = lines.Where(line => line.Bounds.X >= middle).ToArray();
            if (a.Length < 2 || b.Length < 2) continue;
            if (Math.Min(a.Max(line => line.Bounds.Bottom), b.Max(line => line.Bounds.Bottom)) -
                Math.Max(a.Min(line => line.Bounds.Y), b.Min(line => line.Bounds.Y)) < height) continue;
            var crossing = lines.Where(line => line.Bounds.X < middle && line.Bounds.Right > middle).ToArray();
            if (crossing.Length > lines.Length / 3) continue;
            if (crossing.Any(span => a.Concat(b).Any(line =>
                    line.Bounds.Y < span.Bounds.Bottom && line.Bounds.Bottom > span.Bounds.Y))) continue;
            split = middle;
            return true;
        }
        return false;
    }

    public static string Compose(IReadOnlyList<OcrTextFragment> fragments)
    {
        var text = new StringBuilder();
        OcrTextFragment? previous = null;
        foreach (var fragment in fragments.Where(item => item.Text.Length > 0))
        {
            if (previous is not null)
            {
                if (CanJoin(previous, fragment))
                {
                    if (NeedsWordSpace(previous.Text[^1], fragment.Text[0])) text.Append(' ');
                }
                else text.Append(Environment.NewLine);
            }
            text.Append(fragment.Text);
            previous = fragment;
        }
        return text.ToString();
    }

    private static bool CanJoin(OcrTextFragment a, OcrTextFragment b)
    {
        if (!a.Complete || !b.Complete || a.Line.Block != b.Line.Block || b.Line.Order != a.Line.Order + 1) return false;
        var first = a.Line.Source.Bounds;
        var next = b.Line.Source.Bounds;
        var height = Math.Min(first.Height, next.Height);
        if (height <= 0 || Math.Max(first.Height, next.Height) > height * 1.25 ||
            Math.Abs(first.X - next.X) > height * 0.6 ||
            next.Y - first.Bottom < -height * 0.1 || next.Y - first.Bottom > height * 0.75) return false;
        var x = a.Text.Trim();
        var y = b.Text.Trim();
        if (x.Length == 0 || y.Length == 0 || IsStructured(a.Text) || IsStructured(b.Text)) return false;
        if ("。！？.!?：:；;".Contains(x[^1])) return false;
        // Short lines are ambiguous (poetry, menus, labels, chat). Prefer a
        // visible newline to silently flattening intentional structure.
        var chinese = x.Count(IsCjk);
        if (chinese > 0)
        {
            var phrases = x.Split(['，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (phrases.Length >= 3 && phrases.All(phrase => phrase.Count(IsCjk) <= 9)) return false;
            return chinese >= 20 && first.Width / height >= 16;
        }
        return x.Length >= 40 && x.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 7;
    }

    internal static bool IsStructured(string text) => ListStart.IsMatch(text) || CodeStart.IsMatch(text) ||
        text.Contains('\t') || text.Contains("  ", StringComparison.Ordinal) ||
        text.Contains("://", StringComparison.Ordinal) || text.Contains('\\') || text.Contains("=>", StringComparison.Ordinal) ||
        text.IndexOfAny(['{', '}', '|']) >= 0 || Regex.IsMatch(text, @"\b[\w]+_[\w]+\b", RegexOptions.CultureInvariant);

    internal static bool IsCjk(char c) => c is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
    internal static bool NeedsWordSpace(char left, char right) =>
        !IsCjk(left) && !IsCjk(right) && char.IsLetterOrDigit(left) && char.IsLetterOrDigit(right);

    public static string NormalizeSpacing(string text, IReadOnlyList<OcrTextWord> words)
    {
        if (words.Count < 2 || IsStructured(text)) return text;
        var cjkCount = text.Count(IsCjk);
        var fragmentedLatinSpaces = Regex.Matches(text, @"\b(?:[A-Za-z] ){2,}[A-Za-z]\b", RegexOptions.CultureInvariant)
            .Cast<Match>().SelectMany(match => Enumerable.Range(match.Index, match.Length).Where(i => text[i] == ' ')).ToHashSet();
        var remove = new HashSet<int>();
        var cursor = 0;
        OcrTextWord? previous = null;
        var previousEnd = 0;
        foreach (var word in words.OrderBy(word => word.Index))
        {
            if (word.Text.Length == 0) return text;
            var start = text.IndexOf(word.Text, cursor, StringComparison.Ordinal);
            if (start < 0) return text;
            if (previous is not null && start == previousEnd + 1 && text[previousEnd] == ' ' &&
                Valid(previous.Bounds) && Valid(word.Bounds))
            {
                var gap = word.Bounds.X - previous.Bounds.Right;
                var height = Math.Min(word.Bounds.Height, previous.Bounds.Height);
                var cjk = cjkCount >= 6 && IsCjk(previous.Text[^1]) && IsCjk(word.Text[0]);
                var latinChars = fragmentedLatinSpaces.Contains(previousEnd) && previous.Text.Length == 1 && word.Text.Length == 1 &&
                    char.IsAsciiLetter(previous.Text[0]) && char.IsAsciiLetter(word.Text[0]);
                if (gap >= -height * 0.1 && gap <= height * (cjk ? 0.4 : 0.12) && (cjk || latinChars))
                    remove.Add(previousEnd);
            }
            cursor = start + word.Text.Length;
            previous = word;
            previousEnd = cursor;
        }
        if (remove.Count == 0) return text;
        return string.Concat(text.Where((_, i) => !remove.Contains(i)));
    }
}
