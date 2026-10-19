using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Removes the glyphs that fall inside given regions from a page's content stream, so replaced text is really
/// gone (not just painted over and still extractable). The stream is tokenised at byte level and only the
/// text-showing operators that lose glyphs are rewritten — as a TJ array whose numeric adjustments keep every
/// remaining glyph exactly where it was; all other bytes are copied unchanged.
/// <para>
/// Glyph advances come from the font's width tables: simple fonts with /Widths and Type0 fonts with an
/// Identity encoding are handled; text in other fonts is left alone — callers detect the shortfall through the
/// returned counts and fall back to covering it.
/// </para>
/// <para>
/// Form XObjects (running headers and footers are often one form shared by every page) are walked too. A form
/// that loses glyphs is never edited in place: the page gets an edited copy under a new resource name, so the
/// other pages showing the original form are untouched.
/// </para>
/// </summary>
internal static class ContentTextRemover
{
    /// <summary>A region in raw user space (origin bottom-left) and the group it counts towards.</summary>
    public readonly record struct Region(double X1, double Y1, double X2, double Y2, int Group);

    /// <summary>Removes the glyphs centred inside any region; returns how many glyphs each group lost.</summary>
    public static int[] Remove(PdfPage page, IReadOnlyList<Region> regions, int groupCount)
    {
        var removed = new int[groupCount];
        if (regions.Count == 0 || page.Contents.Elements.Count == 0) return removed;

        var content = page.Contents.CreateSingleContent().Stream?.UnfilteredValue;
        if (content is null || content.Length == 0) return removed;

        var initial = new GraphicsState { Ctm = Matrix.Identity, HScale = 1 };
        var rewritten = new Rewriter(content, page.Resources, page.Owner, regions, removed, initial, depth: 0).Run();
        if (rewritten is null) return removed;

        page.Contents.Elements.Clear();
        var single = page.Contents.AppendContent();
        if (single.Stream is null) single.CreateStream(rewritten);
        else single.Stream.Value = rewritten;
        return removed;
    }

    // ── matrices (PDF row-vector convention: p' = p × M) ─────────────────────

    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
        public static Matrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

        public static Matrix operator *(Matrix m, Matrix n) => new(
            m.A * n.A + m.B * n.C, m.A * n.B + m.B * n.D,
            m.C * n.A + m.D * n.C, m.C * n.B + m.D * n.D,
            m.E * n.A + m.F * n.C + n.E, m.E * n.B + m.F * n.D + n.F);

        public (double X, double Y) Point(double x, double y) => (x * A + y * C + E, x * B + y * D + F);
        public (double X, double Y) Vector(double x, double y) => (x * A + y * C, x * B + y * D);
    }

    private struct GraphicsState
    {
        public Matrix Ctm;
        public double CharSpacing, WordSpacing, HScale, Leading, Rise, FontSize;
        public string? Font;
    }

    // ── fonts ────────────────────────────────────────────────────────────────

    private sealed class FontMetrics(bool twoByte, Func<int, double> width)
    {
        public bool TwoByte { get; } = twoByte;
        /// <summary>Glyph width in thousandths of text space.</summary>
        public double Width(int code) => width(code);
    }

    private sealed class FontTable(PdfDictionary? fonts)
    {
        private readonly Dictionary<string, FontMetrics?> _cache = [];

        public FontMetrics? Get(string? name)
        {
            if (name is null || fonts is null) return null;
            if (_cache.TryGetValue(name, out var cached)) return cached;
            FontMetrics? metrics;
            try { metrics = Load(fonts.Elements.GetDictionary(name)); }
            catch (Exception) { metrics = null; } // malformed font dictionary: treat as unsupported
            return _cache[name] = metrics;
        }

        private static FontMetrics? Load(PdfDictionary? font)
        {
            if (font is null) return null;
            switch (font.Elements.GetName("/Subtype"))
            {
                case "/Type0":
                {
                    // Only Identity-H/V: two bytes per code, code = CID.
                    if (font.Elements.GetName("/Encoding") is not ("/Identity-H" or "/Identity-V")) return null;
                    var cid = font.Elements.GetArray("/DescendantFonts")?.Elements.GetDictionary(0);
                    if (cid is null) return null;
                    var dw = cid.Elements.ContainsKey("/DW") ? Number(cid.Elements["/DW"]) : 1000;
                    var widths = new Dictionary<int, double>();
                    if (cid.Elements.GetArray("/W") is { } w)
                    {
                        var items = w.Elements.Select(Resolve).ToList();
                        for (var i = 0; i < items.Count;)
                        {
                            var first = (int)Number(items[i]);
                            if (i + 1 < items.Count && items[i + 1] is PdfArray list)
                            {
                                var values = list.Elements.Select(Resolve).ToList();
                                for (var k = 0; k < values.Count; k++) widths[first + k] = Number(values[k]);
                                i += 2;
                            }
                            else if (i + 2 < items.Count)
                            {
                                var last = (int)Number(items[i + 1]);
                                var value = Number(items[i + 2]);
                                for (var c = first; c <= last && c - first < 65536; c++) widths[c] = value;
                                i += 3;
                            }
                            else break;
                        }
                    }
                    return new FontMetrics(true, code => widths.TryGetValue(code, out var v) ? v : dw);
                }
                case "/Type1" or "/TrueType" or "/MMType1":
                {
                    var widthArray = font.Elements.GetArray("/Widths");
                    if (widthArray is null) return null; // standard 14 font without metrics
                    var firstChar = font.Elements.GetInteger("/FirstChar");
                    var widths = widthArray.Elements.Select(e => Number(Resolve(e))).ToArray();
                    var descriptor = font.Elements.GetDictionary("/FontDescriptor");
                    var missing = descriptor is not null && descriptor.Elements.ContainsKey("/MissingWidth")
                        ? Number(descriptor.Elements["/MissingWidth"]) : 0;
                    return new FontMetrics(false, code =>
                        code - firstChar is var i && (uint)i < (uint)widths.Length ? widths[i] : missing);
                }
                default:
                    return null; // Type3 (glyph-space matrix) and anything exotic
            }
        }

        private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

        private static double Number(PdfItem? item) => Resolve(item) switch
        {
            PdfInteger i => i.Value,
            PdfReal r => r.Value,
            PdfLongInteger l => l.Value,
            _ => 0,
        };
    }

    // ── tokens ───────────────────────────────────────────────────────────────

    private enum Kind { Number, String, Name, Array, Other, Operator }

    private sealed class Token
    {
        public Kind Kind;
        public int Start, End;
        public double Number;
        public byte[] Bytes = [];
        public string Text = "";
        public List<Token> Items = [];
    }

    /// <summary>
    /// Rewrites one content stream (a page's, or a form XObject's) against <paramref name="resources"/>, starting
    /// from the graphics state in force where it is drawn.
    /// </summary>
    private sealed class Rewriter(
        byte[] src, PdfDictionary? resources, PdfDocument document, IReadOnlyList<Region> regions, int[] removed,
        GraphicsState initial, int depth)
    {
        private const int MaxFormDepth = 8; // forms can nest (or, in broken files, loop)

        private readonly FontTable fonts = new(resources?.Elements.GetDictionary("/Font"));
        private int _pos;
        private readonly List<(int Start, int End, string Replacement)> _splices = [];
        private readonly Stack<GraphicsState> _stack = new();
        private GraphicsState _gs = initial;
        private Matrix _tm = Matrix.Identity, _tlm = Matrix.Identity;
        private bool _positionKnown = true;

        public byte[]? Run()
        {
            var operands = new List<Token>();
            while (Next() is { } token)
            {
                if (token.Kind != Kind.Operator) { operands.Add(token); continue; }
                Execute(token, operands);
                operands.Clear();
            }
            if (_splices.Count == 0) return null;

            using var output = new MemoryStream(src.Length + 64);
            var at = 0;
            foreach (var (start, end, replacement) in _splices)
            {
                output.Write(src, at, start - at);
                output.Write(Encoding.ASCII.GetBytes(replacement));
                at = end;
            }
            output.Write(src, at, src.Length - at);
            return output.ToArray();
        }

        private void Execute(Token op, List<Token> args)
        {
            double N(int i) => i < args.Count && args[i].Kind == Kind.Number ? args[i].Number : 0;
            switch (op.Text)
            {
                case "q": _stack.Push(_gs); break;
                case "Q": if (_stack.Count > 0) _gs = _stack.Pop(); break;
                case "cm" when args.Count == 6:
                    _gs.Ctm = new Matrix(N(0), N(1), N(2), N(3), N(4), N(5)) * _gs.Ctm; break;
                case "BT": _tm = _tlm = Matrix.Identity; _positionKnown = true; break;
                case "Tf" when args.Count == 2:
                    _gs.Font = args[0].Kind == Kind.Name ? args[0].Text : null; _gs.FontSize = N(1); break;
                case "Tc": _gs.CharSpacing = N(0); break;
                case "Tw": _gs.WordSpacing = N(0); break;
                case "Tz": _gs.HScale = N(0) / 100; break;
                case "TL": _gs.Leading = N(0); break;
                case "Ts": _gs.Rise = N(0); break;
                case "Td": MoveLine(N(0), N(1)); break;
                case "TD": _gs.Leading = -N(1); MoveLine(N(0), N(1)); break;
                case "T*": MoveLine(0, -_gs.Leading); break;
                case "Tm" when args.Count == 6:
                    _tm = _tlm = new Matrix(N(0), N(1), N(2), N(3), N(4), N(5)); _positionKnown = true; break;
                case "Tj" when args.Count == 1:
                    Show(op, args, args, prefix: ""); break;
                case "'" when args.Count == 1:
                    MoveLine(0, -_gs.Leading);
                    Show(op, args, args, prefix: "T* "); break;
                case "\"" when args.Count == 3:
                    _gs.WordSpacing = N(0); _gs.CharSpacing = N(1);
                    MoveLine(0, -_gs.Leading);
                    Show(op, args, [args[2]], prefix: $"{Fmt(N(0))} Tw {Fmt(N(1))} Tc T* "); break;
                case "TJ" when args.Count == 1 && args[0].Kind == Kind.Array:
                    Show(op, args, args[0].Items, prefix: ""); break;
                case "Do" when args.Count == 1 && args[0].Kind == Kind.Name:
                    DrawForm(op, args[0]); break;
            }
        }

        /// <summary>
        /// Walks a form XObject in the current graphics state. If it loses glyphs, an edited copy is added to this
        /// stream's resources under a fresh name and the Do is pointed at it; the original form stays as it was.
        /// </summary>
        private void DrawForm(Token op, Token name)
        {
            if (depth >= MaxFormDepth) return;
            var xobjects = resources?.Elements.GetDictionary("/XObject");
            if (xobjects?.Elements.GetDictionary(name.Text) is not { } form || form.Elements.GetName("/Subtype") != "/Form") return;
            if (form.Stream?.UnfilteredValue is not { Length: > 0 } body) return;

            var state = _gs;
            if (form.Elements.GetArray("/Matrix") is { Elements.Count: 6 } m)
                state.Ctm = new Matrix(Num(m, 0), Num(m, 1), Num(m, 2), Num(m, 3), Num(m, 4), Num(m, 5)) * state.Ctm;
            // A form without its own resources uses those of the stream that draws it.
            var formResources = form.Elements.GetDictionary("/Resources") ?? resources;
            var edited = new Rewriter(body, formResources, document, regions, removed, state, depth + 1).Run();
            if (edited is null) return;

            var copy = new PdfDictionary(document);
            foreach (var key in form.Elements.Keys)
                if (key is not ("/Length" or "/Filter" or "/DecodeParms"))
                    copy.Elements[key] = form.Elements[key];
            if (formResources is not null && !form.Elements.ContainsKey("/Resources"))
                copy.Elements["/Resources"] = formResources;
            copy.CreateStream(edited);
            document.Internals.AddObject(copy);

            var fresh = FreshName(xobjects);
            xobjects.Elements[fresh] = copy.Reference;
            _splices.Add((name.Start, op.End, $"{fresh} Do"));
        }

        private static string FreshName(PdfDictionary dict)
        {
            for (var i = 1; ; i++)
                if (!dict.Elements.ContainsKey($"/HisuiFm{i}")) return $"/HisuiFm{i}";
        }

        private static double Num(PdfArray array, int index) => (array.Elements[index] is PdfReference r ? r.Value : array.Elements[index]) switch
        {
            PdfInteger i => i.Value,
            PdfReal d => d.Value,
            _ => 0,
        };

        private void MoveLine(double tx, double ty) => _tm = _tlm = Matrix.Translate(tx, ty) * _tlm;

        /// <summary>
        /// Advances through <paramref name="items"/> (strings and TJ adjustments) glyph by glyph; if any glyph lies
        /// in a region, the operator (with its operands) is spliced out for an equivalent TJ without those glyphs.
        /// </summary>
        private void Show(Token op, List<Token> args, List<Token> items, string prefix)
        {
            var font = fonts.Get(_gs.Font);
            var fs = _gs.FontSize;
            if (font is null || fs == 0 || !_positionKnown) { _positionKnown = false; return; }

            var th = _gs.HScale;
            var parts = new StringBuilder();
            var kept = new List<byte>();
            var any = false;

            void FlushKept()
            {
                if (kept.Count == 0) return;
                parts.Append('<').Append(Convert.ToHexString(kept.ToArray())).Append('>');
                kept.Clear();
            }

            foreach (var item in items)
            {
                if (item.Kind == Kind.Number)
                {
                    FlushKept();
                    parts.Append(' ').Append(Fmt(item.Number)).Append(' ');
                    _tm = Matrix.Translate(-item.Number / 1000 * fs * th, 0) * _tm;
                    continue;
                }
                if (item.Kind != Kind.String) continue;

                var bytes = item.Bytes;
                var step = font.TwoByte ? 2 : 1;
                for (var i = 0; i + step <= bytes.Length; i += step)
                {
                    var code = step == 2 ? (bytes[i] << 8) | bytes[i + 1] : bytes[i];
                    var w0 = font.Width(code) / 1000;
                    var spacing = _gs.CharSpacing + (step == 1 && code == 32 ? _gs.WordSpacing : 0);
                    var tx = (w0 * fs + spacing) * th;

                    var group = RegionOf(w0 * fs * th, fs);
                    if (group >= 0)
                    {
                        any = true;
                        removed[group]++;
                        FlushKept();
                        // The same displacement the glyph had, so everything after it stays put.
                        parts.Append(' ').Append(Fmt(-tx * 1000 / (fs * th))).Append(' ');
                    }
                    else
                    {
                        for (var k = 0; k < step; k++) kept.Add(bytes[i + k]);
                    }
                    _tm = Matrix.Translate(tx, 0) * _tm;
                }
            }
            FlushKept();

            if (!any) return;
            var start = args.Count > 0 ? args[0].Start : op.Start;
            _splices.Add((start, op.End, $"{prefix}[{parts.ToString().Trim()}] TJ"));
        }

        /// <summary>The group of the region holding the glyph about to be shown (centre test), or -1.</summary>
        private int RegionOf(double advance, double fs)
        {
            var trm = _tm * _gs.Ctm;
            var (bx, by) = trm.Point(advance / 2, _gs.Rise);  // middle of the baseline segment
            var (ux, uy) = trm.Vector(0, fs);                    // "up" at full font size
            var cx = bx + ux * 0.3;
            var cy = by + uy * 0.3;
            var tol = Math.Sqrt(ux * ux + uy * uy) * 0.15;
            foreach (var r in regions)
                if (cx >= r.X1 - tol && cx <= r.X2 + tol && cy >= r.Y1 - tol && cy <= r.Y2 + tol)
                    return r.Group;
            return -1;
        }

        private static string Fmt(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

        // ── tokenizer ──────────────────────────────────────────────────────────

        private static bool IsWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
        private static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        private Token? Next()
        {
            while (true)
            {
                while (_pos < src.Length && IsWhite(src[_pos])) _pos++;
                if (_pos >= src.Length) return null;
                if (src[_pos] != '%') break;
                while (_pos < src.Length && src[_pos] is not (10 or 13)) _pos++;
            }

            var start = _pos;
            var c = src[_pos];
            switch (c)
            {
                case (byte)'(':
                    return new Token { Kind = Kind.String, Start = start, Bytes = LiteralString(), End = _pos };
                case (byte)'<' when _pos + 1 < src.Length && src[_pos + 1] == '<':
                    SkipDictionary();
                    return new Token { Kind = Kind.Other, Start = start, End = _pos };
                case (byte)'<':
                    return new Token { Kind = Kind.String, Start = start, Bytes = HexString(), End = _pos };
                case (byte)'[':
                {
                    _pos++;
                    var array = new Token { Kind = Kind.Array, Start = start };
                    while (Next() is { } t && !(t.Kind == Kind.Other && t.Text == "]")) array.Items.Add(t);
                    array.End = _pos;
                    return array;
                }
                case (byte)']' or (byte)'{' or (byte)'}' or (byte)'>' or (byte)')':
                    _pos++;
                    return new Token { Kind = Kind.Other, Start = start, End = _pos, Text = ((char)c).ToString() };
                case (byte)'/':
                    _pos++;
                    while (_pos < src.Length && !IsWhite(src[_pos]) && !IsDelimiter(src[_pos])) _pos++;
                    return new Token { Kind = Kind.Name, Start = start, End = _pos, Text = Encoding.Latin1.GetString(src, start, _pos - start) };
            }

            while (_pos < src.Length && !IsWhite(src[_pos]) && !IsDelimiter(src[_pos])) _pos++;
            if (_pos == start) _pos++; // stray byte: always make progress
            var text = Encoding.Latin1.GetString(src, start, _pos - start);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && (char.IsDigit(text[^1]) || text[^1] == '.'))
                return new Token { Kind = Kind.Number, Start = start, End = _pos, Number = number, Text = text };
            if (text == "BI") SkipInlineImage();
            return new Token { Kind = Kind.Operator, Start = start, End = _pos, Text = text };
        }

        private byte[] LiteralString()
        {
            var bytes = new List<byte>();
            var depth = 0;
            _pos++; // (
            while (_pos < src.Length)
            {
                var b = src[_pos++];
                if (b == '(') { depth++; bytes.Add(b); }
                else if (b == ')') { if (depth-- == 0) break; bytes.Add(b); }
                else if (b == '\\' && _pos < src.Length)
                {
                    var e = src[_pos++];
                    switch (e)
                    {
                        case (byte)'n': bytes.Add(10); break;
                        case (byte)'r': bytes.Add(13); break;
                        case (byte)'t': bytes.Add(9); break;
                        case (byte)'b': bytes.Add(8); break;
                        case (byte)'f': bytes.Add(12); break;
                        case 13: if (_pos < src.Length && src[_pos] == 10) _pos++; break; // line continuation
                        case 10: break;
                        case >= (byte)'0' and <= (byte)'7':
                        {
                            var value = e - '0';
                            for (var k = 0; k < 2 && _pos < src.Length && src[_pos] is >= (byte)'0' and <= (byte)'7'; k++)
                                value = value * 8 + (src[_pos++] - '0');
                            bytes.Add((byte)value);
                            break;
                        }
                        default: bytes.Add(e); break; // \( \) \\ and unknown escapes
                    }
                }
                else bytes.Add(b);
            }
            return [.. bytes];
        }

        private byte[] HexString()
        {
            _pos++; // <
            var digits = new StringBuilder();
            while (_pos < src.Length && src[_pos] != '>')
            {
                if (Uri.IsHexDigit((char)src[_pos])) digits.Append((char)src[_pos]);
                _pos++;
            }
            _pos++; // >
            if (digits.Length % 2 == 1) digits.Append('0');
            return Convert.FromHexString(digits.ToString());
        }

        private void SkipDictionary()
        {
            var depth = 0;
            while (_pos < src.Length)
            {
                if (src[_pos] == '<' && _pos + 1 < src.Length && src[_pos + 1] == '<') { depth++; _pos += 2; }
                else if (src[_pos] == '>' && _pos + 1 < src.Length && src[_pos + 1] == '>') { _pos += 2; if (--depth == 0) return; }
                else if (src[_pos] == '(') LiteralString();
                else _pos++;
            }
        }

        /// <summary>Skips "… ID &lt;binary&gt; EI" after BI: binary data may contain anything, so look for a delimited EI.</summary>
        private void SkipInlineImage()
        {
            while (Next() is { } t && !(t.Kind == Kind.Operator && t.Text == "ID")) { }
            if (_pos < src.Length) _pos++; // single whitespace after ID
            while (_pos + 2 < src.Length)
            {
                if (IsWhite(src[_pos]) && src[_pos + 1] == 'E' && src[_pos + 2] == 'I' &&
                    (_pos + 3 >= src.Length || IsWhite(src[_pos + 3]) || IsDelimiter(src[_pos + 3])))
                {
                    _pos += 3;
                    return;
                }
                _pos++;
            }
            _pos = src.Length;
        }
    }
}
