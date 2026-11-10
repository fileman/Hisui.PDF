using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Wordprocessing;
using Hisui.Pdf.Core.Model;

namespace Hisui.Pdf.Core.Services;

internal sealed partial class PdfWordExporter
{
    private const double ParagraphGapEm = 1.75;   // centre-to-centre pitch above this (in em) is a blank line
    private const double ShortLineFraction = 0.15; // a line ending this far before the right edge ends its paragraph
    private const double FlushTolerance = 0.03;    // edges this close (fraction of the content width) are flush

    private sealed record Line(List<TextWord> Words, PdfRect Box, double FontPt);

    /// <summary>A paragraph (text or picture) with where it sits on the page, for the vertical spacing pass.</summary>
    private sealed record Block(Paragraph Paragraph, double Top, double Bottom, double FontPt);

    private static List<Paragraph> BuildPage(DocWriter writer, PageContent content, PageLayout layout)
    {
        var result = new List<Paragraph>();

        // Rotated lines (margin stamps, vertical labels): each one as a small paragraph at the top of the page.
        foreach (var word in content.Words.Where(w => !w.IsHorizontal).OrderBy(w => w.BoundingBox.Left))
        {
            var props = new ParagraphProperties(new SpacingBetweenLines { Before = "0" });
            var paragraph = new Paragraph(props);
            AppendRuns(writer, paragraph, [new Segment(word.Text, StyleOf(word))]);
            result.Add(paragraph);
        }

        var blocks = TextBlocks(writer, content.Words.Where(w => w.IsHorizontal).ToList(), layout);

        // Pictures go before the first text block that starts below them.
        foreach (var picture in content.Pictures.OrderBy(p => p.Box.Top))
        {
            var block = new Block(PictureParagraph(writer, picture, layout), picture.Box.Top, picture.Box.Bottom, 0);
            var at = blocks.FindIndex(b => b.Top >= picture.Box.Top);
            blocks.Insert(at < 0 ? blocks.Count : at, block);
        }

        // Vertical gaps become "spacing before", so blank space on the page survives without empty paragraphs.
        Block? previous = null;
        foreach (var block in blocks)
        {
            if (previous is not null)
            {
                var gapPt = (block.Top - previous.Bottom) * layout.HeightPt - 0.25 * Math.Max(block.FontPt, previous.FontPt);
                var twips = (int)Math.Clamp(Math.Round(gapPt * 20), 0, 1440);
                if (twips > 0 && block.Paragraph.ParagraphProperties?.GetFirstChild<SpacingBetweenLines>() is { } spacing)
                    spacing.Before = twips.ToString(CultureInfo.InvariantCulture);
            }
            previous = block;
            result.Add(block.Paragraph);
        }
        return result;
    }

    // ── lines and paragraphs ─────────────────────────────────────────────────

    private static List<Block> TextBlocks(DocWriter writer, List<TextWord> words, PageLayout layout)
    {
        var blocks = new List<Block>();
        if (words.Count == 0) return blocks;

        foreach (var lines in Paragraphs(Lines(words, layout), layout))
        {
            var props = new ParagraphProperties(new SpacingBetweenLines { Before = "0" });
            var (justification, indent, firstLine) = Alignment(lines, layout);
            if (indent > 0 || firstLine != 0)
            {
                var ind = new Indentation();
                if (indent > 0) ind.Left = indent.ToString(CultureInfo.InvariantCulture);
                if (firstLine > 0) ind.FirstLine = firstLine.ToString(CultureInfo.InvariantCulture);
                else if (firstLine < 0) ind.Hanging = (-firstLine).ToString(CultureInfo.InvariantCulture);
                props.Append(ind);
            }
            if (justification is { } jc) props.Append(new Justification { Val = jc });

            var paragraph = new Paragraph(props);
            AppendRuns(writer, paragraph, Segments(lines));
            var box = Union(lines.Select(l => l.Box));
            blocks.Add(new Block(paragraph, box.Top, box.Bottom, lines.Max(l => l.FontPt)));
        }
        return blocks;
    }

    private const double RowTolerance = 0.4;  // centres closer than this (× the taller word) share a row
    private const double SegmentGapEm = 2.5;  // a wider horizontal gap splits a row (columns, tab stops)

    /// <summary>
    /// Visual lines in reading order. Words are first gathered into rows by their vertical centre, each row is
    /// cut where a gap is wider than <see cref="SegmentGapEm"/> em (columns), and the resulting lines are then
    /// ordered as a whole. Ordering whole lines keeps a list number with its item: ordering single words lets a
    /// narrow column of numbers come apart from the text beside it.
    /// </summary>
    private static List<Line> Lines(List<TextWord> words, PageLayout layout)
    {
        var rows = new List<(double Centre, double Height, List<TextWord> Words)>();
        foreach (var w in words.OrderBy(Centre))
        {
            var c = Centre(w);
            var h = w.BoundingBox.Height;
            var i = rows.FindLastIndex(r => Math.Abs(c - r.Centre) < RowTolerance * Math.Max(r.Height, h));
            if (i < 0) { rows.Add((c, h, [w])); continue; }
            var row = rows[i];
            row.Words.Add(w);
            rows[i] = (row.Centre + (c - row.Centre) / row.Words.Count, Math.Max(row.Height, h), row.Words);
        }

        var lines = new List<Line>();
        foreach (var row in rows)
        {
            var sorted = row.Words.OrderBy(w => w.BoundingBox.Left).ToList();
            var current = new List<TextWord> { sorted[0] };
            for (var k = 1; k < sorted.Count; k++)
            {
                var em = Math.Max(sorted[k].FontSizePoints, sorted[k - 1].FontSizePoints) / layout.WidthPt;
                if (sorted[k].BoundingBox.Left - sorted[k - 1].BoundingBox.Right > SegmentGapEm * em)
                {
                    lines.Add(MakeLine(current));
                    current = [];
                }
                current.Add(sorted[k]);
            }
            lines.Add(MakeLine(current));
        }
        return ReadingOrder.Sort(lines, l => l.Box);
    }

    private static double Centre(TextWord w) => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2;

    private static Line MakeLine(List<TextWord> words)
    {
        var sizes = words.Select(w => w.FontSizePoints).Order().ToList();
        return new Line(words, Union(words.Select(w => w.BoundingBox)), sizes[sizes.Count / 2]);
    }

    /// <summary>
    /// Lines into paragraphs. A paragraph ends at a blank line, a jump back up (next column), a list item, a
    /// clear change of font size, or a line that stops well short of the right edge (a hard line end: the
    /// lines of an address block, a heading, the last line of a paragraph).
    /// </summary>
    private static List<List<Line>> Paragraphs(List<Line> lines, PageLayout layout)
    {
        var result = new List<List<Line>>();
        var contentWidth = Math.Max(layout.Content.Width, 0.01);
        Line? prev = null;
        foreach (var line in lines)
        {
            var start = prev is null || result.Count == 0;
            if (!start)
            {
                var em = Math.Max(prev!.FontPt, line.FontPt) / layout.HeightPt;
                var pitch = (line.Box.Top + line.Box.Bottom) / 2 - (prev.Box.Top + prev.Box.Bottom) / 2;
                start = pitch > em * ParagraphGapEm
                        || pitch < 0
                        || prev.Box.Right < layout.Content.Right - ShortLineFraction * contentWidth
                        || IsListItem(line)
                        || StartsElsewhere(prev, line, firstOfParagraph: result[^1].Count == 1, layout.Content)
                        || Math.Abs(line.FontPt - prev.FontPt) > 0.25 * Math.Max(line.FontPt, prev.FontPt);
            }
            if (start) result.Add([line]);
            else result[^1].Add(line);
            prev = line;
        }
        return result;
    }

    /// <summary>
    /// Running text keeps its left edge from line to line (only a first line may be indented). Lines that start
    /// somewhere else (right-aligned or centred address lines, a name and its e-mail) are separate lines.
    /// </summary>
    private static bool StartsElsewhere(Line prev, Line line, bool firstOfParagraph, PdfRect content)
    {
        var width = Math.Max(content.Width, 0.01);
        var shift = line.Box.Left - prev.Box.Left;
        if (Math.Abs(shift) <= FlushTolerance * width) return false;

        // A first-line indent: a slightly indented, full first line, then the text moves back left.
        var indentedFullLine = prev.Box.Left - content.Left < 0.15 * width && content.Right - prev.Box.Right < FlushTolerance * width;
        return !(firstOfParagraph && shift < 0 && indentedFullLine);
    }

    [GeneratedRegex(@"^(\d{1,3}[.)]|[a-zA-Z][.)]|[•·▪‣◦●■–-])$")]
    private static partial Regex ListMarker();

    private static bool IsListItem(Line line) => line.Words.Count > 1 && ListMarker().IsMatch(line.Words[0].Text);

    /// <summary>
    /// Alignment from where the lines sit inside the content box: flush right and well indented → right; centred
    /// → centre; several lines flush on both sides → justified; otherwise left, with the indent of the paragraph.
    /// </summary>
    private static (JustificationValues? Jc, int Indent, int FirstLine) Alignment(List<Line> lines, PageLayout layout)
    {
        var c = layout.Content;
        var width = Math.Max(c.Width, 0.01);
        var tolerance = FlushTolerance * width;
        var left = lines.Min(l => l.Box.Left);
        var flushLeft = left - c.Left < tolerance;

        if (lines.All(l => c.Right - l.Box.Right < tolerance) && left - c.Left > 0.25 * width)
            return (JustificationValues.Right, 0, 0);

        var centre = (c.Left + c.Right) / 2;
        if (!flushLeft && lines.All(l => Math.Abs((l.Box.Left + l.Box.Right) / 2 - centre) < tolerance))
            return (JustificationValues.Center, 0, 0);

        var indent = layout.IndentTwips(left);
        var firstLine = lines.Count > 1 ? layout.IndentTwips(lines[0].Box.Left) - indent : 0;
        if (Math.Abs(firstLine) < 60) firstLine = 0; // under 3 pt is noise
        if (indent < 60) indent = 0;

        var justified = lines.Count > 1 && lines.SkipLast(1).All(l => c.Right - l.Box.Right < tolerance);
        return (justified ? JustificationValues.Both : null, indent, firstLine);
    }

    // ── runs ─────────────────────────────────────────────────────────────────

    private readonly record struct TextStyle(bool Bold, string? Color, int HalfPoints);

    private sealed record Segment(string Text, TextStyle Style);

    private static TextStyle StyleOf(TextWord w)
    {
        var argb = w.ColorArgb;
        int r = (int)(argb >> 16) & 0xFF, g = (int)(argb >> 8) & 0xFF, b = (int)argb & 0xFF;
        var color = r < 0x30 && g < 0x30 && b < 0x30 ? null : $"{r:X2}{g:X2}{b:X2}";
        var half = Math.Clamp((int)Math.Round(w.FontSizePoints * 2), 12, 144);
        return new TextStyle(w.IsBold, color, half);
    }

    /// <summary>
    /// The paragraph's text as styled segments: words joined by spaces, lines joined by a space, or with
    /// nothing when a word was hyphenated across the line end ("organiz-" + "zative" → "organizzative").
    /// </summary>
    private static List<Segment> Segments(List<Line> lines)
    {
        var segments = new List<Segment>();
        var text = new StringBuilder();
        TextStyle? style = null;

        void Add(string piece, TextStyle s)
        {
            if (style is { } current && current != s)
            {
                segments.Add(new Segment(text.ToString(), current));
                text.Clear();
            }
            style = s;
            text.Append(piece);
        }

        for (var li = 0; li < lines.Count; li++)
        {
            var words = lines[li].Words;
            for (var wi = 0; wi < words.Count; wi++)
            {
                var word = words[wi].Text;
                var joiner = li == 0 && wi == 0 ? "" : " ";
                if (wi == 0 && li > 0 && EndsHyphenated(text) && word.Length > 0 && char.IsLower(word[0]))
                {
                    text.Length--; // drop the hyphen
                    joiner = "";
                }
                var s = StyleOf(words[wi]);
                if (joiner.Length > 0) text.Append(joiner);
                Add(word, s);
            }
        }
        if (style is { } last && text.Length > 0) segments.Add(new Segment(text.ToString(), last));
        return segments;
    }

    private static bool EndsHyphenated(StringBuilder text) =>
        text.Length > 1 && text[^1] == '-' && char.IsLetter(text[^2]);

    [GeneratedRegex(@"(?<url>(?:https?://|www\.)[^\s<>""]+)|(?<mail>[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+)")]
    private static partial Regex LinkPattern();

    /// <summary>Appends the segments as runs, turning URLs and e-mail addresses into clickable links.</summary>
    private static void AppendRuns(DocWriter writer, Paragraph paragraph, IEnumerable<Segment> segments)
    {
        foreach (var segment in segments)
        {
            var text = segment.Text;
            var pos = 0;
            foreach (Match m in LinkPattern().Matches(text))
            {
                var value = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"');
                if (value.Length == 0) continue;
                var target = m.Groups["mail"].Success ? "mailto:" + value
                    : value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + value : value;
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) continue;

                if (m.Index > pos) paragraph.Append(MakeRun(text[pos..m.Index], segment.Style, link: false));
                var rel = writer.Main.AddHyperlinkRelationship(uri, isExternal: true);
                paragraph.Append(new Hyperlink(MakeRun(value, segment.Style, link: true)) { Id = rel.Id, History = true });
                pos = m.Index + value.Length;
            }
            if (pos < text.Length) paragraph.Append(MakeRun(text[pos..], segment.Style, link: false));
        }
    }

    private static Run MakeRun(string text, TextStyle style, bool link)
    {
        var props = new RunProperties();
        if (link) props.Append(new RunStyle { Val = "Hyperlink" });
        if (style.Bold) props.Append(new Bold());
        if (style.Color is { } color && !link) props.Append(new Color { Val = color });
        props.Append(new FontSize { Val = style.HalfPoints.ToString(CultureInfo.InvariantCulture) });
        return new Run(props, new Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });
    }
}
