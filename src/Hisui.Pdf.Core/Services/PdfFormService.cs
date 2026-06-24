using Hisui.Pdf.Core.Abstractions;
using Hisui.Pdf.Core.Model;
using PDFtoImage;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.IO;

namespace Hisui.Pdf.Core.Services;

/// <summary>
/// AcroForm read/fill backed by PDFsharp; flatten is implemented as rasterisation so it works
/// regardless of whether the form has pre-built appearance streams.
/// </summary>
internal sealed class PdfFormService : IPdfFormService
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

            PdfAcroForm form;
            try { form = doc.AcroForm; }
            catch (InvalidOperationException) { return pdf; }

            if (form.Fields.Count == 0) return pdf;

            for (var i = 0; i < form.Fields.Count; i++)
            {
                var field = form.Fields[i];
                if (!values.TryGetValue(field.Name, out var value)) continue;

                if (field is PdfTextField textField)
                    textField.Text = value;
                else if (field is PdfCheckBoxField checkField)
                    checkField.Checked = IsCheckedValue(value);
                else if (field is PdfComboBoxField comboField)
                    comboField.Value = new PdfString(value);
            }

            using var output = new MemoryStream();
            doc.Save(output);
            return output.ToArray();
        }, ct);
    }

    public async Task<byte[]> FlattenAsync(byte[] pdf, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        var pageCount = await Task.Run(() => Conversion.GetPageCount(pdf), ct);
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
            outDoc.Save(ms);
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
