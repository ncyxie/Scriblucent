namespace Scriblucent.Models;

public sealed class MindMapDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public MindMapNode Root { get; set; } = new();
    public string LayoutMode { get; set; } = "BalancedTree";
    public bool UseManualPositions { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.Now;
    public string AiModelDisplayName { get; set; } = string.Empty;
    public Guid? SourceNoteId { get; set; }
    public Guid? GroupId { get; set; }
    public List<NoteScheduleEntry> Schedules { get; set; } = new();
    public bool IsPinned { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}
