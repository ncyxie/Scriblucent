using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Scriblucent.Controls;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>Extracts one bounded excerpt and maps pictures to source text, not character-count height guesses.</summary>
internal static class NotePreviewService
{
    internal const int Version = 1;
    internal const int MaxCharacters = 600;
    internal const double DefaultLayoutWidth = 720;
    private const char Marker = '\uFFFC';
    private static readonly Regex LegacyMarker = new(@"\[\[ScriblucentImage:[^\]]+\]\]", RegexOptions.Compiled);
    internal sealed record Position(int Offset, TextPointer Pointer, NoteImageAttachment? Image = null);

    internal static string SourceHash(NoteDocument note)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes((note.Content ?? string.Empty).AsSpan()));
        var layout = new StringBuilder().Append(note.FontFamily).Append('|')
            .Append(note.FontSize.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(note.PreviewLayoutWidth?.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(note.IsWordWrapEnabled);
        foreach (var image in note.Images ?? [])
            layout.Append(CultureInfo.InvariantCulture, $"|{image.Left:R},{image.Top:R},{image.Width:R},{image.Height:R},{(int)image.WrapMode},{image.InlineAnchorOffset}");
        hash.AppendData(MemoryMarshal.AsBytes(layout.ToString().AsSpan()));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static NotePreviewSnapshot Build(NoteDocument note)
    {
        var hash = SourceHash(note);
        if (note.CardPreview is { Version: Version } cached && cached.SourceHash == hash) return cached;
        FlowDocument document;
        var raw = note.Content ?? string.Empty;
        try
        {
            var encoded = raw.StartsWith("UEs", StringComparison.Ordinal) || raw.StartsWith("e1xc", StringComparison.Ordinal)
                || raw.StartsWith("e1xy", StringComparison.Ordinal);
            // Plain-text imports take exactly the same image-association path as rich notes.
            if (encoded)
            {
                var bytes = Convert.FromBase64String(raw);
                document = new FlowDocument();
                using var stream = new MemoryStream(bytes);
                new TextRange(document.ContentStart, document.ContentEnd).Load(stream,
                    bytes.Length > 1 && bytes[0] == 'P' && bytes[1] == 'K' ? DataFormats.XamlPackage : DataFormats.Rtf);
            }
            else
            {
                document = new FlowDocument();
                foreach (var line in raw.Split('\n'))
                    document.Blocks.Add(new Paragraph(new Run(line.TrimEnd('\r'))) { Margin = new Thickness(0) });
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A damaged encoded payload must never leak Base64/markup into the dashboard.
            document = new FlowDocument(new Paragraph());
            System.Diagnostics.Debug.WriteLine($"Note preview: {error.Message}");
        }

        document.FontFamily = SafeFont(note.FontFamily);
        document.FontSize = double.IsFinite(note.FontSize) ? Math.Clamp(note.FontSize, 1, 300) : 14;
        var width = ResolveWidth(note.PreviewLayoutWidth);
        return Complete(Capture(document, note.Images ?? [], null, width, wordWrap: note.IsWordWrapEnabled != false), hash);
    }

    internal static NotePreviewSnapshot Complete(NotePreviewSnapshot result, string hash)
    { result.SourceHash = hash; return result; }

    internal static double ResolveWidth(double? width)
        => width is double value && double.IsFinite(value) && value >= 100 ? Math.Min(value, 10000) : DefaultLayoutWidth;

    private static FontFamily SafeFont(string? family)
    {
        try { return new FontFamily(string.IsNullOrWhiteSpace(family) ? "Calibri" : family); }
        catch { return new FontFamily("Calibri"); }
    }

    internal static NotePreviewSnapshot Capture(FlowDocument document, IReadOnlyList<NoteImageAttachment> images,
        RichTextBox? editor, double width, Point scrollOffset = default, bool wordWrap = true)
    {
        var text = new StringBuilder(MaxCharacters + 8);
        var positions = new List<Position>();
        var used = new HashSet<Guid>();
        int rawOffset = 0, inlineIndex = 0;
        var inline = images.Where(i => i.WrapMode == NoteImageWrapMode.Inline).ToArray();
        var pointer = document.ContentStart;
        bool previousSpace = true;
        void Add(char ch, TextPointer at, NoteImageAttachment? image = null)
        {
            if (text.Length >= MaxCharacters) return;
            if (char.IsWhiteSpace(ch))
            {
                if (!previousSpace) { positions.Add(new(text.Length, at)); text.Append(' '); }
                previousSpace = true;
            }
            else
            {
                positions.Add(new(text.Length, at, image)); text.Append(ch); previousSpace = false;
            }
        }
        void Photo(TextPointer at, Guid? id = null)
        {
            var attachment = id is Guid known ? images.FirstOrDefault(i => i.Id == known)
                : inline.FirstOrDefault(i => !used.Contains(i.Id) && i.InlineAnchorOffset == rawOffset)
                    ?? inline.Skip(inlineIndex).FirstOrDefault(i => !used.Contains(i.Id));
            if (attachment is not null) { if (!used.Add(attachment.Id)) return; inlineIndex++; }
            Add(Marker, at, attachment);
        }
        while (pointer is not null && text.Length < MaxCharacters && pointer.CompareTo(document.ContentEnd) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var buffer = new char[MaxCharacters - text.Length];
                var count = pointer.GetTextInRun(LogicalDirection.Forward, buffer, 0, buffer.Length);
                var run = new string(buffer, 0, count);
                // Legacy text markers are an older representation of the same inline object.
                for (int i = 0; i < run.Length && text.Length < MaxCharacters; i++)
                {
                    if (char.IsWhiteSpace(run[i]) && previousSpace) { rawOffset++; continue; }
                    var at = pointer.GetPositionAtOffset(i, LogicalDirection.Forward)!;
                    if (run[i] == Marker) Photo(at);
                    else if (run.AsSpan(i).StartsWith("[[ScriblucentImage:", StringComparison.Ordinal)
                        && run.IndexOf("]]", i, StringComparison.Ordinal) is var end && end >= 0)
                    {
                        const string prefix = "[[ScriblucentImage:";
                        var payload = run.Substring(i + prefix.Length, end - i - prefix.Length);
                        Photo(at, Guid.TryParse(payload, out var id) ? id : null);
                        rawOffset += end + 2 - i; i = end + 1; continue;
                    }
                    else Add(run[i], at);
                    rawOffset++;
                }
                // Continue inside a long run when whitespace normalization left room in
                // the excerpt. Advancing to the run end would lose later text/photos.
                pointer = count == 0 ? pointer.GetNextContextPosition(LogicalDirection.Forward)
                    : pointer.GetPositionAtOffset(count, LogicalDirection.Forward);
                continue;
            }
            else if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.EmbeddedElement)
            {
                var element = pointer.GetAdjacentElement(LogicalDirection.Forward);
                if (element is InlineNoteImageElement current) { Photo(pointer, current.ImageId); rawOffset++; }
                else if (element is Image || element is Border { Tag: Guid }) { Photo(pointer); rawOffset++; }
                // Checkboxes and wrap spacers are UI containers too, but are not photographs.
            }
            else if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementEnd
                && pointer.Parent is Paragraph or ListItem or TableCell or BlockUIContainer)
                Add(' ', pointer);
            else if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart
                && pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak)
                Add(' ', pointer);
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
        { text.Length--; positions.RemoveAt(positions.Count - 1); }

        // Rich-text formatting can split an old marker across several runs.
        foreach (Match match in LegacyMarker.Matches(text.ToString()).Cast<Match>().Reverse())
        {
            var at = positions[match.Index].Pointer;
            var payload = match.Value["[[ScriblucentImage:".Length..^2];
            if (Guid.TryParse(payload, out var id)) used.Add(id);
            text.Remove(match.Index, match.Length).Insert(match.Index, Marker);
            positions.RemoveRange(match.Index, match.Length);
            positions.Insert(match.Index, new(match.Index, at, Guid.TryParse(payload, out var imageId) ? images.FirstOrDefault(i => i.Id == imageId) : null));
        }
        for (int i = 0; i < positions.Count; i++)
            if (positions[i].Offset != i) positions[i] = positions[i] with { Offset = i };

        var floating = images.Where(i => i.WrapMode != NoteImageWrapMode.Inline && !used.Contains(i.Id) && ValidGeometry(i)).DistinctBy(i => i.Id).ToArray();
        var hasText = text.ToString().Any(c => c != Marker && !char.IsWhiteSpace(c));
        var insertions = new List<(int Offset, NoteImageAttachment Image)>();
        if (!hasText)
        {
            foreach (var image in images.DistinctBy(i => i.Id))
                if (!used.Contains(image.Id) && (image.WrapMode == NoteImageWrapMode.Inline || ValidGeometry(image)))
                    insertions.Add((text.Length, image));
        }
        else if (floating.Length > 0)
        {
            var lines = new List<(int Offset, Rect Rect)>();
            if (editor is null || positions.Count == 0 || !positions[0].Pointer.HasValidLayout)
                lines = NotePreviewSourceLayout.Measure(document, text.ToString(), positions, images, width, wordWrap);
            else
            {
                TextPointer? nextLine = null;
                Paragraph? finishedParagraph = null;
                foreach (var position in positions)
                {
                    if (finishedParagraph is not null && ReferenceEquals(finishedParagraph, position.Pointer.Paragraph)) continue;
                    if (nextLine is not null && position.Pointer.CompareTo(nextLine) < 0) continue;
                    var rect = position.Pointer.GetCharacterRect(LogicalDirection.Forward);
                    if (rect.IsEmpty || rect.Height <= 0 || !double.IsFinite(rect.Top)) continue;
                    rect.Offset(scrollOffset.X - editor.Padding.Left, scrollOffset.Y - editor.Padding.Top);
                    lines.Add((position.Offset, rect));
                    nextLine = position.Pointer.GetLineStartPosition(1);
                    if (nextLine is null) finishedParagraph = position.Pointer.Paragraph;
                }
            }
            if (lines.Count > 0)
            {
                var excerptBottom = lines.Max(line => line.Rect.Bottom);
                foreach (var image in floating)
                {
                    if (image.Top >= excerptBottom || image.Top + image.Height <= 0
                        || image.Left >= width || image.Left + image.Width <= 0) continue;
                    var line = lines.FirstOrDefault(l => l.Rect.Bottom > image.Top);
                    insertions.Add((line.Rect.IsEmpty || line.Rect.Height == 0 ? text.Length : line.Offset, image));
                }
            }
        }
        foreach (var group in insertions.GroupBy(i => i.Offset).OrderByDescending(g => g.Key))
            text.Insert(group.Key, new string(Marker, group.Count()));
        return new NotePreviewSnapshot { Version = Version, Text = text.ToString().Trim(), LayoutWidth = width };
    }

    internal static bool ValidGeometry(NoteImageAttachment image)
        => double.IsFinite(image.Top) && double.IsFinite(image.Left) && double.IsFinite(image.Width)
            && double.IsFinite(image.Height) && image.Width > 0 && image.Height > 0;
}
