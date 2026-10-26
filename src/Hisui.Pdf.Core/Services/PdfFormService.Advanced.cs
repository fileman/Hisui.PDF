using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Hisui.Pdf.Core.Model;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Hisui.Pdf.Core.Services;

/// <summary>Layout (batch move/delete, tab order), field properties, form data exchange, reset and vector flatten.</summary>
internal sealed partial class PdfFormService
{
    private const int ReadOnlyFlag = 1, RequiredFlag = 1 << 1, MultilineFlag = 1 << 12;

    // ── layout ───────────────────────────────────────────────────────────────

    public Task<byte[]> SetWidgetRectsAsync(byte[] pdf, int pageIndex, IReadOnlyDictionary<int, PdfRect> rects, CancellationToken ct = default) =>
        EditPageAsync(pdf, pageIndex, ct, (doc, page, annots) =>
        {
            foreach (var (index, rect) in rects)
            {
                var widget = WidgetAt(annots, index);
                widget.Elements.SetRectangle("/Rect", ToRaw(page, rect));
                if (InheritedName(widget, "/FT") is "/Tx" or "/Ch")
                    doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.SetBoolean("/NeedAppearances", true);
            }
        });

    public Task<byte[]> DeleteWidgetsAsync(byte[] pdf, int pageIndex, IReadOnlyCollection<int> annotIndices, CancellationToken ct = default) =>
        EditPageAsync(pdf, pageIndex, ct, (doc, _, annots) =>
        {
            var widgets = annotIndices.Distinct().Select(i => WidgetAt(annots, i)).ToList();
            foreach (var widget in widgets) RemoveWidget(doc, annots, widget);
        });

    public Task<byte[]> ReorderWidgetsAsync(byte[] pdf, int pageIndex, IReadOnlyList<int> widgetOrder, CancellationToken ct = default) =>
        EditPageAsync(pdf, pageIndex, ct, (doc, page, annots) =>
        {
            // Widgets take the /Annots slots widgets held before, in the new order; other annotations keep their place.
            var slots = Enumerable.Range(0, annots.Elements.Count)
                .Where(i => Resolve(annots.Elements[i]) is PdfDictionary d && d.Elements.GetName("/Subtype") == "/Widget").ToList();
            if (widgetOrder.Count != slots.Count || widgetOrder.Except(slots).Any())
                throw new ArgumentException("The order must list every widget of the page exactly once.", nameof(widgetOrder));
            var items = widgetOrder.Select(i => annots.Elements[i]).ToList();
            for (var k = 0; k < slots.Count; k++) annots.Elements[slots[k]] = items[k];
            page.Elements.SetName("/Tabs", "/S"); // viewers follow the array order
        });

    private static Task<byte[]> EditPageAsync(byte[] pdf, int pageIndex, CancellationToken ct, Action<PdfDocument, PdfPage, PdfArray> edit)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
            if ((uint)pageIndex >= (uint)doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            var page = doc.Pages[pageIndex];
            var annots = page.Elements.GetArray("/Annots") ?? throw new ArgumentOutOfRangeException(nameof(pageIndex), "The page has no annotations.");
            edit(doc, page, annots);
            return Save(doc);
        }, ct);
    }

    private static PdfDictionary WidgetAt(PdfArray annots, int index) =>
        (uint)index < (uint)annots.Elements.Count && Resolve(annots.Elements[index]) is PdfDictionary w && w.Elements.GetName("/Subtype") == "/Widget"
            ? w
            : throw new ArgumentOutOfRangeException(nameof(index), "No form widget at that index.");

    /// <summary>Removes a widget from its page and the field tree, climbing while a parent is left without kids.</summary>
    private static void RemoveWidget(PdfDocument doc, PdfArray annots, PdfDictionary widget)
    {
        for (var i = annots.Elements.Count - 1; i >= 0; i--)
            if (ReferenceEquals(Resolve(annots.Elements[i]), widget)) annots.Elements.RemoveAt(i);
        var fields = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
        for (PdfDictionary? node = widget; node is not null;)
        {
            var parent = Parent(node);
            var container = parent?.Elements.GetArray("/Kids") ?? fields;
            if (container is null) break;
            for (var i = container.Elements.Count - 1; i >= 0; i--)
                if (ReferenceEquals(Resolve(container.Elements[i]), node)) container.Elements.RemoveAt(i);
            node = parent is not null && container.Elements.Count == 0 ? parent : null;
        }
    }

    private static byte[] Save(PdfDocument doc)
    {
        using var output = new MemoryStream();
        doc.SaveStamped(output);
        return output.ToArray();
    }

    // ── properties ───────────────────────────────────────────────────────────

    public Task<FormFieldProperties> ReadPropertiesAsync(byte[] pdf, int pageIndex, int annotIndex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            var annots = doc.Pages[pageIndex].Elements.GetArray("/Annots") ?? throw new ArgumentOutOfRangeException(nameof(annotIndex));
            var widget = WidgetAt(annots, annotIndex);
            var field = Terminal(widget);
            var ff = InheritedInt(widget, "/Ff");
            var (font, size) = ParseDa((Inherited(widget, "/DA") as PdfString)?.Value ?? acroDa(doc));
            var js = ((Inherited(widget, "/AA") as PdfDictionary)?.Elements.GetDictionary("/F")?.Elements.GetString("/JS")) ?? "";
            var (format, decimals, pattern) = ParseFormat(js);
            return new FormFieldProperties
            {
                Name = FullName(field),
                Type = TypeOf(widget),
                Tooltip = field.Elements.GetString("/TU") is { Length: > 0 } tu ? tu : null,
                Required = (ff & RequiredFlag) != 0,
                ReadOnly = (ff & ReadOnlyFlag) != 0,
                Multiline = (ff & MultilineFlag) != 0,
                MaxLength = Inherited(widget, "/MaxLen") is PdfInteger { Value: > 0 } ml ? ml.Value : null,
                FontName = font,
                FontSize = size,
                Alignment = (FieldAlignment)Math.Clamp(InheritedInt(widget, "/Q"), 0, 2),
                Format = format,
                Decimals = decimals,
                DatePattern = pattern,
                Options = Inherited(widget, "/Opt") is PdfArray opt ? OptionValues(opt) : [],
            };
        }, ct);

        static string acroDa(PdfDocument doc) => doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetString("/DA") ?? "";
    }

    public Task<byte[]> SetPropertiesAsync(byte[] pdf, int pageIndex, int annotIndex, FormFieldProperties props, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(props);
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
            var annots = doc.Pages[pageIndex].Elements.GetArray("/Annots") ?? throw new ArgumentOutOfRangeException(nameof(annotIndex));
            var widget = WidgetAt(annots, annotIndex);
            var field = Terminal(widget);
            var acro = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!;

            // Rename: only the last name part is this field's own /T; the full name must stay unique.
            var newName = props.Name.Trim();
            var oldName = FullName(field);
            if (newName.Length == 0) throw new ArgumentException("The field name cannot be empty.", nameof(props));
            if (newName != oldName)
            {
                var prefix = oldName.Contains('.') ? oldName[..(oldName.LastIndexOf('.') + 1)] : "";
                if (!newName.StartsWith(prefix, StringComparison.Ordinal) || newName[prefix.Length..].Contains('.'))
                    newName = prefix + newName.Replace('.', '_');
                if (acro.Elements.GetArray("/Fields") is { } all && WalkFields(all, "").Any(f => f.Name == newName))
                    throw new ArgumentException($"A form field named '{newName}' already exists.", nameof(props));
                field.Elements.SetString("/T", newName[prefix.Length..]);
            }

            if (string.IsNullOrWhiteSpace(props.Tooltip)) field.Elements.Remove("/TU");
            else field.Elements.SetString("/TU", props.Tooltip.Trim());

            var ff = InheritedInt(widget, "/Ff");
            ff = Flag(ff, ReadOnlyFlag, props.ReadOnly);
            ff = Flag(ff, RequiredFlag, props.Required);
            var isText = InheritedName(widget, "/FT") == "/Tx";
            if (isText) ff = Flag(ff, MultilineFlag, props.Multiline);
            field.Elements.SetInteger("/Ff", ff);

            if (isText && props.MaxLength is > 0) field.Elements.SetInteger("/MaxLen", props.MaxLength.Value);
            else field.Elements.Remove("/MaxLen");

            if (InheritedName(widget, "/FT") is "/Tx" or "/Ch")
            {
                var font = props.FontName is "TiRo" or "Cour" ? props.FontName : "Helv";
                EnsureFont(doc, acro, font);
                var da = $"/{font} {Math.Max(0, props.FontSize).ToString("0.##", CultureInfo.InvariantCulture)} Tf 0 g";
                field.Elements.SetString("/DA", da);
                foreach (var w in WidgetsOf(field)) if (!ReferenceEquals(w, field)) w.Elements.Remove("/DA"); // inherit the field's
                field.Elements.SetInteger("/Q", (int)props.Alignment);
                acro.Elements.SetBoolean("/NeedAppearances", true);
            }

            if (isText) SetFormatActions(doc, field, props);

            if (InheritedName(widget, "/FT") == "/Ch")
            {
                var opt = new PdfArray(doc);
                foreach (var o in props.Options.Where(o => !string.IsNullOrWhiteSpace(o))) opt.Elements.Add(new PdfString(o.Trim()));
                field.Elements["/Opt"] = opt;
            }
            return Save(doc);
        }, ct);

        static int Flag(int value, int flag, bool on) => on ? value | flag : value & ~flag;
    }

    private static PdfFormFieldType TypeOf(PdfDictionary widget)
    {
        var ff = InheritedInt(widget, "/Ff");
        return InheritedName(widget, "/FT") switch
        {
            "/Tx" => PdfFormFieldType.Text,
            "/Btn" when (ff & PushButtonFlag) != 0 => PdfFormFieldType.Unknown,
            "/Btn" when (ff & RadioFlag) != 0 => PdfFormFieldType.RadioButton,
            "/Btn" => PdfFormFieldType.CheckBox,
            "/Ch" => (ff & ComboFlag) != 0 ? PdfFormFieldType.ComboBox : PdfFormFieldType.ListBox,
            "/Sig" => PdfFormFieldType.Signature,
            _ => PdfFormFieldType.Unknown,
        };
    }

    private static (string Font, double Size) ParseDa(string da)
    {
        var m = Regex.Match(da, @"/(\w+)\s+([\d.]+)\s+Tf");
        if (!m.Success) return ("Helv", 0);
        var font = m.Groups[1].Value is "TiRo" or "Cour" ? m.Groups[1].Value : "Helv";
        return (font, double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    // AcroForm JavaScript format functions (AF*); the separator style 2 is "1.234,56" (Italian/European).
    private static void SetFormatActions(PdfDocument doc, PdfDictionary field, FormFieldProperties p)
    {
        var aa = field.Elements.GetDictionary("/AA");
        var dec = Math.Clamp(p.Decimals, 0, 10);
        var pattern = p.DatePattern.Replace("\"", "");
        var (format, keystroke) = p.Format switch
        {
            FieldFormat.Number => ($"AFNumber_Format({dec}, 2, 0, 0, \"\", true);", $"AFNumber_Keystroke({dec}, 2, 0, 0, \"\", true);"),
            FieldFormat.Currency => ($"AFNumber_Format({dec}, 2, 0, 0, \"€ \", true);", $"AFNumber_Keystroke({dec}, 2, 0, 0, \"€ \", true);"),
            FieldFormat.Percent => ($"AFPercent_Format({dec}, 2);", $"AFPercent_Keystroke({dec}, 2);"),
            FieldFormat.Date => ($"AFDate_FormatEx(\"{pattern}\");", $"AFDate_KeystrokeEx(\"{pattern}\");"),
            _ => ((string?)null, (string?)null),
        };
        if (format is null)
        {
            aa?.Elements.Remove("/F");
            aa?.Elements.Remove("/K");
            if (aa is { Elements.Count: 0 }) field.Elements.Remove("/AA");
            return;
        }
        if (aa is null) { aa = new PdfDictionary(doc); field.Elements["/AA"] = aa; }
        aa.Elements["/F"] = Js(doc, format);
        aa.Elements["/K"] = Js(doc, keystroke!);

        static PdfDictionary Js(PdfDocument doc, string code)
        {
            var action = new PdfDictionary(doc);
            action.Elements.SetName("/S", "/JavaScript");
            action.Elements.SetString("/JS", code);
            return action;
        }
    }

    private static (FieldFormat Format, int Decimals, string Pattern) ParseFormat(string js)
    {
        if (Regex.Match(js, @"AFNumber_Format\(\s*(\d+)\s*,[^,]*,[^,]*,[^,]*,\s*""([^""]*)""") is { Success: true } n)
            return (n.Groups[2].Value.Trim().Length > 0 ? FieldFormat.Currency : FieldFormat.Number, int.Parse(n.Groups[1].Value), "dd/mm/yyyy");
        if (Regex.Match(js, @"AFPercent_Format\(\s*(\d+)") is { Success: true } pc)
            return (FieldFormat.Percent, int.Parse(pc.Groups[1].Value), "dd/mm/yyyy");
        if (Regex.Match(js, @"AFDate_Format(?:Ex)?\(\s*""([^""]+)""") is { Success: true } d)
            return (FieldFormat.Date, 2, d.Groups[1].Value);
        return (FieldFormat.None, 2, "dd/mm/yyyy");
    }

    /// <summary>Makes sure a standard font resource exists in the AcroForm /DR.</summary>
    private static void EnsureFont(PdfDocument doc, PdfDictionary acro, string resource)
    {
        if (resource == "Helv") { AcroFormRestorer.EnsureHelvetica(doc, acro); return; }
        var dr = acro.Elements.GetDictionary("/DR") ?? new PdfDictionary(doc);
        acro.Elements["/DR"] = dr;
        var fonts = dr.Elements.GetDictionary("/Font") ?? new PdfDictionary(doc);
        dr.Elements["/Font"] = fonts;
        if (fonts.Elements.ContainsKey("/" + resource)) return;
        var font = new PdfDictionary(doc);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", resource == "TiRo" ? "/Times-Roman" : "/Courier");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        doc.Internals.AddObject(font);
        fonts.Elements["/" + resource] = font.Reference;
    }

    // ── data exchange ────────────────────────────────────────────────────────

    /// <summary>Terminal fields (the ones holding a value) with their raw value: text, choice, or the button state name.</summary>
    private static List<(string Name, PdfDictionary Field, string Value)> TerminalValues(PdfDocument doc)
    {
        var result = new List<(string, PdfDictionary, string)>();
        if (doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is not { } fields) return result;
        foreach (var (name, field) in WalkFields(fields, ""))
        {
            if (field.Elements.GetArray("/Kids") is { } kids && kids.Elements.Select(Resolve).OfType<PdfDictionary>().Any(k => k.Elements.GetString("/T").Length > 0))
                continue; // a parent of named fields holds no value
            var ft = InheritedName(field, "/FT");
            if (ft is "" or "/Sig") continue;
            if (ft == "/Btn" && (InheritedInt(field, "/Ff") & PushButtonFlag) != 0) continue;
            var value = Inherited(field, "/V") switch
            {
                PdfString s => s.Value,
                PdfName n => n.Value[1..],
                PdfArray a when a.Elements.Count > 0 => (Resolve(a.Elements[0]) as PdfString)?.Value ?? "",
                _ => ft == "/Btn" ? "Off" : "",
            };
            result.Add((name, field, value));
        }
        return result;
    }

    public Task<byte[]> ExportDataAsync(byte[] pdf, FormDataFormat format, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            var values = TerminalValues(doc).Select(v => (v.Name, v.Value)).ToList();
            return format switch
            {
                FormDataFormat.Xfdf => ToXfdf(values),
                FormDataFormat.Fdf => ToFdf(values),
                _ => ToCsv(values),
            };
        }, ct);
    }

    public async Task<byte[]> ImportDataAsync(byte[] pdf, byte[] data, FormDataFormat format, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var values = format switch
        {
            FormDataFormat.Xfdf => FromXfdf(data),
            FormDataFormat.Fdf => FromFdf(data),
            _ => FromCsv(data),
        };
        return await FillFieldsAsync(pdf, values, ct);
    }

    private static byte[] ToXfdf(List<(string Name, string Value)> values)
    {
        XNamespace ns = "http://ns.adobe.com/xfdf/";
        var root = new XElement(ns + "fields");
        foreach (var (name, value) in values)
        {
            // Hierarchical names nest: "a.b" is <field name="a"><field name="b">.
            var parent = root;
            foreach (var part in name.Split('.'))
            {
                var next = parent.Elements(ns + "field").FirstOrDefault(e => (string?)e.Attribute("name") == part);
                if (next is null) parent.Add(next = new XElement(ns + "field", new XAttribute("name", part)));
                parent = next;
            }
            parent.Add(new XElement(ns + "value", value));
        }
        var xdoc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "xfdf", new XAttribute(XNamespace.Xml + "space", "preserve"), root));
        using var ms = new MemoryStream();
        xdoc.Save(ms);
        return ms.ToArray();
    }

    private static Dictionary<string, string> FromXfdf(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // The file comes from the user's disk: no DTD, no external entities (XXE), whatever the reader defaults are.
        using var reader = System.Xml.XmlReader.Create(new MemoryStream(data),
            new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
        var xdoc = XDocument.Load(reader);
        void Walk(XElement element, string prefix)
        {
            foreach (var f in element.Elements().Where(e => e.Name.LocalName == "field"))
            {
                var name = prefix.Length == 0 ? (string?)f.Attribute("name") ?? "" : prefix + "." + (string?)f.Attribute("name");
                if (f.Elements().FirstOrDefault(e => e.Name.LocalName == "value") is { } v) result[name] = v.Value;
                Walk(f, name);
            }
        }
        foreach (var fields in xdoc.Descendants().Where(e => e.Name.LocalName == "fields")) Walk(fields, "");
        return result;
    }

    // ponytail: FDF uses full names in /T (flat); FDF files with hierarchical /Kids import only their leaves' own names.
    private static byte[] ToFdf(List<(string Name, string Value)> values)
    {
        var sb = new StringBuilder("%FDF-1.2\n%\u00e2\u00e3\u00cf\u00d3\n1 0 obj\n<< /FDF << /Fields [\n");
        foreach (var (name, value) in values)
            sb.Append("<< /T ").Append(PdfText(name)).Append(" /V ").Append(PdfText(value)).Append(" >>\n");
        sb.Append("] >> >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());

        static string PdfText(string s) => s.All(c => c < 128)
            ? "(" + s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", "\\r").Replace("\n", "\\n") + ")"
            : "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)) + ">";
    }

    private static Dictionary<string, string> FromFdf(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        const string str = @"\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]*>|/[^\s/<>\[\]()]+";
        foreach (Match m in Regex.Matches(text, @"/T\s*(" + str + @")\s*/V\s*(" + str + @")|/V\s*(" + str + @")\s*/T\s*(" + str + ")"))
        {
            var (t, v) = m.Groups[1].Success ? (m.Groups[1].Value, m.Groups[2].Value) : (m.Groups[4].Value, m.Groups[3].Value);
            result[Decode(t)] = Decode(v);
        }
        return result;

        static string Decode(string token)
        {
            if (token.StartsWith('/')) return token[1..];
            if (token.StartsWith('<'))
            {
                var bytes = Convert.FromHexString(Regex.Replace(token[1..^1], @"\s", ""));
                return bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
                    ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
                    : Encoding.Latin1.GetString(bytes);
            }
            var body = token[1..^1];
            var sb = new StringBuilder();
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] != '\\' || i + 1 >= body.Length) { sb.Append(body[i]); continue; }
                var c = body[++i];
                sb.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => c });
            }
            var raw = sb.ToString();
            return raw.StartsWith("\u00fe\u00ff") ? Encoding.BigEndianUnicode.GetString(Encoding.Latin1.GetBytes(raw[2..])) : raw;
        }
    }

    // CSV: header row of field names, one row of values; ';' separated (Excel, Italian locale) with a UTF-8 BOM.
    private static byte[] ToCsv(List<(string Name, string Value)> values)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var text = string.Join(';', values.Select(v => Q(v.Name))) + "\r\n" + string.Join(';', values.Select(v => Q(v.Value))) + "\r\n";
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
    }

    private static Dictionary<string, string> FromCsv(byte[] data)
    {
        var rows = ParseCsv(new UTF8Encoding(false).GetString(data).TrimStart('\uFEFF'));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rows.Count < 2) return result;
        for (var i = 0; i < rows[0].Count && i < rows[1].Count; i++)
            if (rows[0][i].Length > 0) result[rows[0][i]] = rows[1][i];
        return result;
    }

    /// <summary>RFC 4180-style parse; the separator (';', ',' or tab) is whichever appears first outside quotes.</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var sep = text.FirstOrDefault(c => c is ';' or ',' or '\t');
        if (sep == default) sep = ';';
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row); row = [];
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    // ── reset / flatten ──────────────────────────────────────────────────────

    public Task<byte[]> ResetFieldsAsync(byte[] pdf, IReadOnlyCollection<string>? names = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
            var acro = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            if (acro is null) return pdf;
            foreach (var (name, field, _) in TerminalValues(doc))
            {
                if (names is not null && !names.Contains(name)) continue;
                var dv = Inherited(field, "/DV");
                if (dv is null) field.Elements.Remove("/V");
                else field.Elements["/V"] = dv.Clone();
                if (InheritedName(field, "/FT") == "/Btn")
                {
                    var state = (dv as PdfName)?.Value ?? "/Off";
                    foreach (var w in WidgetsOf(field)) w.Elements.SetName("/AS", OnState(w) == state.TrimStart('/') ? state : "/Off");
                }
            }
            acro.Elements.SetBoolean("/NeedAppearances", true);
            return Save(doc);
        }, ct);
    }

    /// <summary>
    /// Burns the current values of the chosen fields (all when <paramref name="names"/> is null) into the page content as
    /// vector text/marks and removes those fields. Pages stay text-searchable, unlike <see cref="FlattenAsync"/>.
    /// ponytail: values are drawn in Arial at the field's size (auto: fit the box); embedded custom appearances are not reproduced.
    /// </summary>
    public Task<byte[]> FlattenFieldsAsync(byte[] pdf, IReadOnlyCollection<string>? names = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        GlobalFontSettings.FontResolver ??= new BundledFontResolver();
        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
            for (var p = 0; p < doc.PageCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.Pages[p];
                if (page.Elements.GetArray("/Annots") is not { } annots) continue;
                var targets = annots.Elements.Select(Resolve).OfType<PdfDictionary>()
                    .Where(w => w.Elements.GetName("/Subtype") == "/Widget" && TypeOf(w) is not (PdfFormFieldType.Unknown or PdfFormFieldType.Signature)
                                && (names is null || names.Contains(FullName(Terminal(w)))))
                    .ToList();
                if (targets.Count == 0) continue;

                var (vw, vh) = PageGeometry.VisualSize(page);
                using (var gfx = PageGeometry.OpenGraphics(page))
                    foreach (var w in targets)
                    {
                        var r = ToVisual(page, w.Elements.GetRectangle("/Rect"));
                        var box = new XRect(r.Left * vw, r.Top * vh, r.Width * vw, r.Height * vh);
                        DrawValue(gfx, w, box);
                    }
                foreach (var w in targets) RemoveWidget(doc, annots, w);
            }
            return Save(doc);
        }, ct);
    }

    private static void DrawValue(XGraphics gfx, PdfDictionary widget, XRect box)
    {
        switch (TypeOf(widget))
        {
            case PdfFormFieldType.CheckBox or PdfFormFieldType.RadioButton:
                if (widget.Elements.GetName("/AS") is not { Length: > 0 } a || a == "/Off") return;
                var pen = new XPen(XColors.Black, Math.Max(0.8, Math.Min(box.Width, box.Height) / 8));
                if (TypeOf(widget) == PdfFormFieldType.RadioButton)
                {
                    var d = Math.Min(box.Width, box.Height) * 0.45;
                    gfx.DrawEllipse(XBrushes.Black, box.X + (box.Width - d) / 2, box.Y + (box.Height - d) / 2, d, d);
                }
                else
                    gfx.DrawLines(pen, [new XPoint(box.X + box.Width * 0.2, box.Y + box.Height * 0.5),
                        new XPoint(box.X + box.Width * 0.42, box.Y + box.Height * 0.75), new XPoint(box.X + box.Width * 0.8, box.Y + box.Height * 0.22)]);
                return;
            default:
                var value = Inherited(Terminal(widget), "/V") switch
                {
                    PdfString s => s.Value,
                    PdfName n => n.Value[1..],
                    PdfArray arr when arr.Elements.Count > 0 => (Resolve(arr.Elements[0]) as PdfString)?.Value ?? "",
                    _ => "",
                };
                var (fmt, dec, _) = ParseFormat(((Inherited(widget, "/AA") as PdfDictionary)?.Elements.GetDictionary("/F")?.Elements.GetString("/JS")) ?? "");
                value = FieldValueFormatter.ToDisplay(fmt, dec, value);
                if (value.Length == 0) return;
                var (_, size) = ParseDa((Inherited(widget, "/DA") as PdfString)?.Value ?? "");
                var multiline = (InheritedInt(widget, "/Ff") & MultilineFlag) != 0;
                var inner = new XRect(box.X + 2, box.Y + 1, Math.Max(1, box.Width - 4), Math.Max(1, box.Height - 2));
                if (size <= 0) size = multiline ? 10 : Math.Clamp(inner.Height * 0.75, 4, 12);
                var font = new XFont("Arial", size);
                if (!multiline)
                    while (size > 4 && gfx.MeasureString(value, font).Width > inner.Width) font = new XFont("Arial", size -= 0.5);
                var q = (FieldAlignment)Math.Clamp(InheritedInt(widget, "/Q"), 0, 2);
                if (multiline)
                    new XTextFormatter(gfx) { Alignment = q switch { FieldAlignment.Center => XParagraphAlignment.Center, FieldAlignment.Right => XParagraphAlignment.Right, _ => XParagraphAlignment.Left } }
                        .DrawString(value, font, XBrushes.Black, inner, XStringFormats.TopLeft);
                else
                    gfx.DrawString(value, font, XBrushes.Black, inner, q switch
                    {
                        FieldAlignment.Center => XStringFormats.Center,
                        FieldAlignment.Right => XStringFormats.CenterRight,
                        _ => XStringFormats.CenterLeft,
                    });
                return;
        }
    }
}
