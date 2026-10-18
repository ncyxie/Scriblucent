using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.ViewModels;

public sealed class QuizViewModel : ViewModelBase
{
    private bool _isSelectedInMassSelect;
    private string? _cachedTagsDisplay;
    private string? _cachedNextScheduleDisplay;

    public QuizViewModel(QuizDocument document)
    {
        Document = document;
    }

    public QuizDocument Document { get; }

    internal void NotifyTitleChanged() => OnPropertyChanged(nameof(Title));

    public string Title => string.IsNullOrWhiteSpace(Document.Title)
        ? LocalizationService.GetString("QuizUntitled")
        : Document.Title.Trim();

    public int QuestionCount => Document.Questions?.Count ?? 0;

    public string QuestionCountText => string.Format(LocalizationService.GetString("QuizQuestionCountFormat"), QuestionCount);

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
            "QuizGeneratedWithModel",
            Document.AiModelDisplayName,
            includeReviewDisclaimer: !Document.AiReviewNoticeDismissed)
        : string.Empty;

    public void NotifyChanged()
    {
        _cachedTagsDisplay = null;
        _cachedNextScheduleDisplay = null;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(QuestionCount));
        OnPropertyChanged(nameof(QuestionCountText));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(TagsDisplay));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(HasSchedule));
        OnPropertyChanged(nameof(NextScheduleDisplay));
        OnPropertyChanged(nameof(IsAiGenerated));
        OnPropertyChanged(nameof(ShowAiGeneratedChrome));
        OnPropertyChanged(nameof(AiGeneratedTooltip));
    }
}
