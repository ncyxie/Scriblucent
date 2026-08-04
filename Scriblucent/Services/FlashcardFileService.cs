using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Scriblucent.Localization;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>
/// Flashcard file import/export. Two formats share one dispatch entry point
/// (LoadAnyFlashcardDocument):
/// - .scribflash (this app's own lossless format) is a zip package - a "manifest.json" envelope
///   (mirrors ScribQuizFile's shape) plus a media\ folder holding copies of every image/audio file
///   referenced by the document's cards. A plain-JSON export couldn't carry media portably, since
///   FlashcardItem's *ImageFileName/*AudioFileName fields are just filenames into a machine-local
///   folder (FlashcardMediaStorageService) - the zip is analogous to how Anki's own .apkg format
///   bundles a collection with a media folder. Round-trips everything, including SM-2/FSRS state.
/// - .csv/.txt (FlashcardDelimitedFormat) are structure-only, content-only interop formats - they
///   can't carry media, attempt history, or scheduling state, so normalization always discards
///   personal state for them regardless of the keepProgress flag.
/// </summary>
internal static class FlashcardFileService
{
    private const int CurrentScribFlashFormatVersion = 1;
    private const string ManifestFileName = "manifest.json";
    private const string MediaFolderName = "media";

    private static readonly JsonSerializerOptions ScribFlashJsonOptions = new()
    {
        WriteIndented = true
    };

    public static bool IsScribFlashPath(string path)
        => string.Equals(Path.GetExtension(path), ".scribflash", StringComparison.OrdinalIgnoreCase);

    public static bool IsCsvPath(string path)
        => string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);

    public static bool IsTxtPath(string path)
        => string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stages a manifest + media folder in a temp directory, then zips it - the write-side
    /// counterpart of the ZipFile.ExtractToDirectory pattern BundledModelHostService already uses
    /// for downloaded runtime archives.</summary>
    public static void SaveScribFlashFile(string path, FlashcardSetDocument document)
    {
        var tempWorkDir = Path.Combine(Path.GetTempPath(), "Scriblucent", "scribflash-export", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempWorkDir);

            var file = new ScribFlashFile { FormatVersion = CurrentScribFlashFormatVersion, Document = document };
            var json = JsonSerializer.Serialize(file, ScribFlashJsonOptions);
            File.WriteAllText(Path.Combine(tempWorkDir, ManifestFileName), json, new UTF8Encoding(false));

            var mediaDir = Path.Combine(tempWorkDir, MediaFolderName);
            Directory.CreateDirectory(mediaDir);

            var referencedFileNames = document.Cards
                .SelectMany(card => new[] { card.QuestionImageFileName, card.QuestionAudioFileName, card.AnswerImageFileName, card.AnswerAudioFileName })
                .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
                .Select(fileName => fileName!)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var fileName in referencedFileNames)
            {
                var sourcePath = FlashcardMediaStorageService.ResolvePath(fileName);
                if (sourcePath is not null && File.Exists(sourcePath))
                    File.Copy(sourcePath, Path.Combine(mediaDir, fileName), overwrite: true);
            }

            if (File.Exists(path))
                File.Delete(path);
            ZipFile.CreateFromDirectory(tempWorkDir, path);
        }
        finally
        {
            TryDeleteDirectory(tempWorkDir);
        }
    }

    /// <summary>Extracts the zip, deserializes the manifest (same envelope-probe/version-check
    /// shape as QuizFileService.LoadScribQuizDocument), then unconditionally rehomes every
    /// referenced media file into this machine's own media folder under a fresh name.</summary>
    public static FlashcardSetDocument LoadScribFlashDocument(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(LocalizationService.GetString("FlashcardImportFileNotFound"), path);

        var tempWorkDir = Path.Combine(Path.GetTempPath(), "Scriblucent", "scribflash-import", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempWorkDir);
            try
            {
                ZipFile.ExtractToDirectory(path, tempWorkDir, overwriteFiles: true);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException(LocalizationService.GetString("FlashcardImportParseError"), ex);
            }

            var manifestPath = Path.Combine(tempWorkDir, ManifestFileName);
            if (!File.Exists(manifestPath))
                throw new InvalidOperationException(LocalizationService.GetString("FlashcardImportParseError"));

            string json;
            try
            {
                json = File.ReadAllText(manifestPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(LocalizationService.GetString("FlashcardImportReadFailed"), ex);
            }

            ScribFlashFile file;
            try
            {
                file = JsonSerializer.Deserialize<ScribFlashFile>(json)
                    ?? throw new InvalidOperationException(LocalizationService.GetString("FlashcardImportFailed"));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(LocalizationService.GetString("FlashcardImportParseError"), ex);
            }

            if (file.FormatVersion > CurrentScribFlashFormatVersion)
                throw new InvalidOperationException(LocalizationService.GetString("FlashcardImportUnsupportedVersion"));

            var document = file.Document ?? throw new InvalidOperationException(LocalizationService.GetString("FlashcardImportFailed"));

            var mediaDir = Path.Combine(tempWorkDir, MediaFolderName);
            RehomeExtractedMedia(document, mediaDir);

            return document;
        }
        finally
        {
            TryDeleteDirectory(tempWorkDir);
        }
    }

    /// <summary>Copies every media file a card references from the extracted zip into this
    /// machine's own FlashcardMediaStorageService folder under a fresh GUID name, rewriting the
    /// card's field to match. Unconditional, independent of keepProgress - unlike scheduling state,
    /// this has no "preserve as-is" option, since the importing machine's media folder may already
    /// contain unrelated files under whatever names the zip happens to use. A file referenced but
    /// missing from the zip is dropped (field set to null) rather than left dangling.</summary>
    private static void RehomeExtractedMedia(FlashcardSetDocument document, string extractedMediaDir)
    {
        foreach (var card in document.Cards)
        {
            card.QuestionImageFileName = RehomeOne(card.QuestionImageFileName, extractedMediaDir, isImage: true);
            card.AnswerImageFileName = RehomeOne(card.AnswerImageFileName, extractedMediaDir, isImage: true);
            card.QuestionAudioFileName = RehomeOne(card.QuestionAudioFileName, extractedMediaDir, isImage: false);
            card.AnswerAudioFileName = RehomeOne(card.AnswerAudioFileName, extractedMediaDir, isImage: false);
        }
    }

    private static string? RehomeOne(string? fileName, string extractedMediaDir, bool isImage)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var extractedPath = Path.Combine(extractedMediaDir, fileName);
        if (!File.Exists(extractedPath))
            return null;

        try
        {
            return isImage
                ? FlashcardMediaStorageService.SaveImage(extractedPath)
                : FlashcardMediaStorageService.SaveAudio(extractedPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Single dispatch entry point for dashboard-level import - dispatches by extension, then
    /// normalizes the result as a fresh, standalone library entry (see
    /// NormalizeImportedFlashcardSetDocument).
    /// </summary>
    public static FlashcardSetDocument LoadAnyFlashcardDocument(string path, string fallbackTitle, bool keepProgress, out IReadOnlyList<string> warnings)
    {
        warnings = Array.Empty<string>();

        FlashcardSetDocument document;
        if (IsScribFlashPath(path))
        {
            document = LoadScribFlashDocument(path);
        }
        else if (IsCsvPath(path) || IsTxtPath(path))
        {
            var text = File.Exists(path)
                ? File.ReadAllText(path, Encoding.UTF8)
                : throw new FileNotFoundException(LocalizationService.GetString("FlashcardImportFileNotFound"), path);
            var delimiter = IsCsvPath(path) ? ',' : '\t';
            var result = FlashcardDelimitedFormat.ParseWithWarnings(text, delimiter, fallbackTitle);
            document = result.Document;
            warnings = result.Warnings;
        }
        else
        {
            throw new InvalidOperationException(LocalizationService.GetString("FlashcardImportUnsupportedFormat"));
        }

        NormalizeImportedFlashcardSetDocument(document, fallbackTitle, keepProgress);
        return document;
    }

    /// <summary>
    /// Does the source document actually carry any personal study progress worth asking about?
    /// CSV/TXT never do (their model can't represent it); .scribflash only does if this particular
    /// export happened to have real SM-2/FSRS state or an in-progress study session. Used to decide
    /// whether to show the keep-progress prompt at all before the user picks files to import.
    /// </summary>
    public static bool HasProgressData(FlashcardSetDocument document)
        => document.StudySession.CurrentCardId.HasValue
           || document.StudySession.History.Count > 0
           || document.Cards.Any(card => card.Repetitions > 0
               || card.DueDate.HasValue
               || card.LastReviewedAt.HasValue
               || card.IsKnown
               || card.IsUnknown);

    /// <summary>
    /// Resets an imported document to a fresh, standalone entry - always regenerates the document
    /// Id (even for .scribflash, where the embedded Id might match an existing library entry) so a
    /// re-imported backup or a file shared between two people's libraries can never silently
    /// overwrite an unrelated existing set via AddOrUpdateFlashcardSet's upsert-by-Id. Mirrors
    /// QuizFileService.NormalizeImportedQuizDocument's philosophy.
    /// </summary>
    public static void NormalizeImportedFlashcardSetDocument(FlashcardSetDocument document, string fallbackTitle, bool keepProgress)
    {
        document.Id = Guid.NewGuid();
        document.Title = string.IsNullOrWhiteSpace(document.Title)
            ? fallbackTitle
            : document.Title.Trim();
        document.Tags ??= new List<string>();
        document.Cards ??= new List<FlashcardItem>();
        document.SetNames ??= new Dictionary<int, string>();
        document.CreatedAt = DateTime.UtcNow;
        document.LastModified = DateTime.Now;
        document.GroupId = null;
        document.Schedules = new List<NoteScheduleEntry>();
        document.IsPinned = false;
        document.SourceNoteId = null;

        if (keepProgress)
        {
            // Card Id and SM-2/FSRS state are left untouched here - only the document-level Id
            // above is regenerated (for library uniqueness), same reasoning as
            // QuizFileService.NormalizeImportedQuizDocument's question-Id preservation.
            return;
        }

        document.StudySession = new FlashcardStudySession();

        // FlashcardItem.Id/SetIndex are init-only, so a fresh Id means reconstructing each card
        // rather than mutating in place - mirrors MainViewModel.CloneFlashcardSetDocument's own
        // card-copy pattern. SM-2/FSRS fields are simply left off the initializer, which leaves
        // them at the model's own "new card" defaults (EaseFactor 2.5, everything else 0/null).
        document.Cards = document.Cards.Select(card => new FlashcardItem
        {
            Id = Guid.NewGuid(),
            Question = card.Question,
            Answer = card.Answer,
            Category = card.Category,
            Hint = card.Hint,
            SetIndex = card.SetIndex,
            QuestionImageFileName = card.QuestionImageFileName,
            QuestionAudioFileName = card.QuestionAudioFileName,
            AnswerImageFileName = card.AnswerImageFileName,
            AnswerAudioFileName = card.AnswerAudioFileName
        }).ToList();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
