using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.ViewModels;

public sealed class FlashcardSetViewModel : ViewModelBase
{
    private bool _isSelectedInMassSelect;
    private int? _cachedSetCount;
    private string? _cachedTagsDisplay;
    private string? _cachedNextScheduleDisplay;

    public FlashcardSetViewModel(FlashcardSetDocument document)
    {
        Document = document;
    }

    public FlashcardSetDocument Document { get; }

    public string Title => string.IsNullOrWhiteSpace(Document.Title)
        ? LocalizationService.GetString("FlashcardSetUntitled")
        : Document.Title.Trim();

    public int CardCount => Document.Cards?.Count ?? 0;

    public string CardCountText => string.Format(LocalizationService.GetString("FlashcardCardsCountFormat"), CardCount);

    public int SetCount => _cachedSetCount ??= Document.Cards?
        .Select(card => Math.Max(1, card.SetIndex))
        .Distinct()
        .Count() ?? 0;

    public string SetCountText => string.Format(LocalizationService.GetString("FlashcardSetCountFormat"), SetCount);

    public bool HasTags => Document.Tags?.Any(tag => !string.IsNullOrWhiteSpace(tag)) == true;

    public string TagsDisplay => _cachedTagsDisplay ??= HasTags
        ? string.Join("   ", Document.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => $"#{tag.Trim()}"))
        : string.Empty;

    public bool IsPinned => Document.IsPinned;

    public bool IsSelectedInMassSelect
    {
        get => _isSelectedInMassSelect;
        set => SetProperty(ref _isSelectedInMassSelect, value);
    }

    public bool HasSchedule => Document.Schedules?.Any() == true;

    public string NextScheduleDisplay => _cachedNextScheduleDisplay ??= HasSchedule
        ? Document.Schedules
            .OrderBy(schedule => schedule.ScheduledAt)
            .First()
            .ScheduledAt.ToString("yyyy-MM-dd HH:mm")
        : string.Empty;

    public bool IsAiGenerated => !string.IsNullOrWhiteSpace(Document.AiModelDisplayName);

    public bool ShowAiGeneratedChrome => IsAiGenerated && AiFeatureGate.IsEnabled;

    public string AiGeneratedTooltip => IsAiGenerated
        ? LocalizationService.FormatAiGeneratedTooltip(
            "FlashcardsGeneratedWithModel",
            Document.AiModelDisplayName,
            includeReviewDisclaimer: !Document.AiReviewNoticeDismissed)
        : string.Empty;

    /// <summary>How many cards are due right now - algorithm-agnostic, since IsDue only reads
    /// DueDate, which SM-2 and FSRS populate identically. This is the dashboard's only standing
    /// signal for "is there anything to study" - finishing a session used to leave no way to tell
    /// when to come back short of opening the set or reading a one-time completion popup.</summary>
    public int DueCount => Document.Cards?.Count(card => FlashcardReviewScheduler.IsDue(card.DueDate, DateTime.Now)) ?? 0;

    public bool HasDueCards => DueCount > 0;

    /// <summary>"{n} due" when something needs review now, otherwise "Next review: ..." from the
    /// soonest upcoming DueDate among cards that have one - null only for a set with nothing ever
    /// scheduled (e.g. no cards at all).</summary>
    public string? DueStatusText
    {
        get
        {
            if (DueCount > 0)
                return string.Format(LocalizationService.GetString("FlashcardDueCountFormat"), DueCount);

            var upcomingDueDates = Document.Cards?
                .Where(card => card.DueDate.HasValue)
                .Select(card => card.DueDate!.Value)
                .ToList();

            if (upcomingDueDates is not { Count: > 0 })
                return null;

            var days = (upcomingDueDates.Min().Date - DateTime.Now.Date).Days;
            return days <= 1
                ? LocalizationService.GetString("FlashcardNextReviewTomorrow")
                : string.Format(LocalizationService.GetString("FlashcardNextReviewInDaysFormat"), days);
        }
    }

    public bool ShowNextReviewText => !HasDueCards && !string.IsNullOrEmpty(DueStatusText);

    public void NotifyChanged()
    {
        _cachedSetCount = null;
        _cachedTagsDisplay = null;
        _cachedNextScheduleDisplay = null;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CardCount));
        OnPropertyChanged(nameof(CardCountText));
        OnPropertyChanged(nameof(SetCount));
        OnPropertyChanged(nameof(SetCountText));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(TagsDisplay));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(HasSchedule));
        OnPropertyChanged(nameof(NextScheduleDisplay));
        OnPropertyChanged(nameof(IsAiGenerated));
        OnPropertyChanged(nameof(ShowAiGeneratedChrome));
        OnPropertyChanged(nameof(AiGeneratedTooltip));
        OnPropertyChanged(nameof(DueCount));
        OnPropertyChanged(nameof(HasDueCards));
        OnPropertyChanged(nameof(DueStatusText));
        OnPropertyChanged(nameof(ShowNextReviewText));
    }
}
