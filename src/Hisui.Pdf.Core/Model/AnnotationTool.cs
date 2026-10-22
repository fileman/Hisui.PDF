namespace Hisui.Pdf.Core.Model;

public enum AnnotationTool
{
    None,
    Highlight,
    Rectangle,
    FreeText,
    StickyNote,
    TextEdit,
    ImageOverlay,
    Signature,
    Watermark,
    OcrSelection,
    FormText,
    FormCheckBox,
    FormComboBox,
    FormRadio,
    FormListBox,
    FormSignature,
    /// <summary>A real sticky-note comment (/Text annotation), editable and replyable.</summary>
    Comment,
    /// <summary>A link area to a page or a web address.</summary>
    Link,
    /// <summary>A certificate (PAdES) signature in the dragged area.</summary>
    DigitalSignature,
    /// <summary>Saves the dragged area of the page as a PNG image.</summary>
    CaptureArea,
}
