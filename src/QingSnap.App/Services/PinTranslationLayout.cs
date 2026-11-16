using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingSnap.App.Models;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;

namespace QingSnap.App.Services;

internal sealed record PinTranslationBlock(int Id, string Text, Rect Bounds, double LineHeight);
internal sealed record PinTranslationPatch(int Id, string Text, Rect Bounds, double FontSize, bool IsTruncated);
internal sealed record PinTranslationDocument(DrawingImage Overlay, OcrRecognitionResult SelectionResult,
    IReadOnlyList<PinTranslationPatch> Patches, string Text, bool ApproximateBackground, string TargetLanguage = "zh");

internal static class PinTranslationLayout
{
    public static IReadOnlyList<PinTranslationBlock> Plan(OcrRecognitionResult source, IReadOnlySet<int>? selected = null,
        CancellationToken cancellationToken = default, string targetLanguage = "zh")
    {
        if (!TranslationLanguages.IsSupported(targetLanguage)) throw new TranslationException("language", "请选择支持的目标语言。");
        var image = new Rect(0, 0, Math.Max(1, source.SourceWidth), Math.Max(1, source.SourceHeight));
        var blocks = new List<PinTranslationBlock>();
        var characters = 0;
        foreach (var item in OcrTextLayoutService.Get(source).Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = item.Source;
            var words = selected is null ? line.Words : line.Words.Where(word => selected.Contains(word.Index)).ToArray();
            if (selected is not null && words.Count == 0) continue;
            var text = selected is null ? line.Text : OcrTextSelectionBuilder.Build(source,
                words.Select(word => word.Index).ToHashSet());
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (!NeedsTranslation(text, targetLanguage, selected is not null)) continue;
            characters += text.Length + 1;
            if (characters > BaiduTranslationService.MaximumSourceLength)
                throw new TranslationException("length", "这张图文字较多，请先选中一部分再翻译。");
            var b = selected is null || words.Count == 0
                ? line.Bounds
                : new OcrTextBounds(words.Min(word => word.Bounds.X), words.Min(word => word.Bounds.Y),
                    words.Max(word => word.Bounds.Right) - words.Min(word => word.Bounds.X),
                    words.Max(word => word.Bounds.Bottom) - words.Min(word => word.Bounds.Y));
            if (!double.IsFinite(b.X) || !double.IsFinite(b.Y) || !double.IsFinite(b.Width) ||
                !double.IsFinite(b.Height) || b.Width < 2 || b.Height < 2 || !double.IsFinite(b.Right) || !double.IsFinite(b.Bottom)) continue;
            var bounds = Rect.Intersect(image, new Rect(b.X, b.Y, b.Width, b.Height));
            if (bounds.IsEmpty || bounds.Width < 2 || bounds.Height < 2) continue;
            var previousIndex = selected is null ? blocks.FindLastIndex(previous => CanJoin(previous, bounds)) : -1;
            if (previousIndex >= 0)
            {
                var previous = blocks[previousIndex];
                var separator = previous.Text.Any(char.IsAsciiLetter) ? " " : string.Empty;
                blocks[previousIndex] = previous with { Text = previous.Text + separator + text, Bounds = Rect.Union(previous.Bounds, bounds) };
            }
            else
            {
                if (blocks.Count >= 500)
                    throw new TranslationException("length", "这张图文字区域较多，请先选中一部分再翻译。");
                blocks.Add(new(blocks.Count, text, bounds, bounds.Height));
            }
        }
        return blocks;
    }

    internal static bool NeedsTranslation(string text, string targetLanguage = "zh", bool explicitSelection = false)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return false;
        // Script alone cannot distinguish English/French/German etc. For other
        // targets (or an explicit selection), let the provider auto-detect the
        // source. Identical returned text is preserved without painting a mask.
        if (targetLanguage != "zh" || explicitSelection) return text.EnumerateRunes().Any(Rune.IsLetter);
        var han = 0;
        var other = 0;
        var kana = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            if (rune.Value is >= 0x3040 and <= 0x30FF) kana++;
            if (rune.Value is >= 0x3400 and <= 0x9FFF or >= 0x20000 and <= 0x323AF) han++;
            else other++;
        }
        // Keep existing Chinese text, numbers and links intact. Japanese kana,
        // Korean, Cyrillic etc. are letters too, not just ASCII English.
        return kana >= 2 || other >= 2 && (han == 0 || other > han * 2);
    }

    private static bool CanJoin(PinTranslationBlock previous, Rect next)
    {
        var gap = next.Top - previous.Bounds.Bottom;
        var height = previous.LineHeight;
        var overlap = Math.Max(0, Math.Min(previous.Bounds.Right, next.Right) - Math.Max(previous.Bounds.Left, next.Left));
        return gap >= -height * 0.2 && gap <= height * 0.8 &&
               next.Height / height is >= 0.85 and <= 1.2 &&
               Math.Abs(previous.Bounds.Left - next.Left) <= height &&
               overlap >= Math.Min(previous.Bounds.Width, next.Width) * 0.7;
    }

    public static PinTranslationDocument Render(BitmapSource source, OcrRecognitionResult original,
        IReadOnlyList<PinTranslationBlock> blocks, IReadOnlyList<TranslatedSegment> translations,
        CancellationToken cancellationToken = default, string targetLanguage = "zh")
    {
        if (!TranslationLanguages.IsSupported(targetLanguage)) throw new TranslationException("language", "请选择支持的目标语言。");
        if (source.PixelWidth != original.SourceWidth || source.PixelHeight != original.SourceHeight)
            throw new TranslationException("geometry", "识字坐标已变化，请重新识别后翻译。");
        var targets = translations.ToDictionary(item => item.Id, item => item.Text);
        if (targets.Count != blocks.Count || blocks.Any(block => !targets.ContainsKey(block.Id)))
            throw new TranslationException("mapping", "译文与图片区域不匹配，已保留原图。");
        var changedBlocks = blocks.Where(block => !string.Equals(block.Text.Trim(), targets[block.Id].Trim(), StringComparison.Ordinal)).ToArray();
        var drawing = new DrawingGroup();
        var patches = new List<PinTranslationPatch>();
        var lines = new List<OcrTextLine>();
        var wordId = 0;
        var approximate = false;
        var converted = source.Format == PixelFormats.Bgra32 ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var typeface = new Typeface(new FontFamily(TranslationLanguages.GetFontFamily(targetLanguage)), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var culture = CultureInfo.GetCultureInfo(TranslationLanguages.GetCultureName(targetLanguage));
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            foreach (var block in changedBlocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = targets[block.Id];
                var (background, varied) = SampleBackground(converted, block.Bounds);
                approximate |= varied;
                var foreground = (background.R * 0.2126 + background.G * 0.7152 + background.B * 0.0722) > 140
                    ? Color.FromRgb(28, 34, 39) : Color.FromRgb(245, 247, 249);
                var rect = block.Bounds;
                var padding = Math.Min(1.5, rect.Height * 0.05);
                var available = new Size(Math.Max(1, rect.Width - padding * 2), Math.Max(1, rect.Height - padding * 2));
                var size = Math.Clamp(block.LineHeight * 0.8, 4, 96);
                FormattedText formatted;
                while (true)
                {
                    formatted = new FormattedText(text, culture, FlowDirection.LeftToRight,
                        typeface, size, new SolidColorBrush(foreground), 1) { MaxTextWidth = available.Width };
                    if (formatted.Height <= available.Height || size <= 4.01) break;
                    size = Math.Max(4, size * 0.9);
                }
                var truncated = formatted.Height > available.Height;
                formatted.MaxTextHeight = available.Height;
                formatted.Trimming = TextTrimming.CharacterEllipsis;
                var position = new Point(rect.Left + padding, rect.Top + padding);
                context.PushClip(new RectangleGeometry(rect));
                context.DrawRectangle(new SolidColorBrush(background), null, rect);
                context.DrawText(formatted, position);
                context.Pop();
                patches.Add(new(block.Id, text, rect, size, truncated));

                var words = new List<OcrTextWord>();
                var spans = new Dictionary<int, (int Start, int End)>();
                var enumerator = StringInfo.GetTextElementEnumerator(text);
                while (enumerator.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var element = enumerator.GetTextElement();
                    if (string.IsNullOrWhiteSpace(element)) continue;
                    var geometry = formatted.BuildHighlightGeometry(position, enumerator.ElementIndex, element.Length);
                    if (geometry is null) continue;
                    var bounds = Rect.Intersect(geometry.Bounds, rect);
                    if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
                    spans[wordId] = (enumerator.ElementIndex, enumerator.ElementIndex + element.Length);
                    words.Add(new(wordId++, lines.Count, element, new(bounds.X, bounds.Y, bounds.Width, bounds.Height)));
                }
                foreach (var row in words.GroupBy(word => Math.Round(word.Bounds.Y, 2)).OrderBy(group => group.Key))
                {
                    var rowWords = row.Select(word => word with { LineIndex = lines.Count }).ToArray();
                    var left = rowWords.Min(word => word.Bounds.X);
                    var top = rowWords.Min(word => word.Bounds.Y);
                    var right = rowWords.Max(word => word.Bounds.Right);
                    var bottom = rowWords.Max(word => word.Bounds.Bottom);
                    var start = rowWords.Min(word => spans[word.Index].Start);
                    var end = rowWords.Max(word => spans[word.Index].End);
                    lines.Add(new(lines.Count, text[start..end],
                        new(left, top, right - left, bottom - top), rowWords));
                }
            }
        }
        // Keep Chinese and unselected source text selectable without another OCR
        // request. Reindex words so translated/source IDs cannot collide.
        foreach (var line in original.Lines)
        {
            var kept = line.Words.Where(word => !changedBlocks.Any(block => block.Bounds.Contains(
                new Point(word.Bounds.X + word.Bounds.Width / 2, word.Bounds.Y + word.Bounds.Height / 2)))).ToArray();
            if (kept.Length == 0) continue;
            var text = kept.Length == line.Words.Count ? line.Text : OcrTextSelectionBuilder.Build(original,
                kept.Select(word => word.Index).ToHashSet(), preserveOriginalLineBreaks: true);
            var words = kept.Select(word => word with { Index = wordId++, LineIndex = lines.Count }).ToArray();
            lines.Add(new(lines.Count, text, line.Bounds, words));
        }
        drawing.Freeze();
        var overlay = new DrawingImage(drawing);
        overlay.Freeze();
        var selection = new OcrRecognitionResult(string.Join(Environment.NewLine, lines.Select(line => line.Text)),
            "译图文字 · " + TranslationLanguages.GetName(targetLanguage), targetLanguage, lines.Count,
            source.PixelWidth, source.PixelHeight, source.PixelWidth, source.PixelHeight, lines);
        return new(overlay, selection, patches, string.Join(Environment.NewLine, blocks.Select(block => targets[block.Id])), approximate, targetLanguage);
    }

    private static (Color Color, bool Varied) SampleBackground(BitmapSource image, Rect bounds)
    {
        var left = Math.Clamp((int)Math.Floor(bounds.Left), 0, image.PixelWidth - 1);
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right), left + 1, image.PixelWidth);
        var top = Math.Clamp((int)Math.Floor(bounds.Top) - 1, 0, image.PixelHeight - 1);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom) + 1, 0, image.PixelHeight - 1);
        var buckets = new Dictionary<int, (int Count, long R, long G, long B)>();
        var total = 0;
        foreach (var y in new[] { top, bottom })
        {
            var pixels = new byte[(right - left) * 4];
            image.CopyPixels(new Int32Rect(left, y, right - left, 1), pixels, pixels.Length, 0);
            for (var x = 0; x < right - left; x += Math.Max(1, (right - left) / 64))
            {
                var i = x * 4;
                if (pixels[i + 3] < 32) continue;
                var r = pixels[i + 2]; var g = pixels[i + 1]; var b = pixels[i];
                var key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
                buckets.TryGetValue(key, out var bucket);
                buckets[key] = (bucket.Count + 1, bucket.R + r, bucket.G + g, bucket.B + b);
                total++;
            }
        }
        if (total == 0) return (Colors.White, true);
        var most = buckets.Values.MaxBy(value => value.Count);
        return (Color.FromRgb((byte)(most.R / most.Count), (byte)(most.G / most.Count), (byte)(most.B / most.Count)),
            most.Count < total * 0.65);
    }
}
