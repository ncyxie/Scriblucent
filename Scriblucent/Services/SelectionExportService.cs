using System.IO;
using System.IO.Compression;
using Scriblucent.Models;
using Scriblucent.Localization;

namespace Scriblucent.Services;

/// <summary>An owned document copy captured on the UI dispatcher; writers never touch a live editor.</summary>
internal sealed record DocumentExportSnapshot(EditorDocumentKind Kind, string Title, object Document)
{
    internal string? NoteMediaDirectory { get; init; }
    internal static DocumentExportSnapshot Capture(object document) => document switch
    {
        NoteDocument note => new(EditorDocumentKind.Note, note.Title, EditorHosting.Clone(note)),
        FlashcardSetDocument cards => new(EditorDocumentKind.Flashcards, cards.Title, EditorHosting.Clone(cards)),
        QuizDocument quiz => new(EditorDocumentKind.Quiz, quiz.Title, EditorHosting.Clone(quiz)),
        MindMapDocument map => new(EditorDocumentKind.MindMap, map.Title, EditorHosting.Clone(map)),
        _ => throw new ArgumentException("Unsupported export document.", nameof(document))
    };
    internal string Extension => Kind switch { EditorDocumentKind.Note => ".scribnote", EditorDocumentKind.Flashcards => ".scribflash", EditorDocumentKind.Quiz => ".scribquiz", _ => ".scribmap" };
}

internal static class SelectionExportService
{
    internal static object Subset(object source, IReadOnlySet<Guid> ids)
    {
        if (source is FlashcardSetDocument cards)
        {
            var copy = EditorHosting.Clone(cards);
            copy.Cards = copy.Cards.Where(card => ids.Contains(card.Id)).ToList();
            if (copy.Cards.Count == 0) throw new InvalidOperationException(LocalizationService.GetString("SelectionExportEmpty"));
            var sets = copy.Cards.Select(card => card.SetIndex).ToHashSet();
            copy.SetNames = copy.SetNames.Where(pair => sets.Contains(pair.Key)).ToDictionary();
            var session = copy.StudySession;
            var current = session.CurrentCardId;
            var oldHistoryPosition = session.HistoryPosition;
            var oldHistory = session.History;
            session.History = oldHistory.Where(ids.Contains).ToList();
            session.HistoryPosition = oldHistory.Take(Math.Max(0, oldHistoryPosition + 1)).Count(ids.Contains) - 1;
            session.CurrentCardId = current is Guid id && ids.Contains(id) ? current : copy.Cards.FirstOrDefault()?.Id;
            return copy;
        }
        if (source is QuizDocument quiz)
        {
            var copy = EditorHosting.Clone(quiz);
            copy.Questions = copy.Questions.Where(question => ids.Contains(question.Id)).ToList();
            if (copy.Questions.Count == 0) throw new InvalidOperationException(LocalizationService.GetString("SelectionExportEmpty"));
            if (copy.ProgressSession is { } session)
            {
                var current = session.Questions.ElementAtOrDefault(session.CurrentQuestionIndex)?.Id;
                session.Questions = session.Questions.Where(question => ids.Contains(question.Id)).ToList();
                session.SelectedOptionIndexesByQuestionId = session.SelectedOptionIndexesByQuestionId.Where(pair => ids.Contains(pair.Key)).ToDictionary();
                session.PlainTextAnswersByQuestionId = session.PlainTextAnswersByQuestionId.Where(pair => ids.Contains(pair.Key)).ToDictionary();
                session.FlaggedQuestionIds = session.FlaggedQuestionIds.Where(ids.Contains).ToList();
                session.CurrentQuestionIndex = Math.Max(0, session.Questions.FindIndex(question => question.Id == current));
                if (session.Questions.Count == 0) copy.ProgressSession = null;
            }
            return copy;
        }
        throw new ArgumentException("Only editor cards/questions support subset export.", nameof(source));
    }
    internal static string SafeName(string title, string fallback = "Untitled")
    {
        var name = NoteFileService.CreateSafeFileName(title, fallback).TrimEnd(' ', '.');
        if (name.Length == 0) name = fallback;
        var stem = name.Split('.')[0].TrimEnd(' ', '.');
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) name = "_" + name;
        return name.Length > 140 ? name[..140].TrimEnd(' ', '.') : name;
    }
    internal static Task ExportArchiveAsync(IReadOnlyList<DocumentExportSnapshot> documents, string path, CancellationToken cancellationToken, IProgress<string>? progress = null)
        => Task.Run(async () =>
        {
            if (documents.Count == 0) throw new InvalidOperationException(LocalizationService.GetString("SelectionExportEmpty"));
            string stage = StagePath(path), scratch = Path.Combine(Path.GetTempPath(), "Scriblucent", "SelectionExport", Guid.NewGuid().ToString("N"));
            try
            {
                cancellationToken.ThrowIfCancellationRequested(); Directory.CreateDirectory(scratch);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var archive = ZipFile.Open(stage, ZipArchiveMode.Create))
                {
                    for (int i = 0; i < documents.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); var document = documents[i];
                        var basename = SafeName(document.Title); string name = basename + document.Extension; int suffix = 2;
                        while (!used.Add(name)) name = $"{basename} ({suffix++}){document.Extension}";
                        progress?.Report($"{i + 1}/{documents.Count}: {document.Title}");
                        var file = Path.Combine(scratch, name); WriteDocument(file, SelfContainedDocument(document), document.Extension);
                        cancellationToken.ThrowIfCancellationRequested();
                        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                        await using (var input = File.OpenRead(file))
                        await using (var output = entry.Open())
                            await input.CopyToAsync(output, cancellationToken);
                        File.Delete(file);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested(); File.Move(stage, path, true);
            }
            finally { if (File.Exists(stage)) File.Delete(stage); if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
        }, cancellationToken);
    private static object SelfContainedDocument(DocumentExportSnapshot snapshot)
    {
        if (snapshot.Document is not NoteDocument note || snapshot.NoteMediaDirectory is null) return snapshot.Document;
        var copy = EditorHosting.Clone(note);
        foreach (var image in copy.Images)
        {
            if (!string.IsNullOrEmpty(image.Data) || string.IsNullOrEmpty(image.DataFilePath)) continue;
            var file = Path.Combine(snapshot.NoteMediaDirectory, Path.GetFileName(image.DataFilePath));
            if (!File.Exists(file)) continue; // Same missing-media policy as ordinary note export.
            image.Data = Convert.ToBase64String(File.ReadAllBytes(file)); image.DataFilePath = string.Empty;
        }
        return copy;
    }
    internal static async Task<IReadOnlyList<string>> ExportDocumentAsync(object snapshot, string path, CancellationToken cancellationToken, Func<string, CancellationToken, Task>? printable = null)
    {
        var stage = StagePath(path); var extension = Path.GetExtension(path).ToLowerInvariant();
        IReadOnlyList<string> warnings = Array.Empty<string>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (extension == ".xps" && printable is not null) await printable(stage, cancellationToken);
            else warnings = await Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return WriteDocument(stage, snapshot, extension); }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested(); File.Move(stage, path, true);
            return warnings;
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
    private static string StagePath(string path) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
    private static IReadOnlyList<string> WriteDocument(string path, object document, string extension)
    {
        switch (document, extension)
        {
            case (NoteDocument note, ".scribnote"): NoteFileService.SaveNotePackage(path, note); break;
            case (MindMapDocument map, ".scribmap"): MindMapFileService.SaveScribmapFile(path, map); break;
            case (FlashcardSetDocument cards, ".scribflash"): FlashcardFileService.SaveScribFlashFile(path, cards); break;
            case (FlashcardSetDocument cards, ".csv"): FlashcardDelimitedFormat.SaveCsvFile(path, cards); break;
            case (FlashcardSetDocument cards, ".txt"): FlashcardDelimitedFormat.SaveTxtFile(path, cards); break;
            case (QuizDocument quiz, ".scribquiz"): QuizFileService.SaveScribQuizFile(path, quiz); break;
            case (QuizDocument quiz, ".gift"): GiftQuizFormat.SaveGiftFile(path, quiz); break;
            case (QuizDocument quiz, ".csv"): return QuizCsvFormat.SaveCsvFile(path, quiz);
            default: throw new InvalidOperationException(LocalizationService.GetString("SelectionExportUnsupported"));
        }
        return Array.Empty<string>();
    }
}
