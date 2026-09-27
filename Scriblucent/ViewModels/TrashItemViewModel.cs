using Scriblucent.Icons;
using Scriblucent.Localization;
using System;

namespace Scriblucent.ViewModels;

public enum TrashItemKind
{
    Note,
    FlashcardSet,
    MindMap,
    Quiz
}

public sealed class TrashItemViewModel : ViewModelBase
{
    private bool _isExcludedFromLogicalSelection;

    public bool IsExcludedFromLogicalSelection
    {
        get => _isExcludedFromLogicalSelection;
        set => SetProperty(ref _isExcludedFromLogicalSelection, value);
    }

    public TrashItemViewModel(
        TrashItemKind kind,
        Guid id,
        string title,
        DateTime? deletedAtUtc,
        object source,
        int retentionDays)
    {
        Kind = kind;
        Id = id;
        Title = string.IsNullOrWhiteSpace(title)
            ? LocalizationService.GetString("Untitled")
            : title;
        DeletedAtUtc = deletedAtUtc ?? DateTime.UtcNow;
        Source = source;
        RetentionDays = retentionDays;
    }

    public TrashItemKind Kind { get; }
    public Guid Id { get; }
    public string Title { get; }
    public DateTime DeletedAtUtc { get; }
    public object Source { get; }
    public int RetentionDays { get; }

    public DateTime DeletedAtLocal => DeletedAtUtc.ToLocalTime();

    public int DaysLeft
    {
        get
        {
            var expiresAt = DeletedAtUtc.AddDays(RetentionDays);
            var days = (int)Math.Ceiling((expiresAt - DateTime.UtcNow).TotalDays);
            return Math.Max(0, days);
        }
    }

    public string DeletedAtDisplay => DeletedAtLocal.ToString("g");

    public string DaysLeftDisplay => DaysLeft <= 0
        ? LocalizationService.GetString("TrashExpiresToday")
        : string.Format(LocalizationService.GetString("TrashDaysLeftFormat"), DaysLeft);

    public string KindDisplayName => Kind switch
    {
        TrashItemKind.Note => LocalizationService.GetString("NotesDashboard"),
        TrashItemKind.FlashcardSet => LocalizationService.GetString("FlashcardsDashboard"),
        TrashItemKind.MindMap => LocalizationService.GetString("MindMapsDashboard"),
        TrashItemKind.Quiz => LocalizationService.GetString("QuizzesDashboard"),
        _ => Kind.ToString()
    };

    public AppIconKind KindIcon => Kind switch
    {
        TrashItemKind.Note => AppIconKind.Notes,
        TrashItemKind.FlashcardSet => AppIconKind.Flashcards,
        TrashItemKind.MindMap => AppIconKind.MindMaps,
        TrashItemKind.Quiz => AppIconKind.Quizzes,
        _ => AppIconKind.Trash
    };
}
