using Scriblucent.Localization;
using Scriblucent.Models;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Documents;

namespace Scriblucent.Services;

internal static class NoteFileService
{
    private const int CurrentScribNoteFormatVersion = 1;

    private static readonly JsonSerializerOptions NotePackageJsonOptions = new()
    {
        WriteIndented = true
    };

    public static bool IsScribNotePath(string path)
        => string.Equals(Path.GetExtension(path), ".scribnote", StringComparison.OrdinalIgnoreCase);

    public static bool IsLegacyNoteCardPath(string path)
        => string.Equals(Path.GetExtension(path), ".notecard", StringComparison.OrdinalIgnoreCase);

    public static bool IsNotePackagePath(string path)
        => IsScribNotePath(path) || IsLegacyNoteCardPath(path);

    public static string LoadEditorContentFromFile(string path)
    {
        var extension = Path.GetExtension(path);
        if (IsUnsupportedNoteFile(extension))
            throw new InvalidOperationException(LocalizationService.GetString("FailedToOpenFile"));

        if (string.Equals(extension, ".rtf", StringComparison.OrdinalIgnoreCase))
            return Convert.ToBase64String(File.ReadAllBytes(path));

        return ReadTextFileWithFallback(path);
    }

    private static bool IsUnsupportedNoteFile(string extension)
        => string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".xamlpackage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".xamlpkg", StringComparison.OrdinalIgnoreCase);

    public static NoteDocument LoadNotePackage(string path)
    {
        var document = ReadNotePackage(path);
        NormalizeImportedDocument(document, Path.GetFileNameWithoutExtension(path));
        return document;
    }

    /// <summary>Package contents without import normalization, for replacing an existing note.</summary>
    public static NoteDocument ReadNotePackage(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(LocalizationService.GetString("FailedToOpenFile"), path);

        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LocalizationService.GetString("FailedToOpenFile"), ex);
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
            throw new InvalidOperationException(LocalizationService.GetString("FailedToOpenFile"), ex);
        }

        if (!hasEnvelope)
        {
            return JsonSerializer.Deserialize<NoteDocument>(json, NotePackageJsonOptions)
                ?? throw new InvalidDataException(LocalizationService.GetString("FailedToOpenFile"));
        }

        var file = JsonSerializer.Deserialize<ScribNoteFile>(json, NotePackageJsonOptions)
            ?? throw new InvalidDataException(LocalizationService.GetString("FailedToOpenFile"));

        if (file.FormatVersion > CurrentScribNoteFormatVersion)
            throw new InvalidOperationException(LocalizationService.GetString("NoteImportUnsupportedVersion"));

        return file.Document
            ?? throw new InvalidDataException(LocalizationService.GetString("FailedToOpenFile"));
    }

    public static void SaveNotePackage(string path, NoteDocument document)
    {
        var file = new ScribNoteFile { FormatVersion = CurrentScribNoteFormatVersion, Document = document };
        var json = JsonSerializer.Serialize(file, NotePackageJsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    public static void SavePlainText(string path, string text)
        => File.WriteAllText(path, text, new UTF8Encoding(false));

    public static void SaveTextRange(string path, TextRange textRange, string dataFormat)
    {
        using var stream = File.Create(path);
        textRange.Save(stream, dataFormat);
    }

    public static string ReadTextFileWithFallback(string path)
    {
        var rawBytes = File.ReadAllBytes(path);

        try
        {
            return new UTF8Encoding(false, true).GetString(rawBytes);
        }
        catch
        {
            try
            {
                using var stream = new MemoryStream(rawBytes);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch
            {
                try
                {
                    return Encoding.Default.GetString(rawBytes);
                }
                catch
                {
                    try
                    {
                        return Encoding.GetEncoding(1257).GetString(rawBytes);
                    }
                    catch
                    {
                        return string.Empty;
                    }
                }
            }
        }
    }

    public static void NormalizeImportedDocument(NoteDocument document, string fallbackTitle)
    {
        document.Id = Guid.NewGuid();
        document.Title = string.IsNullOrWhiteSpace(document.Title)
            ? fallbackTitle
            : document.Title.Trim();
        document.Content ??= string.Empty;
        document.Tags = document.Tags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        document.FontFamily = string.IsNullOrWhiteSpace(document.FontFamily)
            ? "Calibri"
            : document.FontFamily;
        document.FontSize = document.FontSize > 0 ? document.FontSize : 14;
        document.CreatedAt = DateTime.UtcNow;
        document.LastModified = DateTime.Now;
        document.EditHistory ??= new List<NoteEditHistoryEntry>();
        document.GroupId = null;
        document.IsPinned = false;
        document.IsDeleted = false;
        document.DeletedAt = null;
        document.ScheduledAt = null;
        document.ScheduleNote = string.Empty;
        document.Schedules = new List<NoteScheduleEntry>();
    }

    public static string CreateSafeFileName(string title, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(title)
            ? fallback
            : title.Trim();

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(candidate)
            ? fallback
            : candidate;
    }
}
