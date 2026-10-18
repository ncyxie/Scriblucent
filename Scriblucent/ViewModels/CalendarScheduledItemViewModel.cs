using Scriblucent.Icons;

namespace Scriblucent.ViewModels;

public enum ScheduledItemType { Note, Flashcard, MindMap, Quiz }

public sealed class CalendarScheduledItemViewModel
{
    public required ScheduledItemType ItemType { get; init; }
    public required DateTime ScheduledAt { get; init; }
    public string ScheduleNote { get; init; } = string.Empty;
    public required string Title { get; init; }

    public Scriblucent.Models.NoteScheduleEntry? SourceEntry { get; init; }
    public bool IsDeleted => Note?.Document.IsDeleted ?? FlashcardSet?.Document.IsDeleted ?? MindMap?.Document.IsDeleted ?? Quiz?.Document.IsDeleted ?? true;
    public Guid OwnerId => Note?.Document.Id ?? FlashcardSet?.Document.Id ?? MindMap?.Document.Id ?? Quiz?.Document.Id ?? Guid.Empty;
    public string TypeText => Scriblucent.Localization.LocalizationService.GetString(ItemType switch
    { ScheduledItemType.Note => "NotesDashboard", ScheduledItemType.Flashcard => "FlashcardsDashboard", ScheduledItemType.MindMap => "MindMapsDashboard", _ => "QuizzesDashboard" });
    public string OwnerDescription => TypeText + " \u00b7 " + (Note?.Document.LastModified ?? FlashcardSet?.Document.LastModified ?? MindMap?.Document.LastModified ?? Quiz?.Document.LastModified ?? ScheduledAt).ToString("g");
    public bool HasDescription => !string.IsNullOrWhiteSpace(ScheduleNote);
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
