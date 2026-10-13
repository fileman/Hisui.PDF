using System.Globalization;
using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Advanced;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// AcroForm read/fill backed by PDFsharp; flatten is implemented as rasterisation so it works
/// regardless of whether the form has pre-built appearance streams.
/// </summary>
internal sealed partial class PdfFormService : IPdfFormService
{
    private readonly IPdfRenderer _renderer;

    public PdfFormService(IPdfRenderer renderer) => _renderer = renderer;

    public Task<IReadOnlyList<PdfFormField>> ReadFieldsAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<PdfFormField>>(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

            // doc.AcroForm throws InvalidOperationException when the PDF has no /AcroForm.
            PdfAcroForm form;
            try { form = doc.AcroForm; }
            catch (InvalidOperationException) { return []; }

            if (form.Fields.Count == 0) return [];

            var result = new List<PdfFormField>(form.Fields.Count);
            for (var i = 0; i < form.Fields.Count; i++)
                result.Add(MapField(form.Fields[i]));
            return result;
        }, ct);
    }

    public Task<byte[]> FillFieldsAsync(byte[] pdf, IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0) return Task.FromResult(pdf);

        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);

            var acro = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            if (acro?.Elements.GetArray("/Fields") is not { } fields) return pdf;

            var changed = false;
            foreach (var (name, field) in WalkFields(fields, ""))
            {
                if (!values.TryGetValue(name, out var value)) continue;
                var widgets = WidgetsOf(field);
                switch (InheritedName(field, "/FT"))
                {
                    case "/Btn" when (InheritedInt(field, "/Ff") & RadioFlag) != 0:
                        var choice = SafeName(value, "Off");
                        field.Elements.SetName("/V", "/" + choice);
                        foreach (var w in widgets) w.Elements.SetName("/AS", OnState(w) == choice ? "/" + choice : "/Off");
                        break;
                    case "/Btn":
                        var on = IsCheckedValue(value) || widgets.Any(w => OnState(w) == value);
                        var state = on ? widgets.Select(OnState).FirstOrDefault(st => st is not null) ?? "Yes" : "Off";
                        field.Elements.SetName("/V", "/" + state);
                        foreach (var w in widgets) w.Elements.SetName("/AS", on ? "/" + (OnState(w) ?? state) : "/Off");
                        break;
                    default: // text and choice fields; viewers regenerate the appearance (/NeedAppearances)
                        field.Elements.SetString("/V", value);
                        break;
                }
                changed = true;
            }
            if (!changed) return pdf;
            acro.Elements.SetBoolean("/NeedAppearances", true);

            using var output = new MemoryStream();
            doc.SaveStamped(output);
            return output.ToArray();
        }, ct);
    }

    public Task<IReadOnlyList<PdfFormWidget>> ReadWidgetsAsync(byte[] pdf, int pageIndex, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run<IReadOnlyList<PdfFormWidget>>(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
            if ((uint)pageIndex >= (uint)doc.PageCount) return [];
            var page = doc.Pages[pageIndex];
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) return [];

            var result = new List<PdfFormWidget>();
            for (var i = 0; i < annots.Elements.Count; i++)
            {
                if (Resolve(annots.Elements[i]) is not PdfDictionary w || w.Elements.GetName("/Subtype") != "/Widget") continue;
                var ff = InheritedInt(w, "/Ff");
                var type = InheritedName(w, "/FT") switch
                {
                    "/Tx" => PdfFormFieldType.Text,
                    "/Btn" when (ff & PushButtonFlag) != 0 => PdfFormFieldType.Unknown,
                    "/Btn" when (ff & RadioFlag) != 0 => PdfFormFieldType.RadioButton,
                    "/Btn" => PdfFormFieldType.CheckBox,
                    "/Ch" => (ff & ComboFlag) != 0 ? PdfFormFieldType.ComboBox : PdfFormFieldType.ListBox,
                    "/Sig" => PdfFormFieldType.Signature,
                    _ => PdfFormFieldType.Unknown,
                };
                if (type == PdfFormFieldType.Unknown) continue;

                var field = Terminal(w);
                var v = Inherited(w, "/V");
                var (format, decimals, pattern) = ParseFormat(
                    ((Inherited(w, "/AA") as PdfDictionary)?.Elements.GetDictionary("/F")?.Elements.GetString("/JS")) ?? "");
                string? value = type switch
                {
                    PdfFormFieldType.CheckBox => (w.Elements.GetName("/AS") is { Length: > 0 } a && a != "/Off") ? "true" : "false",
                    PdfFormFieldType.RadioButton => v is PdfName n && n.Value != "/Off" ? n.Value[1..] : null,
                    _ => v switch
                    {
                        PdfString str => str.Value,
                        PdfName n => n.Value[1..],
                        PdfArray arr when arr.Elements.Count > 0 => (Resolve(arr.Elements[0]) as PdfString)?.Value,
                        _ => null,
                    },
                };

                result.Add(new PdfFormWidget
                {
                    Name = FullName(field),
                    Type = type,
                    PageIndex = pageIndex,
                    AnnotIndex = i,
                    Rect = ToVisual(page, w.Elements.GetRectangle("/Rect")),
                    Value = value,
                    OnState = OnState(w),
                    Options = Inherited(w, "/Opt") is PdfArray opt ? OptionValues(opt) : [],
                    IsReadOnly = (ff & 1) != 0,
                    Tooltip = field.Elements.GetString("/TU") is { Length: > 0 } tu ? tu : null,
                    IsRequired = (ff & RequiredFlag) != 0,
                    IsMultiline = type == PdfFormFieldType.Text && (ff & MultilineFlag) != 0,
                    MaxLength = Inherited(w, "/MaxLen") is PdfInteger { Value: > 0 } ml ? ml.Value : null,
                    Alignment = (FieldAlignment)Math.Clamp(InheritedInt(w, "/Q"), 0, 2),
                    Format = format,
                    Decimals = decimals,
                    DatePattern = pattern,
                });
            }
            return result;
        }, ct);
    }

    public Task<byte[]> SetWidgetRectAsync(byte[] pdf, int pageIndex, int annotIndex, PdfRect rect, CancellationToken ct = default) =>
        SetWidgetRectsAsync(pdf, pageIndex, new Dictionary<int, PdfRect> { [annotIndex] = rect }, ct);

    public Task<byte[]> DeleteWidgetAsync(byte[] pdf, int pageIndex, int annotIndex, CancellationToken ct = default) =>
        DeleteWidgetsAsync(pdf, pageIndex, [annotIndex], ct);

    public Task<byte[]> AddFieldAsync(byte[] pdf, int pageIndex, PdfRect rect, PdfFormFieldType type,
        string? name = null, IReadOnlyList<string>? options = null, CancellationToken ct = default) =>
        AddFieldsAsync(pdf,
            [new FormFieldSpec { PageIndex = pageIndex, Rect = rect, Type = type, Name = name, Options = options }], ct);

    public Task<byte[]> AddFieldsAsync(byte[] pdf, IReadOnlyList<FormFieldSpec> specs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(specs);
        foreach (var spec in specs)
            if (spec.Type is PdfFormFieldType.Unknown)
                throw new ArgumentOutOfRangeException(nameof(specs), spec.Type, "Unknown is not a creatable field type.");
        if (specs.Count == 0) return Task.FromResult(pdf);

        return Task.Run(() =>
        {
            using var stream = new MemoryStream(pdf, writable: false);
            using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);

            // Existing /AcroForm (or a new one) and the field names already taken.
            var acro = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            if (acro is null)
            {
                acro = new PdfDictionary(doc);
                doc.Internals.Catalog.Elements["/AcroForm"] = acro;
            }
            var fields = acro.Elements.GetArray("/Fields");
            if (fields is null)
            {
                fields = new PdfArray(doc);
                acro.Elements["/Fields"] = fields;
            }
            var taken = new Dictionary<string, PdfDictionary>(StringComparer.Ordinal);
            for (var i = 0; i < fields.Elements.Count; i++)
                if (Resolve(fields.Elements[i]) is PdfDictionary f && f.Elements.GetString("/T") is { Length: > 0 } t)
                    taken[t] = f;

            foreach (var spec in specs)
            {
                ct.ThrowIfCancellationRequested();
                AddOne(doc, fields, taken, spec);
            }

            // Tab order: widgets are appended to /Annots in spec order; /Tabs /S makes viewers follow that array
            // instead of re-sorting by row (a tagged document keeps its own structure order).
            if (!doc.Internals.Catalog.Elements.ContainsKey("/StructTreeRoot"))
                foreach (var pageIndex in specs.Select(sp => sp.PageIndex).Distinct())
                    doc.Pages[pageIndex].Elements.SetName("/Tabs", "/S");

            // /Helv must exist in /DR for the /DA strings the fields use.
            AcroFormRestorer.EnsureHelvetica(doc, acro);
            acro.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
            if (specs.Any(sp => sp.Type is PdfFormFieldType.Text or PdfFormFieldType.ComboBox or PdfFormFieldType.ListBox))
                acro.Elements.SetBoolean("/NeedAppearances", true);

            using var output = new MemoryStream();
            doc.SaveStamped(output);
            return output.ToArray();
        }, ct);
    }

    private static void AddOne(PdfDocument doc, PdfArray fields, Dictionary<string, PdfDictionary> taken, FormFieldSpec spec)
    {
        var (pageIndex, rect, type, name, options) = (spec.PageIndex, spec.Rect, spec.Type, spec.Name, spec.Options);
        if ((uint)pageIndex >= (uint)doc.PageCount)
            throw new ArgumentOutOfRangeException(nameof(spec), $"Page {pageIndex} does not exist.");
        var page = doc.Pages[pageIndex];

        // A radio button joins the group of the same name; any other duplicate is an error.
        const int RadioFlags = RadioFlag | (1 << 14); // Radio + NoToggleToOff
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        PdfDictionary? group = null;
        if (name is null)
        {
            var prefix = type switch
            {
                PdfFormFieldType.CheckBox => "CheckBox", PdfFormFieldType.ComboBox => "ComboBox",
                PdfFormFieldType.RadioButton => "Radio", PdfFormFieldType.ListBox => "List",
                PdfFormFieldType.Signature => "Signature", _ => "Text",
            };
            var n = 1;
            while (taken.ContainsKey($"{prefix}{n}")) n++;
            name = $"{prefix}{n}";
        }
        else if (taken.TryGetValue(name, out var existing))
        {
            if (type == PdfFormFieldType.RadioButton && (existing.Elements.GetInteger("/Ff") & (1 << 15)) != 0)
                group = existing;
            else
                throw new ArgumentException($"A form field named '{name}' already exists.", nameof(name));
        }

        var rotation = PageGeometry.Rotation(page);
        var (vw, vh) = PageGeometry.VisualSize(page);
        var boxW = rect.Width * vw;
        var boxH = rect.Height * vh;
        var rawRect = ToRaw(page, rect);

        var widget = new PdfDictionary(doc);
        doc.Internals.AddObject(widget); // indirect before any array references it
        widget.Elements.SetName("/Type", "/Annot");
        widget.Elements.SetName("/Subtype", "/Widget");
        widget.Elements.SetInteger("/F", 4); // Print
        widget.Elements.SetRectangle("/Rect", rawRect);
        widget.Elements["/P"] = page.Reference;
        var mk = new PdfDictionary(doc);
        if (rotation != 0) mk.Elements.SetInteger("/R", rotation);
        mk.Elements["/BC"] = new PdfArray(doc, new PdfReal(0.2), new PdfReal(0.2), new PdfReal(0.2));
        mk.Elements["/BG"] = new PdfArray(doc, new PdfReal(0.93), new PdfReal(0.95), new PdfReal(1));
        widget.Elements["/MK"] = mk;

        // Field-level entries live on the widget itself, except for radio buttons (group = parent field).
        var fieldDict = widget;
        switch (type)
        {
            case PdfFormFieldType.RadioButton:
                if (group is null)
                {
                    group = new PdfDictionary(doc);
                    group.Elements.SetName("/FT", "/Btn");
                    group.Elements.SetString("/T", name);
                    group.Elements.SetInteger("/Ff", RadioFlags);
                    group.Elements.SetName("/V", "/Off");
                    group.Elements["/Kids"] = new PdfArray(doc);
                    doc.Internals.AddObject(group);
                    fields.Elements.Add(group);
                }
                var kids = group.Elements.GetArray("/Kids")!;
                var export = SafeName(options?.FirstOrDefault(), $"Choice{kids.Elements.Count + 1}");
                for (var i = 0; i < kids.Elements.Count; i++)
                    if (Resolve(kids.Elements[i]) is PdfDictionary k &&
                        k.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N")?.Elements.ContainsKey("/" + export) == true)
                        throw new ArgumentException($"Radio group '{name}' already has the choice '{export}'.", nameof(options));

                widget.Elements["/Parent"] = group.Reference;
                widget.Elements.SetName("/AS", "/Off");
                widget.Elements["/AP"] = Ap(doc, ("/" + export, Appearance(doc, boxW, boxH, rotation, on: true, round: true)),
                                                  ("/Off", Appearance(doc, boxW, boxH, rotation, on: false, round: true)));
                kids.Elements.Add(widget);
                fieldDict = group;
                break;

            case PdfFormFieldType.CheckBox:
                widget.Elements.SetName("/FT", "/Btn");
                widget.Elements.SetName("/V", "/Off");
                widget.Elements.SetName("/AS", "/Off");
                widget.Elements["/AP"] = Ap(doc, ("/Yes", Appearance(doc, boxW, boxH, rotation, on: true)),
                                                 ("/Off", Appearance(doc, boxW, boxH, rotation, on: false)));
                break;

            case PdfFormFieldType.ComboBox or PdfFormFieldType.ListBox:
                widget.Elements.SetName("/FT", "/Ch");
                if (type == PdfFormFieldType.ComboBox) widget.Elements.SetInteger("/Ff", 1 << 17); // Combo
                var opt = new PdfArray(doc);
                foreach (var o in options ?? [])
                    if (!string.IsNullOrWhiteSpace(o)) opt.Elements.Add(new PdfString(o.Trim()));
                widget.Elements["/Opt"] = opt;
                widget.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
                widget.Elements["/AP"] = Ap(doc, ("", Appearance(doc, boxW, boxH, rotation)));
                break;

            case PdfFormFieldType.Signature:
                widget.Elements.SetName("/FT", "/Sig");
                widget.Elements["/AP"] = Ap(doc, ("", Appearance(doc, boxW, boxH, rotation)));
                break;

            default: // Text
                widget.Elements.SetName("/FT", "/Tx");
                widget.Elements.SetString("/DA", "/Helv 0 Tf 0 g"); // font size 0 = auto
                widget.Elements["/AP"] = Ap(doc, ("", Appearance(doc, boxW, boxH, rotation)));
                break;
        }
        if (fieldDict == widget) widget.Elements.SetString("/T", name);

        var annots = page.Elements.GetArray("/Annots");
        if (annots is null)
        {
            annots = new PdfArray(doc);
            page.Elements["/Annots"] = annots;
        }
        annots.Elements.Add(widget);
        if (fieldDict == widget) fields.Elements.Add(widget);
        taken[name] = fieldDict;
        if (!string.IsNullOrWhiteSpace(spec.Tooltip)) fieldDict.Elements.SetString("/TU", spec.Tooltip);
    }

    /// <summary>PDF name-safe export value: letters/digits/underscore only.</summary>
    private static string SafeName(string? value, string fallback)
    {
        var s = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray());
        return s.Length == 0 ? fallback : s;
    }

    /// <summary>/AP dictionary. An empty key means a single normal stream; otherwise a state → stream map.</summary>
    private static PdfDictionary Ap(PdfDocument doc, params (string State, PdfReference Stream)[] states)
    {
        var ap = new PdfDictionary(doc);
        if (states is [{ State: "" } single])
        {
            ap.Elements["/N"] = single.Stream;
            return ap;
        }
        var normal = new PdfDictionary(doc);
        foreach (var (state, stream) in states) normal.Elements[state] = stream;
        ap.Elements["/N"] = normal;
        return ap;
    }

    /// <summary>
    /// Appearance form XObject drawn in rendered orientation (<paramref name="w"/> x <paramref name="h"/>);
    /// /Matrix rotates it into the widget's raw space for pages with /Rotate.
    /// </summary>
    private static PdfReference Appearance(PdfDocument doc, double w, double h, int rotation,
        bool on = false, bool round = false)
    {
        var f = CultureInfo.InvariantCulture;
        string N(double v) => v.ToString("0.###", f);

        string content;
        if (round)
        {
            var rx = w / 2; var ry = h / 2;
            content = $"0.93 0.95 1 rg 0.2 G {Ellipse(rx, ry, rx - 0.5, ry - 0.5, N)} B";
            if (on) content += $" 0 g {Ellipse(rx, ry, rx * 0.45, ry * 0.45, N)} f";
        }
        else
        {
            content = $"0.93 0.95 1 rg 0 0 {N(w)} {N(h)} re f 0.2 G 0.5 0.5 {N(w - 1)} {N(h - 1)} re S";
            if (on)
                content += $" 0 G {N(Math.Min(w, h) / 8)} w {N(w * 0.2)} {N(h * 0.5)} m {N(w * 0.42)} {N(h * 0.25)} l {N(w * 0.8)} {N(h * 0.78)} l S";
        }

        var xobj = new PdfDictionary(doc);
        xobj.Elements.SetName("/Type", "/XObject");
        xobj.Elements.SetName("/Subtype", "/Form");
        xobj.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(w), new PdfReal(h));
        if (rotation != 0)
        {
            // Rotate the rendered-orientation drawing back by the page rotation (counter-clockwise).
            var (a, b, c, d) = rotation switch { 90 => (0, -1, 1, 0), 180 => (-1, 0, 0, -1), _ => (0, 1, -1, 0) };
            xobj.Elements["/Matrix"] = new PdfArray(doc, new PdfReal(a), new PdfReal(b), new PdfReal(c), new PdfReal(d),
                new PdfReal(0), new PdfReal(0));
        }
        xobj.CreateStream(System.Text.Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(xobj);
        return xobj.Reference!;
    }

    private static string Ellipse(double cx, double cy, double rx, double ry, Func<double, string> n)
    {
        const double k = 0.5523;
        return $"{n(cx + rx)} {n(cy)} m " +
               $"{n(cx + rx)} {n(cy + ry * k)} {n(cx + rx * k)} {n(cy + ry)} {n(cx)} {n(cy + ry)} c " +
               $"{n(cx - rx * k)} {n(cy + ry)} {n(cx - rx)} {n(cy + ry * k)} {n(cx - rx)} {n(cy)} c " +
               $"{n(cx - rx)} {n(cy - ry * k)} {n(cx - rx * k)} {n(cy - ry)} {n(cx)} {n(cy - ry)} c " +
               $"{n(cx + rx * k)} {n(cy - ry)} {n(cx + rx)} {n(cy - ry * k)} {n(cx + rx)} {n(cy)} c h";
    }

    // ── field tree helpers ───────────────────────────────────────────────────

    private const int RadioFlag = 1 << 15, PushButtonFlag = 1 << 16, ComboFlag = 1 << 17;

    private static PdfRectangle ToRaw(PdfPage page, PdfRect rect) => PageGeometry.ToRaw(page, rect);
    private static PdfRect ToVisual(PdfPage page, PdfRectangle raw) => PageGeometry.ToVisual(page, raw);

    /// <summary>Every named field below <paramref name="fields"/>, with its fully qualified name.</summary>
    private static IEnumerable<(string Name, PdfDictionary Field)> WalkFields(PdfArray fields, string prefix)
    {
        for (var i = 0; i < fields.Elements.Count; i++)
        {
            if (Resolve(fields.Elements[i]) is not PdfDictionary f || f.Elements.GetString("/T") is not { Length: > 0 } t) continue;
            var name = prefix.Length == 0 ? t : prefix + "." + t;
            yield return (name, f);
            if (f.Elements.GetArray("/Kids") is { } kids)
                foreach (var child in WalkFields(kids, name)) yield return child;
        }
    }

    /// <summary>The widgets of a field: itself when merged, else its unnamed kids.</summary>
    private static List<PdfDictionary> WidgetsOf(PdfDictionary field)
    {
        if (field.Elements.GetName("/Subtype") == "/Widget") return [field];
        var kids = field.Elements.GetArray("/Kids");
        if (kids is null) return [];
        return kids.Elements.Select(Resolve).OfType<PdfDictionary>()
            .Where(k => k.Elements.GetName("/Subtype") == "/Widget" && k.Elements.GetString("/T").Length == 0).ToList();
    }

    /// <summary>The field a widget belongs to: the nearest dictionary (itself or a parent) with a /T.</summary>
    private static PdfDictionary Terminal(PdfDictionary widget)
    {
        for (PdfDictionary? d = widget; d is not null; d = Parent(d))
            if (d.Elements.GetString("/T").Length > 0) return d;
        return widget;
    }

    private static string FullName(PdfDictionary field)
    {
        var parts = new List<string>();
        for (PdfDictionary? d = field; d is not null; d = Parent(d))
            if (d.Elements.GetString("/T") is { Length: > 0 } t) parts.Add(t);
        parts.Reverse();
        return string.Join('.', parts);
    }

    private static PdfDictionary? Parent(PdfDictionary d) => Resolve(d.Elements["/Parent"]) as PdfDictionary;

    private static PdfItem? Inherited(PdfDictionary d, string key)
    {
        for (PdfDictionary? x = d; x is not null; x = Parent(x))
            if (x.Elements.ContainsKey(key)) return Resolve(x.Elements[key]);
        return null;
    }

    private static string InheritedName(PdfDictionary d, string key) => (Inherited(d, key) as PdfName)?.Value ?? "";

    private static int InheritedInt(PdfDictionary d, string key) => Inherited(d, key) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => (int)r.Value,
        _ => 0,
    };

    /// <summary>The widget's "on" appearance state (its export value), without the leading slash.</summary>
    private static string? OnState(PdfDictionary widget) =>
        (Resolve(widget.Elements.GetDictionary("/AP")?.Elements["/N"]) as PdfDictionary)?.Elements.Keys
            .FirstOrDefault(k => k != "/Off")?[1..];

    private static IReadOnlyList<string> OptionValues(PdfArray optArray) =>
        optArray.Elements.Select(Resolve).Select(item => item switch
        {
            PdfString s => s.Value,
            PdfName n => n.Value,
            PdfArray a when a.Elements.Count >= 2 => (Resolve(a.Elements[1]) as PdfString)?.Value ?? "",
            _ => item?.ToString() ?? "",
        }).ToList();

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    public async Task<byte[]> FlattenAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        var pageCount = await _renderer.GetPageCountAsync(pdf, ct);
        var pageImages = new List<byte[]>(pageCount);

        for (var i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            pageImages.Add(await _renderer.RenderPageToPngAsync(pdf, i, 300, ct));
        }

        return await Task.Run(() =>
        {
            using var srcStream = new MemoryStream(pdf, writable: false);
            using var srcDoc = PdfReader.Open(srcStream, PdfDocumentOpenMode.Import);
            using var outDoc = new PdfDocument();

            for (var i = 0; i < srcDoc.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var srcPage = srcDoc.Pages[i];
                var newPage = outDoc.AddPage();
                newPage.Width = srcPage.Width;
                newPage.Height = srcPage.Height;

                using var gfx = XGraphics.FromPdfPage(newPage);
                using var imgStream = new MemoryStream(pageImages[i]);
                var xImg = XImage.FromStream(imgStream);
                try { gfx.DrawImage(xImg, new XRect(0, 0, newPage.Width.Point, newPage.Height.Point)); }
                finally { xImg.Dispose(); }
            }

            using var ms = new MemoryStream();
            outDoc.SaveStamped(ms);
            return ms.ToArray();
        }, ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static PdfFormField MapField(PdfAcroField field)
    {
        var type = field switch
        {
            PdfTextField        => PdfFormFieldType.Text,
            PdfCheckBoxField    => PdfFormFieldType.CheckBox,
            PdfRadioButtonField => PdfFormFieldType.RadioButton,
            PdfComboBoxField    => PdfFormFieldType.ComboBox,
            PdfListBoxField     => PdfFormFieldType.ListBox,
            _                   => PdfFormFieldType.Unknown,
        };

        string? value = field switch
        {
            PdfTextField tf     => tf.Text,
            PdfCheckBoxField cb => cb.Checked ? "true" : "false",
            PdfComboBoxField c  => c.Value?.ToString(),
            _                   => null,
        };

        return new PdfFormField
        {
            Name = field.Name,
            Type = type,
            Value = value,
            Options = GetOptions(field),
            IsReadOnly = field.ReadOnly,
            IsRequired = (field.Elements.GetInteger("/Ff") & 2) != 0,
        };
    }

    private static IReadOnlyList<string> GetOptions(PdfAcroField field)
    {
        if (field is not PdfComboBoxField and not PdfListBoxField) return [];

        if (field.Elements["/Opt"] is not PdfArray optArray) return [];

        var options = new List<string>(optArray.Elements.Count);
        for (var i = 0; i < optArray.Elements.Count; i++)
        {
            var item = optArray.Elements[i];
            options.Add(item switch
            {
                PdfString s                             => s.Value,
                PdfName n                               => n.Value,
                PdfArray a when a.Elements.Count >= 2  => (a.Elements[1] as PdfString)?.Value ?? "",
                _                                      => item?.ToString() ?? "",
            });
        }
        return options;
    }

    private static bool IsCheckedValue(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value == "1" ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("on", StringComparison.OrdinalIgnoreCase);
}
