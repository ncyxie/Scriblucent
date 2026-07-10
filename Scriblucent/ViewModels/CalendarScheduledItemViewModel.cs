using Scriblucent.Icons;

namespace Scriblucent.ViewModels;

public enum ScheduledItemType { Note, Flashcard, MindMap, Quiz }

public sealed class CalendarScheduledItemViewModel
{
    public required ScheduledItemType ItemType { get; init; }
    public required DateTime ScheduledAt { get; init; }
    public string ScheduleNote { get; init; } = string.Empty;
    public required string Title { get; init; }

    public string TimeText => ScheduledAt.ToString("HH:mm");

    public AppIconKind TypeIconKind => ItemType switch
    {
        ScheduledItemType.Note => AppIconKind.Notes,
        ScheduledItemType.Flashcard => AppIconKind.Flashcards,
        ScheduledItemType.MindMap => AppIconKind.MindMaps,
        ScheduledItemType.Quiz => AppIconKind.Quizzes,
        _ => AppIconKind.Notes
    };

    public NoteCardViewModel? Note { get; init; }
    public FlashcardSetViewModel? FlashcardSet { get; init; }
    public MindMapViewModel? MindMap { get; init; }
    public QuizViewModel? Quiz { get; init; }
}
