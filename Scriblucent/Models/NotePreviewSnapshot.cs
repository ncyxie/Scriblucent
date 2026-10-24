using System.Text.Json.Serialization;

namespace Scriblucent.Models;

/// <summary>Derived card content. Image markers are positions, never image bytes.</summary>
public sealed class NotePreviewSnapshot
{
    public int Version { get; set; }
    public string SourceHash { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public double LayoutWidth { get; set; }

    [JsonIgnore]
    public IReadOnlyList<NotePreviewPart> Parts => _parts ??= BuildParts();
    private IReadOnlyList<NotePreviewPart>? _parts;

    private IReadOnlyList<NotePreviewPart> BuildParts()
    {
        var parts = new List<NotePreviewPart>();
        int start = 0;
        while (start < Text.Length)
        {
            var marker = Text.IndexOf('\uFFFC', start);
            if (marker < 0) { parts.Add(new(Text[start..], 0)); break; }
            if (marker > start) parts.Add(new(Text[start..marker], 0));
            int count = 1, end = marker + 1;
            while (end < Text.Length)
            {
                int next = end;
                while (next < Text.Length && char.IsWhiteSpace(Text[next])) next++;
                if (next == Text.Length || Text[next] != '\uFFFC') break;
                count++; end = next + 1;
            }
            parts.Add(new(string.Empty, count));
            start = end;
        }
        return parts;
    }
}

public readonly record struct NotePreviewPart(string Text, int ImageCount);
