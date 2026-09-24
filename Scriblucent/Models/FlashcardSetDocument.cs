namespace Scriblucent.Models;

public sealed class FlashcardSetDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public bool? IsEditorTagsPanelOpen { get; set; }
    public Dictionary<int, string> SetNames { get; set; } = new();
    public List<FlashcardItem> Cards { get; set; } = new();
    public FlashcardStudySession StudySession { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.Now;
    public string AiModelDisplayName { get; set; } = string.Empty;
    /// <summary>When true, this set's editor no longer shows the AI review warning.</summary>
    public bool AiReviewNoticeDismissed { get; set; }
    public Guid? SourceNoteId { get; set; }
    public Guid? GroupId { get; set; }
    public List<NoteScheduleEntry> Schedules { get; set; } = new();
    public bool IsPinned { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Preferred flip animation duration for this set (ms). 0 = fall back to app settings.
    /// </summary>
    public int FlipDelayMilliseconds { get; set; }

    /// <summary>Study mode defaults to only showing cards that are due per the active scheduler.</summary>
    public bool StudyDueCardsOnly { get; set; } = true;
}
