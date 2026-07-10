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
}

public static class NoteImageLayout
{
    public const string Inline = "Inline";
    public const string Floating = "Floating";
}

public class NoteImageAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Data { get; set; } = string.Empty;
    public string Layout { get; set; } = NoteImageLayout.Floating;
    public double Width { get; set; }
    public double Height { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public bool PreserveAspectRatio { get; set; }
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
    public List<NoteImageAttachment> Images { get; set; } = new();
}
