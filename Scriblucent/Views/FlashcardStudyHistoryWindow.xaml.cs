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
    private readonly ObservableCollection<StudyDisplayItem> _items = new();
    private int _totalCards;
    private int _knownCards;
    private int _unknownCards;
    private int _studiedCards;

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
    public string SummaryText => _totalCards == 0
        ? "No flashcard study progress yet"
        : string.Format(CultureInfo.CurrentCulture, "{0} studied, {1} known, {2} unknown, {3} total cards", _studiedCards, _knownCards, _unknownCards, _totalCards);
    public string KnownText => _totalCards == 0 ? "-" : string.Format(CultureInfo.CurrentCulture, "{0}/{1}", _knownCards, _totalCards);
    public string UnknownText => _totalCards == 0 ? "-" : string.Format(CultureInfo.CurrentCulture, "{0}", _unknownCards);
    public string StudiedText => _totalCards == 0 ? "-" : string.Format(CultureInfo.CurrentCulture, "{0}", _studiedCards);
    public string ClearButtonText => _documentProvider is null ? "Clear history" : "Clear this set history";

    private void RefreshList()
    {
        _items.Clear();
        _totalCards = 0;
        _knownCards = 0;
        _unknownCards = 0;
        _studiedCards = 0;

        if (_documentProvider is not null)
            AddSingleSetItems(_documentProvider());
        else if (_mainViewModel is not null)
            AddGlobalSetItems(_mainViewModel.FlashcardSets.Select(set => set.Document));

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(KnownText));
        OnPropertyChanged(nameof(UnknownText));
        OnPropertyChanged(nameof(StudiedText));
    }

    private void AddGlobalSetItems(IEnumerable<FlashcardSetDocument> documents)
    {
        foreach (var document in documents.OrderByDescending(GetKnownRatio).ThenBy(document => document.Title))
        {
            var cards = document.Cards ?? new List<FlashcardItem>();
            var historyIds = (document.StudySession?.History ?? new List<Guid>()).Distinct().ToHashSet();
            var known = cards.Count(card => card.IsKnown);
            var unknown = cards.Count(card => card.IsUnknown);
            var studied = cards.Count(card => historyIds.Contains(card.Id) || card.IsKnown || card.IsUnknown);

            _totalCards += cards.Count;
            _knownCards += known;
            _unknownCards += unknown;
            _studiedCards += studied;

            if (cards.Count == 0 && studied == 0)
                continue;

            var isComplete = cards.Count > 0 && known == cards.Count;
            _items.Add(new StudyDisplayItem
            {
                Title = string.IsNullOrWhiteSpace(document.Title) ? "Untitled flashcards" : document.Title.Trim(),
                Details = string.Format(CultureInfo.CurrentCulture, "{0} studied - {1} known - {2} unknown - {3} cards", studied, known, unknown, cards.Count),
                StatusText = isComplete ? "Complete" : "In progress",
                StatusKind = isComplete ? "Complete" : "InProgress",
                IsComplete = isComplete
            });
        }
    }

    private void AddSingleSetItems(FlashcardSetDocument document)
    {
        var cards = document.Cards ?? new List<FlashcardItem>();
        var historyIds = (document.StudySession?.History ?? new List<Guid>()).Distinct().ToHashSet();

        _totalCards = cards.Count;
        _knownCards = cards.Count(card => card.IsKnown);
        _unknownCards = cards.Count(card => card.IsUnknown);
        _studiedCards = cards.Count(card => historyIds.Contains(card.Id) || card.IsKnown || card.IsUnknown);

        foreach (var card in cards.OrderBy(card => Math.Max(1, card.SetIndex)).ThenBy(card => card.Question))
        {
            var wasStudied = historyIds.Contains(card.Id);
            if (!wasStudied && !card.IsKnown && !card.IsUnknown)
                continue;

            var statusKind = card.IsKnown ? "Known" : card.IsUnknown ? "Unknown" : "Studied";
            _items.Add(new StudyDisplayItem
            {
                Title = string.IsNullOrWhiteSpace(card.Question) ? "Untitled card" : card.Question.Trim(),
                Details = string.IsNullOrWhiteSpace(card.Category)
                    ? string.Format(CultureInfo.CurrentCulture, "Set {0}", Math.Max(1, card.SetIndex))
                    : string.Format(CultureInfo.CurrentCulture, "Set {0} - {1}", Math.Max(1, card.SetIndex), card.Category.Trim()),
                StatusText = statusKind,
                StatusKind = statusKind,
                IsComplete = card.IsKnown
            });
        }
    }

    private static double GetKnownRatio(FlashcardSetDocument document)
    {
        var cards = document.Cards ?? new List<FlashcardItem>();
        return cards.Count == 0 ? 0 : cards.Count(card => card.IsKnown) / (double)cards.Count;
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
