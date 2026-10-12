using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// PDF → Excel (one sheet per page: words regrouped into rows and aligned columns, numbers as numbers) and
/// PDF → PowerPoint (one slide per page, page image full size, page text as the picture's alternative text).
/// ponytail: table detection is geometric (no ruling lines, no merged cells); slides are not editable text.
/// </summary>
internal sealed class PdfOfficeExporter : IPdfOfficeExporter
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    private readonly IPdfTextExtractor _text;
    private readonly IPdfRenderer _renderer;

    public PdfOfficeExporter(IPdfTextExtractor text, IPdfRenderer renderer) => (_text, _renderer) = (text, renderer);

    // ── Excel ────────────────────────────────────────────────────────────────

    public async Task<byte[]> ExportXlsxAsync(byte[] pdf, CancellationToken ct = default)
    {
        var pages = await _text.ExtractAllWordsAsync(pdf, ct);
        return await Task.Run(() =>
        {
            using var ms = new MemoryStream();
            using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
            {
                var wb = doc.AddWorkbookPart();
                wb.Workbook = new S.Workbook();
                var sheets = wb.Workbook.AppendChild(new S.Sheets());
                for (var p = 0; p < Math.Max(1, pages.Count); p++)
                {
                    ct.ThrowIfCancellationRequested();
                    var part = wb.AddNewPart<WorksheetPart>();
                    var data = new S.SheetData();
                    var grid = p < pages.Count ? Table(pages[p]) : [];
                    for (var r = 0; r < grid.Count; r++)
                    {
                        var row = new S.Row { RowIndex = (uint)(r + 1) };
                        for (var c = 0; c < grid[r].Length; c++)
                            if (!string.IsNullOrEmpty(grid[r][c])) row.Append(Cell(c, r, grid[r][c]));
                        data.Append(row);
                    }
                    part.Worksheet = new S.Worksheet(data);
                    sheets.Append(new S.Sheet { Id = wb.GetIdOfPart(part), SheetId = (uint)(p + 1), Name = $"Pagina {p + 1}" });
                }
            }
            return ms.ToArray();
        }, ct);
    }

    /// <summary>Rows by vertical position, cells by horizontal gaps, columns by clustering the cells' left edges.</summary>
    internal static List<string[]> Table(IReadOnlyList<TextWord> allWords)
    {
        // Rotated text (vertical column headers) is set aside and becomes a header row over its column.
        var words = allWords.Where(w => w.IsHorizontal).ToList();
        var rotated = allWords.Where(w => !w.IsHorizontal).ToList();
        if (words.Count == 0) return [];
        var heights = words.Select(w => w.BoundingBox.Height).Order().ToList();
        var lineTol = Math.Max(heights[heights.Count / 2] * 0.5, 0.002);

        var rows = new List<List<TextWord>>();
        foreach (var w in words.OrderBy(w => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2))
        {
            var cy = (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2;
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs((row[0].BoundingBox.Top + row[0].BoundingBox.Bottom) / 2 - cy) > lineTol) rows.Add(row = []);
            row.Add(w);
        }

        // Within a row, a gap wider than ~1.5 average character widths starts a new cell.
        var cellRows = rows.Select(row =>
        {
            row.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            var cells = new List<(double Left, string Text)>();
            TextWord? prev = null;
            foreach (var w in row)
            {
                var charW = w.BoundingBox.Width / Math.Max(1, w.Text.Length);
                if (prev is null || w.BoundingBox.Left - prev.BoundingBox.Right > charW * 1.5) cells.Add((w.BoundingBox.Left, w.Text));
                else cells[^1] = (cells[^1].Left, cells[^1].Text + " " + w.Text);
                prev = w;
            }
            return cells;
        }).ToList();

        // Columns: left edges closer than 1.5% of the page width share a column.
        var lefts = cellRows.SelectMany(r => r.Select(c => c.Left)).Concat(rotated.Select(w => w.BoundingBox.Left)).Order().ToList();
        var columns = new List<double>();
        foreach (var x in lefts)
            if (columns.Count == 0 || x - columns[^1] > 0.015) columns.Add(x);
        int ColumnOf(double x)
        {
            var best = 0;
            for (var i = 0; i < columns.Count; i++) if (columns[i] <= x + 0.015) best = i;
            return best;
        }

        var table = cellRows.Select(r =>
        {
            var line = new string[columns.Count];
            foreach (var (left, text) in r)
            {
                var c = ColumnOf(left);
                line[c] = line[c] is null ? text : line[c] + " " + text;
            }
            return line;
        }).ToList();

        if (rotated.Count > 0)
        {
            var header = new string[columns.Count];
            // Bottom-up text reads from the lowest word; join each column's words in that order.
            foreach (var group in rotated.GroupBy(w => ColumnOf(w.BoundingBox.Left)))
                header[group.Key] = string.Join(' ', group.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
            table.Insert(0, header);
        }
        return table;
    }

    private static S.Cell Cell(int col, int row, string text)
    {
        var reference = ColumnName(col) + (row + 1);
        var compact = text.Replace("€", "").Replace(" ", "").Trim();
        if (compact.Length > 0 && compact.Length < 20 && compact.Any(char.IsDigit) &&
            (double.TryParse(compact, NumberStyles.Number, It, out var n) || double.TryParse(compact, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) &&
            !(compact.StartsWith('0') && compact.Length > 1 && !compact.StartsWith("0,") && !compact.StartsWith("0."))) // keep codes like 00123 as text
            return new S.Cell { CellReference = reference, DataType = S.CellValues.Number, CellValue = new S.CellValue(n.ToString(CultureInfo.InvariantCulture)) };
        return new S.Cell { CellReference = reference, DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(text) { Space = SpaceProcessingModeValues.Preserve }) };
    }

    private static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    // ── PowerPoint ───────────────────────────────────────────────────────────

    private const long Emu = 12700; // per point

    public async Task<byte[]> ExportPptxAsync(byte[] pdf, int dpi = 150, CancellationToken ct = default)
    {
        var count = await _renderer.GetPageCountAsync(pdf, ct);
        var sizes = await Task.Run(() => PageSizes(pdf), ct);
        var images = new List<byte[]>();
        for (var i = 0; i < count; i++) images.Add(await _renderer.RenderPageToPngAsync(pdf, i, dpi, ct));
        var texts = await _text.ExtractAllWordsAsync(pdf, ct);

        return await Task.Run(() =>
        {
            using var ms = new MemoryStream();
            using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation))
            {
                // The slide size follows the first page (PowerPoint has one size per presentation).
                var (w0, h0) = sizes.Count > 0 ? sizes[0] : (595.0, 842.0);
                var (cx, cy) = ((long)(w0 * Emu), (long)(h0 * Emu));
                var pres = doc.AddPresentationPart();
                pres.Presentation = new P.Presentation(
                    new P.SlideMasterIdList(), new P.SlideIdList(),
                    new P.SlideSize { Cx = (int)cx, Cy = (int)cy }, new P.NotesSize { Cx = 6858000, Cy = 9144000 },
                    new P.DefaultTextStyle());

                var master = pres.AddNewPart<SlideMasterPart>("rIdMaster");
                var layout = master.AddNewPart<SlideLayoutPart>("rIdLayout");
                layout.SlideLayout = new P.SlideLayout(new P.CommonSlideData(EmptyTree()), new P.ColorMapOverride(new A.MasterColorMapping()))
                    { Type = P.SlideLayoutValues.Blank };
                var theme = master.AddNewPart<ThemePart>("rIdTheme");
                theme.Theme = Theme();
                master.SlideMaster = new P.SlideMaster(
                    new P.CommonSlideData(EmptyTree()),
                    new P.ColorMap { Background1 = A.ColorSchemeIndexValues.Light1, Text1 = A.ColorSchemeIndexValues.Dark1, Background2 = A.ColorSchemeIndexValues.Light2, Text2 = A.ColorSchemeIndexValues.Dark2, Accent1 = A.ColorSchemeIndexValues.Accent1, Accent2 = A.ColorSchemeIndexValues.Accent2, Accent3 = A.ColorSchemeIndexValues.Accent3, Accent4 = A.ColorSchemeIndexValues.Accent4, Accent5 = A.ColorSchemeIndexValues.Accent5, Accent6 = A.ColorSchemeIndexValues.Accent6, Hyperlink = A.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink },
                    new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = "rIdLayout" }));
                pres.Presentation.SlideMasterIdList!.Append(new P.SlideMasterId { Id = 2147483648U, RelationshipId = "rIdMaster" });

                for (var i = 0; i < images.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var slidePart = pres.AddNewPart<SlidePart>($"rIdSlide{i + 1}");
                    slidePart.AddPart(layout);
                    var imagePart = slidePart.AddImagePart(ImagePartType.Png, "rIdImg");
                    using (var img = new MemoryStream(images[i])) imagePart.FeedData(img);

                    // Fit the page into the slide, centred (pages of another size than the first are letterboxed).
                    var (pw, ph) = i < sizes.Count ? sizes[i] : (w0, h0);
                    var scale = Math.Min(cx / (pw * Emu), cy / (ph * Emu));
                    var (iw, ih) = ((long)(pw * Emu * scale), (long)(ph * Emu * scale));
                    var alt = i < texts.Count ? string.Join(' ', texts[i].Select(t => t.Text)) : "";
                    if (alt.Length > 4000) alt = alt[..4000];

                    var picture = new P.Picture(
                        new P.NonVisualPictureProperties(
                            new P.NonVisualDrawingProperties { Id = 2U, Name = $"Pagina {i + 1}", Description = alt },
                            new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                            new P.ApplicationNonVisualDrawingProperties()),
                        new P.BlipFill(new A.Blip { Embed = "rIdImg" }, new A.Stretch(new A.FillRectangle())),
                        new P.ShapeProperties(
                            new A.Transform2D(new A.Offset { X = (cx - iw) / 2, Y = (cy - ih) / 2 }, new A.Extents { Cx = iw, Cy = ih }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
                    var tree = EmptyTree();
                    tree.Append(picture);
                    slidePart.Slide = new P.Slide(new P.CommonSlideData(tree), new P.ColorMapOverride(new A.MasterColorMapping()));
                    pres.Presentation.SlideIdList!.Append(new P.SlideId { Id = (uint)(256 + i), RelationshipId = $"rIdSlide{i + 1}" });
                }
            }
            return ms.ToArray();
        }, ct);
    }

    private static List<(double W, double H)> PageSizes(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(stream, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        return Enumerable.Range(0, doc.PageCount).Select(i => PageGeometry.VisualSize(doc.Pages[i])).ToList();
    }

    private static P.ShapeTree EmptyTree() => new(
        new P.NonVisualGroupShapeProperties(
            new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()),
        new P.GroupShapeProperties(new A.TransformGroup()));

    private static A.Theme Theme()
    {
        A.SchemeColor Dummy() => new() { Val = A.SchemeColorValues.PhColor };
        return new A.Theme(
            new A.ThemeElements(
                new A.ColorScheme(
                    new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
                    new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
                    new A.Dark2Color(new A.RgbColorModelHex { Val = "1F497D" }),
                    new A.Light2Color(new A.RgbColorModelHex { Val = "EEECE1" }),
                    new A.Accent1Color(new A.RgbColorModelHex { Val = "4F81BD" }),
                    new A.Accent2Color(new A.RgbColorModelHex { Val = "C0504D" }),
                    new A.Accent3Color(new A.RgbColorModelHex { Val = "9BBB59" }),
                    new A.Accent4Color(new A.RgbColorModelHex { Val = "8064A2" }),
                    new A.Accent5Color(new A.RgbColorModelHex { Val = "4BACC6" }),
                    new A.Accent6Color(new A.RgbColorModelHex { Val = "F79646" }),
                    new A.Hyperlink(new A.RgbColorModelHex { Val = "0000FF" }),
                    new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "800080" })) { Name = "Office" },
                new A.FontScheme(
                    new A.MajorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" }),
                    new A.MinorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" })) { Name = "Office" },
                new A.FormatScheme(
                    new A.FillStyleList(new A.SolidFill(Dummy()), new A.SolidFill(Dummy()), new A.SolidFill(Dummy())),
                    new A.LineStyleList(new A.Outline(new A.SolidFill(Dummy())), new A.Outline(new A.SolidFill(Dummy())), new A.Outline(new A.SolidFill(Dummy()))),
                    new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
                    new A.BackgroundFillStyleList(new A.SolidFill(Dummy()), new A.SolidFill(Dummy()), new A.SolidFill(Dummy()))) { Name = "Office" }),
            new A.ObjectDefaults(), new A.ExtraColorSchemeList()) { Name = "Office" };
    }
}
