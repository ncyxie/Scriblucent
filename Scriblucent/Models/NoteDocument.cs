namespace Scriblucent.Models;

public class NoteDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? GroupId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<NoteImageAttachment> Images { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public string FontFamily { get; set; } = "Calibri";
    public double FontSize { get; set; } = 14;
    public bool? IsEditorFontPanelOpen { get; set; }
    public bool? IsWordWrapEnabled { get; set; }
    public DateTime LastModified { get; set; } = DateTime.Now;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ScheduledAt { get; set; }
    public string ScheduleNote { get; set; } = string.Empty;
    public List<NoteScheduleEntry> Schedules { get; set; } = new();
    public bool IsPinned { get; set; } = false;
    public List<NoteEditHistoryEntry> EditHistory { get; set; } = new();
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
}

/// <summary>
/// Floating image attachment stored with a note. Geometry is in document space (DIPs);
/// <see cref="Data"/> holds the original image bytes as base64.
/// </summary>
public class NoteImageAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Data { get; set; } = string.Empty;
    public double Width { get; set; }
    public double Height { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    /// <summary>When true, resize keeps aspect ratio. Missing on old notes → treated as true.</summary>
    public bool LockAspectRatio { get; set; } = true;
}

public class NoteScheduleEntry
{
    public DateTime ScheduledAt { get; set; }
    public string Note { get; set; } = string.Empty;
}

public class NoteEditHistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Content { get; set; } = string.Empty;
}
