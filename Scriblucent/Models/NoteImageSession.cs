namespace Scriblucent.Models;

/// <summary>
/// Non-UI session state for a floating note image. Bytes, decode budget, and document-space
/// geometry live here; the editor widget mirrors geometry for presentation only.
/// </summary>
public sealed class NoteImageSession
{
    public required Guid Id { get; init; }
    public required byte[] Bytes { get; init; }
    public double NaturalPixelWidth { get; init; }
    public double NaturalPixelHeight { get; init; }
    public int DecodePixelWidth { get; set; }
    public double DocumentLeft { get; set; }
    public double DocumentTop { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    /// <summary>When true, resize keeps the image aspect ratio (default).</summary>
    public bool LockAspectRatio { get; set; } = true;

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
