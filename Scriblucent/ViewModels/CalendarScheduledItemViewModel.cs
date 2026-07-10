namespace Scriblucent.ViewModels;

public enum ScheduledItemType { Note, Flashcard, MindMap, Quiz }

public sealed class CalendarScheduledItemViewModel
{
    public required ScheduledItemType ItemType { get; init; }
    public required DateTime ScheduledAt { get; init; }
    public string ScheduleNote { get; init; } = string.Empty;
    public required string Title { get; init; }

    public string TimeText => ScheduledAt.ToString("HH:mm");

    public string TypeIcon => ItemType switch
    {
        ScheduledItemType.Note => "📝",
        ScheduledItemType.Flashcard => "🧠",
        ScheduledItemType.MindMap => "🗺",
        ScheduledItemType.Quiz => "☑",
        _ => "📄"
    };

    public NoteCardViewModel? Note { get; init; }
    public FlashcardSetViewModel? FlashcardSet { get; init; }
    public MindMapViewModel? MindMap { get; init; }
    public QuizViewModel? Quiz { get; init; }
}
