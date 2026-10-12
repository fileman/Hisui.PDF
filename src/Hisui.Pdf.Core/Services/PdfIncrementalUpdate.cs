using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// Appends objects to an existing PDF as an incremental update (ISO 32000 §7.5.6): the original bytes stay untouched,
/// so the signatures already in the file keep verifying. Rewritten objects keep their number; the new cross-reference
/// section (a table, or a stream when the file uses one) points back at the previous one with /Prev.
/// </summary>
internal sealed partial class PdfIncrementalUpdate
{
    private readonly MemoryStream _out = new();
    private readonly List<(int Num, int Gen, long Offset)> _entries = [];
    private readonly long _prevXref;
    private readonly bool _xrefStream;
    private readonly string _trailerKeys; // /Root, /Info and /ID copied from the last trailer
    private int _size;

    /// <exception cref="InvalidDataException">The last cross-reference section cannot be located (damaged file).</exception>
    /// <exception cref="NotSupportedException">The file is encrypted.</exception>
    public PdfIncrementalUpdate(byte[] original)
    {
        var text = Encoding.Latin1.GetString(original);
        var sx = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var prev = sx < 0 ? null : StartXref().Match(text, sx + 9);
        if (prev is not { Success: true } || !long.TryParse(prev.Groups[1].Value, out _prevXref) || _prevXref >= sx)
            throw new InvalidDataException("startxref not found.");

        var at = (int)_prevXref;
        string dict;
        if (string.CompareOrdinal(text, at, "xref", 0, 4) == 0)
        {
            var trailer = text.IndexOf("trailer", at, StringComparison.Ordinal);
            if (trailer < 0 || trailer > sx) throw new InvalidDataException("trailer not found.");
            dict = text[trailer..sx];
        }
        else if (XrefStreamHeader().IsMatch(text, at))
        {
            _xrefStream = true;
            var stream = text.IndexOf("stream", at, StringComparison.Ordinal);
            if (stream < 0 || stream > sx) throw new InvalidDataException("xref stream not found.");
            dict = text[at..stream];
        }
        else throw new InvalidDataException("startxref does not point at a cross-reference section.");

        if (dict.Contains("/Encrypt", StringComparison.Ordinal)) throw new NotSupportedException("Encrypted documents cannot be signed.");
        var size = SizeKey().Match(dict);
        var root = RootKey().Match(dict);
        if (!size.Success || !root.Success) throw new InvalidDataException("trailer without /Size or /Root.");
        _size = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
        _trailerKeys = string.Join(" ", new[] { root, InfoKey().Match(dict), IdKey().Match(dict) }.Where(m => m.Success).Select(m => m.Value));

        _out.Write(original);
        if (original[^1] is not ((byte)'\n' or (byte)'\r')) _out.WriteByte((byte)'\n');
    }

    /// <summary>Reserves a new object number.</summary>
    public int Allocate() => _size++;

    /// <summary>Writes one object; returns the absolute offset of <paramref name="body"/> (to patch placeholders in it later).</summary>
    public long WriteObject(int num, int gen, string body, byte[]? stream = null)
    {
        _entries.Add((num, gen, _out.Position));
        Write($"{num} {gen} obj\n");
        var bodyAt = _out.Position;
        Write(body);
        if (stream is not null)
        {
            Write("\nstream\n");
            _out.Write(stream);
            Write("\nendstream");
        }
        Write("\nendobj\n");
        return bodyAt;
    }

    /// <summary>Writes a changed object of the original file under its own number.</summary>
    public void Rewrite(PdfObject obj)
    {
        var r = obj.Reference ?? throw new NotSupportedException("Only indirect objects can be rewritten.");
        var (body, stream) = Serialize(obj, Identity);
        WriteObject(r.ObjectNumber, r.GenerationNumber, body, stream);
    }

    /// <summary>Appends the cross-reference section and trailer; returns the whole updated file.</summary>
    public byte[] Finish()
    {
        if (_xrefStream)
        {
            var num = Allocate();
            var xrefAt = _out.Position;
            var entries = _entries.Append((Num: num, Gen: 0, Offset: xrefAt)).OrderBy(e => e.Num).ToList();
            var data = new byte[entries.Count * 7];
            for (var i = 0; i < entries.Count; i++)
            {
                data[i * 7] = 1;
                for (var b = 0; b < 4; b++) data[i * 7 + 1 + b] = (byte)(entries[i].Offset >> (8 * (3 - b)));
                data[i * 7 + 5] = (byte)(entries[i].Gen >> 8);
                data[i * 7 + 6] = (byte)entries[i].Gen;
            }
            var index = string.Join(" ", Runs(entries).Select(r => $"{entries[r.Start].Num} {r.Count}"));
            WriteObject(num, 0, $"<< /Type /XRef /Size {_size} /W [1 4 2] /Index [{index}] /Prev {_prevXref} {_trailerKeys} /Length {data.Length} >>", data);
            Write($"startxref\n{xrefAt}\n%%EOF\n");
        }
        else
        {
            var entries = _entries.OrderBy(e => e.Num).ToList();
            var xrefAt = _out.Position;
            Write("xref\n");
            foreach (var (start, count) in Runs(entries))
            {
                Write($"{entries[start].Num} {count}\n");
                for (var i = start; i < start + count; i++) Write($"{entries[i].Offset:D10} {entries[i].Gen:D5} n\r\n");
            }
            Write($"trailer\n<< /Size {_size} /Prev {_prevXref} {_trailerKeys} >>\nstartxref\n{xrefAt}\n%%EOF\n");
        }
        return _out.ToArray();
    }

    /// <summary>Consecutive object numbers, as (first index, count) runs of the sorted entries.</summary>
    private static IEnumerable<(int Start, int Count)> Runs(List<(int Num, int Gen, long Offset)> sorted)
    {
        for (var i = 0; i < sorted.Count;)
        {
            var j = i + 1;
            while (j < sorted.Count && sorted[j].Num == sorted[j - 1].Num + 1) j++;
            yield return (i, j - i);
            i = j;
        }
    }

    private void Write(string s) => _out.Write(Encoding.ASCII.GetBytes(s));

    // ── serialisation ────────────────────────────────────────────────────────

    public static string Identity(PdfReference r) => $"{r.ObjectNumber} {r.GenerationNumber} R";

    /// <summary>PDF syntax of an object (and its raw, still-encoded stream); <paramref name="refText"/> writes references.</summary>
    public static (string Body, byte[]? Stream) Serialize(PdfObject obj, Func<PdfReference, string> refText)
    {
        var sb = new StringBuilder();
        Append(sb, obj, refText);
        return (sb.ToString(), (obj as PdfDictionary)?.Stream?.Value);
    }

    public static string Serialize(PdfItem? item, Func<PdfReference, string> refText)
    {
        var sb = new StringBuilder();
        Append(sb, item, refText);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, PdfItem? item, Func<PdfReference, string> refText)
    {
        switch (item)
        {
            case null or PdfNull or PdfNullObject: sb.Append("null"); break;
            case PdfReference r: sb.Append(refText(r)); break;
            case PdfLiteral l: sb.Append(l.Value); break;
            case PdfName n: AppendName(sb, n.Value); break;
            case PdfNameObject n: AppendName(sb, n.Value); break;
            case PdfBoolean b: sb.Append(b.Value ? "true" : "false"); break;
            case PdfBooleanObject b: sb.Append(b.Value ? "true" : "false"); break;
            case PdfInteger i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfIntegerObject i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfLongInteger i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfLongIntegerObject i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfReal d: sb.Append(Num(d.Value)); break;
            case PdfRealObject d: sb.Append(Num(d.Value)); break;
            case PdfString s: sb.Append(Str(s.Value)); break;
            case PdfStringObject s: sb.Append(Str(s.Value)); break;
            case PdfRectangle rc: sb.Append($"[{Num(rc.X1)} {Num(rc.Y1)} {Num(rc.X2)} {Num(rc.Y2)}]"); break;
            case PdfDictionary d:
                sb.Append("<<");
                foreach (var key in d.Elements.Keys)
                {
                    if (d.Stream is not null && key == "/Length") continue;
                    AppendName(sb, key);
                    sb.Append(' ');
                    Append(sb, d.Elements[key], refText);
                    sb.Append(' ');
                }
                if (d.Stream is not null) sb.Append("/Length ").Append(d.Stream.Value.Length).Append(' ');
                sb.Append(">>");
                break;
            case PdfArray a:
                sb.Append('[');
                for (var i = 0; i < a.Elements.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    Append(sb, a.Elements[i], refText);
                }
                sb.Append(']');
                break;
            default: throw new NotSupportedException($"Cannot serialise {item.GetType().Name}.");
        }
    }

    private static void AppendName(StringBuilder sb, string name)
    {
        sb.Append('/');
        foreach (var b in Encoding.UTF8.GetBytes(name.TrimStart('/')))
            if (b is < 0x21 or > 0x7E || "()<>[]{}/%#".Contains((char)b)) sb.Append('#').Append(b.ToString("X2"));
            else sb.Append((char)b);
    }

    /// <summary>A text string as hex: Latin-1 bytes when they suffice, else UTF-16BE with a byte-order mark.</summary>
    public static string Str(string s) => s.All(c => c <= 0xFF)
        ? "<" + Convert.ToHexString(Encoding.Latin1.GetBytes(s)) + ">"
        : "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(s)) + ">";

    public static string Num(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\G\s*(\d+)")] private static partial Regex StartXref();
    [GeneratedRegex(@"\G\d+\s+\d+\s+obj")] private static partial Regex XrefStreamHeader();
    [GeneratedRegex(@"/Size\s+(\d+)")] private static partial Regex SizeKey();
    [GeneratedRegex(@"/Root\s+\d+\s+\d+\s+R")] private static partial Regex RootKey();
    [GeneratedRegex(@"/Info\s+\d+\s+\d+\s+R")] private static partial Regex InfoKey();
    [GeneratedRegex(@"/ID\s*\[[^\]]*\]")] private static partial Regex IdKey();
}
