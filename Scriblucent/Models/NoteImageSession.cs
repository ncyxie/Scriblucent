namespace Scriblucent.Models;

using System.Windows.Media.Imaging;

/// <summary>Numbering style for image captions (LibreOffice's Insert Caption numbering list).</summary>
public enum CaptionNumbering
{
    Arabic,
    RomanUpper,
    RomanLower,
    LettersUpper,
    LettersLower,
    None
}

/// <summary>Caption placement relative to the photo (Above/Below).</summary>
public enum CaptionPosition
{
    Below,
    Above
}

/// <summary>
/// How body text flows around a floating note image. Mirrors word-processor wrap modes
/// (LibreOffice "Parallel"/"Square", ONLYOFFICE "Square"): text lines whose vertical range
/// overlaps the image's rectangular wrap box are shortened on the side with more room.
/// </summary>
public enum NoteImageWrapMode
{
    /// <summary>No wrap - text flows under/over the image (image floats above text).</summary>
    None = 0,
    /// <summary>Square wrap - text flows around the image's rectangular bounding box plus a gap.</summary>
    Square = 1,
    /// <summary>
    /// Top and bottom wrap (Word/LibreOffice "None") - the image's vertical band is reserved
    /// across the full text width: nothing flows beside it, text continues below.
    /// </summary>
    TopAndBottom = 2,
    /// <summary>
    /// In line with text (LibreOffice "As character", ONLYOFFICE "In line") - the image is
    /// anchored to a character position and flows as part of the paragraph's text, moving
    /// with edits. The anchor is <see cref="NoteImageSession.InlineAnchorOffset"/>.
    /// </summary>
    Inline = 3
}

/// <summary>Stacking-order adjustment for overlapping floating images.</summary>
public enum NoteImageArrangeDirection
{
    BringForward,
    SendBackward
}

/// <summary>
/// Non-UI session state for a floating note image. Bytes, decode budget, and document-space
/// geometry live here; the editor widget mirrors geometry for presentation only.
/// </summary>
public sealed class NoteImageSession
{
    public required Guid Id { get; init; }
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
    public double NaturalPixelWidth { get; set; }
    public double NaturalPixelHeight { get; set; }
    public int DecodePixelWidth { get; set; }
    /// <summary>
    /// Cached frozen display source - the decode-width-optimized view of <see cref="Bytes"/>.
    /// Null until first decoded; the coordinator reuses it (and re-decodes only when the
    /// needed decode width exceeds <see cref="DecodePixelWidth"/>). Invalidate whenever the
    /// picture data changes. Frozen sources are immutable and safe to share.
    /// </summary>
    public BitmapSource? DisplaySource { get; set; }
    /// <summary>
    /// Cached Base64 encoding of <see cref="Bytes"/> - the save path builds attachments on
    /// every auto-save, and re-encoding the (immutable) bytes each time churns large strings.
    /// Computed lazily by <see cref="Scriblucent.Services.FloatingImageCoordinator.BuildAttachments"/>
    /// (used only when no media directory is available); invalidate whenever the picture data changes.
    /// </summary>
    public string? AttachmentData { get; set; }
    /// <summary>Cached absolute path of the photo's media file (written once per session by the
    /// coordinator) - subsequent attachment builds reference it instead of re-writing.</summary>
    public string? MediaFilePath { get; set; }
    public double DocumentLeft { get; set; }
    public double DocumentTop { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    /// <summary>When true, resize keeps the image aspect ratio (default).</summary>
    public bool LockAspectRatio { get; set; } = true;
    /// <summary>Text wrap mode. Missing on old notes → treated as None.</summary>
    public NoteImageWrapMode WrapMode { get; set; } = NoteImageWrapMode.None;
    /// <summary>
    /// For <see cref="NoteImageWrapMode.Inline"/>: character offset of the image's anchor in
    /// the document's plain text (the position the inline element occupies in the flow).
    /// Null for floating modes and old notes.
    /// </summary>
    public int? InlineAnchorOffset { get; set; }
    /// <summary>
    /// Stacking position among the floating images (0 = bottom). Higher values render on top
    /// when images overlap. Missing on old notes → the saved attachment order is the order.
    /// </summary>
    public int ZIndex { get; set; }

    /// <summary>Caption label (category, e.g. "Figure"); null means the image has no caption.</summary>
    public string? CaptionLabel { get; set; }
    public string CaptionText { get; set; } = string.Empty;
    public CaptionNumbering CaptionNumbering { get; set; } = CaptionNumbering.Arabic;
    public string CaptionSeparator { get; set; } = ": ";
    public CaptionPosition CaptionPosition { get; set; } = CaptionPosition.Below;
    public bool CaptionExcludeLabel { get; set; }
    /// <summary>Current sequence number within the label (transient - assigned by the
    /// renumber pass, never persisted).</summary>
    public int CaptionNumber { get; set; }

    /// <summary>
    /// Cached rendered caption text - synced photos re-render their captions on every drag,
    /// resize and scroll pass, so the (rarely changing) string is built once and invalidated
    /// by the caption mutations and the renumber pass.
    /// </summary>
    public string? CaptionDisplayText { get; set; }

    public bool HasCaption => CaptionLabel is not null;

    public double DocumentBottom => DocumentTop + Height;
    public double DocumentRight => DocumentLeft + Width;

    public void SetGeometry(double left, double top, double width, double height)
    {
        DocumentLeft = left;
        DocumentTop = top;
        Width = width;
        Height = height;
    }
}
