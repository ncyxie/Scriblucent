namespace Scriblucent.Models;

/// <summary>
/// Versioned envelope for the native .scribflash export format - written as "manifest.json" inside
/// a zip package (see FlashcardFileService), alongside a media/ folder holding copies of every
/// image/audio file the document's cards reference. A lossless, full-fidelity serialization of a
/// single FlashcardSetDocument, kept separate from the internal flashcards.json store's shape.
/// Mirrors ScribQuizFile's exact pattern - plain JSON here, because the zip container is what
/// carries the media files that a bare JSON export couldn't reference portably.
/// </summary>
public sealed class ScribFlashFile
{
    public int FormatVersion { get; set; } = 1;
    public FlashcardSetDocument Document { get; set; } = new();
}
