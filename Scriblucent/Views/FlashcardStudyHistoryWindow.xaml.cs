using Scriblucent.Localization;
using Scriblucent.Animations;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;

namespace Scriblucent.Views;

public partial class FlashcardStudyHistoryWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    public sealed class StudyDisplayItem
    {
        public string Title { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
        public string StatusText { get; init; } = string.Empty;
        /// <summary>Known | Unknown | Studied | Complete | InProgress — drives chip colors.</summary>
        public string StatusKind { get; init; } = "Studied";
        public bool IsComplete { get; init; }
    }

    private readonly MainViewModel? _mainViewModel;
    private readonly Func<FlashcardSetDocument>? _documentProvider;
    private readonly Action? _clearAction;
    private readonly BulkObservableCollection<StudyDisplayItem> _items = new();
    private int _totalCards;
    private int _knownCards;
    private int _unknownCards;
    private int _studiedCards;
    private int _refreshGeneration;
    private bool _isLoading;

    private sealed record SetSnapshot(string Title, FlashcardItem[] Cards, Guid[] History);
    private sealed record StudyResult(List<StudyDisplayItem> Items, int Total, int Known, int Unknown, int Studied);

    public FlashcardStudyHistoryWindow(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        DataContext = this;
        RefreshList();
        WindowContentEnterHelper.AnimateOnLoaded(this);
    }

    public FlashcardStudyHistoryWindow(Func<FlashcardSetDocument> documentProvider, Action clearAction)
    {
        _documentProvider = documentProvider;
        _clearAction = clearAction;
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        DataContext = this;
        RefreshList();
        WindowContentEnterHelper.AnimateOnLoaded(this);
    }

    public ObservableCollection<StudyDisplayItem> Items => _items;
    public bool HasItems => _items.Count > 0;
    public bool IsLoading => _isLoading;
    public bool ShowEmpty => !_isLoading && !HasItems;
    public string SummaryText => _isLoading
        ? LocalizationService.GetString("MotionStateLoading")
        : _totalCards == 0
        ? LocalizationService.GetString("WorkspaceHistoryStudyEmpty")
        : string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("WorkspaceHistoryStudySummary"), _studiedCards, _knownCards, _unknownCards, _totalCards);
    public string KnownText => _totalCards == 0 ? "-" : string.Format(CultureInfo.CurrentCulture, "{0}/{1}", _knownCards, _totalCards);
    public string UnknownText => _totalCards == 0 ? "-" : string.Format(CultureInfo.CurrentCulture, "{0}", _unknownCards);
    public string StudiedText => _totalCards == 0 ? "-" : string.Format(CultureInfo.CurrentCulture, "{0}", _studiedCards);
    public string ClearButtonText => _documentProvider is null ? LocalizationService.GetString("WorkspaceHistoryClear") : LocalizationService.GetString("WorkspaceHistoryClearSet");

    private async void RefreshList()
    {
        var generation = ++_refreshGeneration;
        var snapshots = _documentProvider is not null
            ? new[] { Snapshot(_documentProvider()) }
            : _mainViewModel?.FlashcardSets
                .Where(set => set.Document.Cards is { Count: > 0 })
                .Select(set => Snapshot(set.Document)).ToArray() ?? Array.Empty<SetSnapshot>();
        var isSingle = _documentProvider is not null;
        var count = snapshots.Sum(snapshot => snapshot.Cards.Length);

        StudyResult result;
        if (count >= 10_000 || snapshots.Length >= 10_000)
        {
            _isLoading = true;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(SummaryText));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
            result = await Task.Run(() => isSingle
                ? BuildSingleSetItems(snapshots[0])
                : BuildGlobalSetItems(snapshots));
            if (generation != _refreshGeneration || !IsVisible)
                return;
        }
        else
        {
            result = isSingle ? BuildSingleSetItems(snapshots[0]) : BuildGlobalSetItems(snapshots);
        }

        _totalCards = result.Total;
        _knownCards = result.Known;
        _unknownCards = result.Unknown;
        _studiedCards = result.Studied;
        _items.ReplaceAll(result.Items);
        _isLoading = false;

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(KnownText));
        OnPropertyChanged(nameof(UnknownText));
        OnPropertyChanged(nameof(StudiedText));
    }

    private static SetSnapshot Snapshot(FlashcardSetDocument document)
        => new(document.Title, (document.Cards ?? new List<FlashcardItem>()).ToArray(),
            (document.StudySession?.History ?? new List<Guid>()).ToArray());

    private static StudyResult BuildGlobalSetItems(SetSnapshot[] snapshots)
    {
        var items = new List<StudyDisplayItem>();
        var totalCards = 0;
        var knownCards = 0;
        var unknownCards = 0;
        var studiedCards = 0;
        foreach (var document in snapshots.OrderByDescending(GetKnownRatio).ThenBy(document => document.Title))
        {
            var cards = document.Cards;
            var historyIds = document.History.ToHashSet();
            var known = cards.Count(card => card.IsKnown);
            var unknown = cards.Count(card => card.IsUnknown);
            var studied = cards.Count(card => historyIds.Contains(card.Id) || card.IsKnown || card.IsUnknown);

            totalCards += cards.Length;
            knownCards += known;
            unknownCards += unknown;
            studiedCards += studied;

            if (cards.Length == 0 && studied == 0)
                continue;

            var isComplete = cards.Length > 0 && known == cards.Length;
            items.Add(new StudyDisplayItem
            {
                Title = string.IsNullOrWhiteSpace(document.Title) ? "Untitled flashcards" : document.Title.Trim(),
                Details = string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("WorkspaceHistoryStudyDetails"), studied, known, unknown, cards.Length),
                StatusText = LocalizationService.GetString(isComplete ? "WorkspaceHistoryComplete" : "WorkspaceHistoryInProgress"),
                StatusKind = isComplete ? "Complete" : "InProgress",
                IsComplete = isComplete
            });
        }
        return new StudyResult(items, totalCards, knownCards, unknownCards, studiedCards);
    }

    private static StudyResult BuildSingleSetItems(SetSnapshot document)
    {
        var cards = document.Cards;
        var historyIds = document.History.ToHashSet();

        var knownCards = cards.Count(card => card.IsKnown);
        var unknownCards = cards.Count(card => card.IsUnknown);
        var studiedCards = cards.Count(card => historyIds.Contains(card.Id) || card.IsKnown || card.IsUnknown);
        var items = new List<StudyDisplayItem>();

        foreach (var card in cards.OrderBy(card => Math.Max(1, card.SetIndex)).ThenBy(card => card.Question))
        {
            var wasStudied = historyIds.Contains(card.Id);
            if (!wasStudied && !card.IsKnown && !card.IsUnknown)
                continue;

            var statusKind = card.IsKnown ? "Known" : card.IsUnknown ? "Unknown" : "Studied";
            items.Add(new StudyDisplayItem
            {
                Title = string.IsNullOrWhiteSpace(card.Question) ? "Untitled card" : card.Question.Trim(),
                Details = string.IsNullOrWhiteSpace(card.Category)
                    ? string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("WorkspaceHistorySet"), Math.Max(1, card.SetIndex))
                    : string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("WorkspaceHistorySetCategory"), Math.Max(1, card.SetIndex), card.Category.Trim()),
                StatusText = LocalizationService.GetString("WorkspaceHistory" + statusKind),
                StatusKind = statusKind,
                IsComplete = card.IsKnown
            });
        }
        return new StudyResult(items, cards.Length, knownCards, unknownCards, studiedCards);
    }

    private static double GetKnownRatio(SetSnapshot document)
    {
        var cards = document.Cards;
        return cards.Length == 0 ? 0 : cards.Count(card => card.IsKnown) / (double)cards.Length;
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasItems)
            return;

        var result = ModernMessageBox.Show(
            _documentProvider is null ? "Clear all flashcard study history?" : "Clear this flashcard set history?",
            "Clear History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        if (_clearAction is not null)
        {
            _clearAction();
            RefreshList();
            return;
        }

        if (_mainViewModel is null)
            return;

        foreach (var set in _mainViewModel.FlashcardSets)
        {
            ClearDocumentProgress(set.Document);
            set.NotifyChanged();
        }

        _mainViewModel.SaveFlashcardSets();
        RefreshList();
    }

    public static void ClearDocumentProgress(FlashcardSetDocument document)
    {
        foreach (var card in document.Cards ?? new List<FlashcardItem>())
        {
            card.IsKnown = false;
            card.IsUnknown = false;
            card.EaseFactor = 2.5;
            card.IntervalDays = 0;
            card.Repetitions = 0;
            card.DueDate = null;
            card.LastReviewedAt = null;
            card.Stability = 0;
            card.Difficulty = 0;
        }

        document.StudySession = new FlashcardStudySession();
        document.LastModified = DateTime.Now;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}
