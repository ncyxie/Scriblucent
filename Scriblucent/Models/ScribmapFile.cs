namespace Scriblucent.Models;

/// <summary>
/// Versioned envelope for the native .scribmap export format - a lossless, full-fidelity
/// serialization of a single MindMapDocument, kept separate from MindMapDocument/MindMapStoreData
/// so the internal mind-maps.json store's shape is unaffected.
/// </summary>
public sealed class ScribmapFile
{
    public int FormatVersion { get; set; } = 1;
    public MindMapDocument Document { get; set; } = new();
}
