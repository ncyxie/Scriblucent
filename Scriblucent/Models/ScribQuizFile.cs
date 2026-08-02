namespace Scriblucent.Models;

/// <summary>
/// Versioned envelope for the native .scribquiz export format - a lossless, full-fidelity
/// serialization of a single QuizDocument, kept separate from QuizDocument/QuizStoreData so the
/// internal quizzes.json store's shape is unaffected. Mirrors ScribmapFile's exact pattern.
/// </summary>
public sealed class ScribQuizFile
{
    public int FormatVersion { get; set; } = 1;
    public QuizDocument Document { get; set; } = new();
}
