namespace Scriblucent.Models;

/// <summary>
/// Versioned envelope for the native .scribnote package. The library store stays notes.json;
/// this file is only the portable single-note export, matching ScribQuizFile.
/// </summary>
public sealed class ScribNoteFile
{
    public int FormatVersion { get; set; } = 1;
    public NoteDocument Document { get; set; } = new();
}
