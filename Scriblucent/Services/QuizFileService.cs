using System.IO;
using System.Text;
using System.Text.Json;
using Scriblucent.Localization;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>
/// Quiz file import/export. Four formats share one dispatch entry point (LoadAnyQuizDocument):
/// - .scribquiz (this app's own lossless JSON format, versioned envelope) round-trips a
///   QuizDocument exactly, including attempt history and in-progress state.
/// - bare .json is accepted as a legacy fallback - the quiz export button used to write an
///   unversioned QuizDocument straight to .json before .scribquiz existed, and anyone who already
///   has files like that shouldn't lose the ability to import them.
/// - .gift (Moodle's plain-text quiz format) and .csv (this app's own bulk-edit spreadsheet
///   schema) are both structure-only: they can't carry attempt history, in-progress state, or
///   most per-question metadata, so LoadAnyQuizDocument's normalization always discards personal
///   state for those two regardless of the keepProgress flag.
/// </summary>
internal static class QuizFileService
{
    private const int CurrentScribQuizFormatVersion = 1;

    private static readonly JsonSerializerOptions ScribQuizJsonOptions = new()
    {
        WriteIndented = true
    };

    public static bool IsScribQuizPath(string path)
        => string.Equals(Path.GetExtension(path), ".scribquiz", StringComparison.OrdinalIgnoreCase);

    public static bool IsLegacyJsonPath(string path)
        => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    public static bool IsGiftPath(string path)
        => string.Equals(Path.GetExtension(path), ".gift", StringComparison.OrdinalIgnoreCase);

    public static bool IsCsvPath(string path)
        => string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);

    public static void SaveScribQuizFile(string path, QuizDocument document)
    {
        var file = new ScribQuizFile { FormatVersion = CurrentScribQuizFormatVersion, Document = document };
        var json = JsonSerializer.Serialize(file, ScribQuizJsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    /// <summary>
    /// Loads a .scribquiz envelope, or falls back to a bare QuizDocument for legacy .json exports
    /// (anything written before this envelope existed). Detected up front by peeking at the root
    /// object for a top-level "FormatVersion" property, rather than deserializing twice.
    /// </summary>
    public static QuizDocument LoadScribQuizDocument(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(LocalizationService.GetString("QuizImportFileNotFound"), path);

        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LocalizationService.GetString("QuizImportReadFailed"), ex);
        }

        bool hasEnvelope;
        try
        {
            using var probe = JsonDocument.Parse(json);
            hasEnvelope = probe.RootElement.ValueKind == JsonValueKind.Object
                && probe.RootElement.TryGetProperty("FormatVersion", out _);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(LocalizationService.GetString("QuizImportParseError"), ex);
        }

        if (!hasEnvelope)
        {
            return JsonSerializer.Deserialize<QuizDocument>(json)
                ?? throw new InvalidOperationException(LocalizationService.GetString("QuizImportFailed"));
        }

        var file = JsonSerializer.Deserialize<ScribQuizFile>(json)
            ?? throw new InvalidOperationException(LocalizationService.GetString("QuizImportFailed"));

        if (file.FormatVersion > CurrentScribQuizFormatVersion)
            throw new InvalidOperationException(LocalizationService.GetString("QuizImportUnsupportedVersion"));

        return file.Document
            ?? throw new InvalidOperationException(LocalizationService.GetString("QuizImportFailed"));
    }

    /// <summary>
    /// Single dispatch entry point for dashboard-level import - dispatches by extension, then
    /// normalizes the result as a fresh, standalone library entry (see
    /// NormalizeImportedQuizDocument). GIFT/CSV results are parsed by GiftQuizFormat/QuizCsvFormat
    /// (see those files for warning collection - parse issues there don't throw, they're reported
    /// back to the caller separately so a mostly-good file doesn't fail as a whole).
    /// </summary>
    public static QuizDocument LoadAnyQuizDocument(string path, string fallbackTitle, bool keepProgress, out IReadOnlyList<string> warnings)
    {
        warnings = Array.Empty<string>();

        QuizDocument document;
        if (IsScribQuizPath(path) || IsLegacyJsonPath(path))
        {
            document = LoadScribQuizDocument(path);
        }
        else if (IsGiftPath(path))
        {
            var text = File.Exists(path)
                ? File.ReadAllText(path, Encoding.UTF8)
                : throw new FileNotFoundException(LocalizationService.GetString("QuizImportFileNotFound"), path);
            var result = GiftQuizFormat.Parse(text, fallbackTitle);
            document = result.Document;
            warnings = result.Warnings;
        }
        else if (IsCsvPath(path))
        {
            var text = File.Exists(path)
                ? File.ReadAllText(path, Encoding.UTF8)
                : throw new FileNotFoundException(LocalizationService.GetString("QuizImportFileNotFound"), path);
            var result = QuizCsvFormat.ParseWithWarnings(text, fallbackTitle);
            document = result.Document;
            warnings = result.Warnings;
        }
        else
        {
            throw new InvalidOperationException(LocalizationService.GetString("QuizImportUnsupportedFormat"));
        }

        NormalizeImportedQuizDocument(document, fallbackTitle, keepProgress);
        return document;
    }

    /// <summary>
    /// Does the source file actually carry any personal progress worth asking about? GIFT/CSV
    /// never do (their models can't represent it); .scribquiz/.json only do if this particular
    /// export happened to have attempt history, an in-progress session, or any per-question
    /// LastAttemptCorrect flag set. Used to decide whether to show the keep-progress prompt at all
    /// before the user picks files to import.
    /// </summary>
    public static bool HasProgressData(QuizDocument document)
        => document.Attempts.Count > 0
           || document.ProgressSession is not null
           || document.Questions.Any(question => question.LastAttemptCorrect.HasValue);

    /// <summary>
    /// Resets an imported document to a fresh, standalone entry - always regenerates the document
    /// Id (even for .scribquiz, where the embedded Id might match an existing library entry) so a
    /// re-imported backup or a file shared between two people's libraries can never silently
    /// overwrite an unrelated existing quiz via AddOrUpdateQuiz's upsert-by-Id. Mirrors
    /// MindMapFileService.NormalizeImportedDocument's philosophy.
    /// </summary>
    public static void NormalizeImportedQuizDocument(QuizDocument document, string fallbackTitle, bool keepProgress)
    {
        document.Id = Guid.NewGuid();
        document.Title = string.IsNullOrWhiteSpace(document.Title)
            ? fallbackTitle
            : document.Title.Trim();
        document.Questions ??= new List<QuizQuestion>();
        document.Tags ??= new List<string>();
        document.CreatedAt = DateTime.UtcNow;
        document.LastModified = DateTime.Now;
        document.GroupId = null;
        document.Schedules = new List<NoteScheduleEntry>();
        document.IsPinned = false;
        document.SourceNoteId = null;

        if (keepProgress)
        {
            // Question.Id is left completely untouched here - ProgressSession's
            // SelectedOptionIndexesByQuestionId/PlainTextAnswersByQuestionId/FlaggedQuestionIds and
            // every question's own LastAttemptCorrect are only ever meaningful relative to the
            // exact Ids stored in this same file; regenerating them would silently break every one
            // of those lookups. Only the document-level Id above is regenerated (for library
            // uniqueness) - that's safe because question Ids are only ever looked up within their
            // own document, never compared across two different quizzes.
            return;
        }

        document.Attempts = new List<QuizAttempt>();
        document.ProgressSession = null;
        foreach (var question in document.Questions)
        {
            question.Id = Guid.NewGuid();
            question.LastAttemptCorrect = null;
        }
    }
}
