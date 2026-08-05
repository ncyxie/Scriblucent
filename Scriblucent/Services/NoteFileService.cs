using Scriblucent.Models;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Documents;

namespace Scriblucent.Services;

internal static class NoteFileService
{
    private static readonly JsonSerializerOptions NotePackageJsonOptions = new()
    {
        WriteIndented = true
    };

    public static bool IsNotePackagePath(string path)
        => string.Equals(Path.GetExtension(path), ".notecard", StringComparison.OrdinalIgnoreCase);

    public static bool IsRichContentPackagePath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".xamlpackage", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".xamlpkg", StringComparison.OrdinalIgnoreCase);
    }

    public static string LoadEditorContentFromFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".rtf" || IsRichContentPackagePath(path))
            return Convert.ToBase64String(File.ReadAllBytes(path));

        return ReadTextFileWithFallback(path);
    }

    public static NoteDocument LoadNotePackage(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        var document = JsonSerializer.Deserialize<NoteDocument>(json, NotePackageJsonOptions)
            ?? throw new InvalidDataException("The note package is empty or invalid.");

        NormalizeImportedDocument(document, Path.GetFileNameWithoutExtension(path));
        return document;
    }

    public static void SaveNotePackage(string path, NoteDocument document)
    {
        var json = JsonSerializer.Serialize(document, NotePackageJsonOptions);
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
        document.Schedules ??= new List<NoteScheduleEntry>();
        document.ScheduleNote ??= string.Empty;
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
