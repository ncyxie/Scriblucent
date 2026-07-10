namespace Scriblucent.Models;

public sealed class FlashcardSetDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public Dictionary<int, string> SetNames { get; set; } = new();
    public List<FlashcardItem> Cards { get; set; } = new();
    public FlashcardStudySession StudySession { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.Now;
    public string AiModelDisplayName { get; set; } = string.Empty;
    public Guid? SourceNoteId { get; set; }
    public Guid? GroupId { get; set; }
    public List<NoteScheduleEntry> Schedules { get; set; } = new();
    public bool IsPinned { get; set; }
}
