using Scriblucent.Localization;
using Scriblucent.Models;

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

    public string AiGeneratedTooltip => IsAiGenerated
        ? string.Format(LocalizationService.GetString("FlashcardsGeneratedWithModel"), Document.AiModelDisplayName.Trim())
        : string.Empty;

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
        OnPropertyChanged(nameof(AiGeneratedTooltip));
    }
}
