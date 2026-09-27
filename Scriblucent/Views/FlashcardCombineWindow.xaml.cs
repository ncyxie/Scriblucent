using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Scriblucent.Views;

public partial class FlashcardCombineWindow : Window, INotifyPropertyChanged
{
    public sealed class FlashcardCombineItem : ViewModelBase
    {
        private bool _isSelected;

        public FlashcardCombineItem(FlashcardSetViewModel model)
        {
            Model = model;
        }

        public FlashcardSetViewModel Model { get; }
        public FlashcardSetDocument Document => Model.Document;
        public string Title => Model.Title;
        public string CardCountText => Model.CardCountText;
        public string TagsDisplay => Model.TagsDisplay;
        public bool HasTags => Model.HasTags;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

    }

    private readonly MainViewModel _mainViewModel;
    private readonly BulkObservableCollection<FlashcardCombineItem> _sets = new();
    private readonly BulkObservableCollection<FlashcardCombineItem> _visibleSets = new();
    private readonly BulkObservableCollection<FlashcardCombineItem> _selectedSets = new();
    private readonly BulkObservableCollection<TagFilterItemViewModel> _categoryFilters = new();
    private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private string _searchText = string.Empty;
    private string _combinedTitle = "Combined flashcards";
    private string _statusText = string.Empty;
    private bool _skipDuplicateCards = true;
    private bool _shuffleCards = true;
    private int _filterGeneration;
    private readonly Dictionary<string, int> _selectedCardSignatures = new(StringComparer.OrdinalIgnoreCase);
    private int _selectedCardCount;
    private bool _isClearingSelection;
    private bool _isLoading;
    private bool _isClosed;

    public FlashcardCombineWindow(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        DataContext = this;

        LoadSets();

        LoadCategories();
        UpdateFilteredVisibility();
        UpdateStatusText();
        _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
        WindowContentEnterHelper.AnimateOnLoaded(this);
        Closed += FlashcardCombineWindow_Closed;
    }

    private void FlashcardCombineWindow_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _filterGeneration++;
        Closed -= FlashcardCombineWindow_Closed;
        _mainViewModel.PropertyChanged -= MainViewModel_PropertyChanged;
        foreach (var item in _sets)
            item.PropertyChanged -= Item_PropertyChanged;
    }

    private async void LoadSets()
    {
        var source = _mainViewModel.FlashcardSets.ToArray();
        var items = new List<FlashcardCombineItem>(source.Length);
        if (source.Length >= 10_000)
        {
            _isLoading = true;
            StatusText = "Loading flashcard sets...";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
        }

        var sliceTimer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var set in source)
        {
            if (_isClosed)
            {
                foreach (var createdItem in items)
                    createdItem.PropertyChanged -= Item_PropertyChanged;
                return;
            }

            var item = new FlashcardCombineItem(set);
            item.PropertyChanged += Item_PropertyChanged;
            items.Add(item);
            if (_isLoading && sliceTimer.ElapsedMilliseconds >= 4)
            {
                sliceTimer.Restart();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        _sets.AddRange(items);
        _isLoading = false;
        UpdateFilteredVisibility();
        UpdateStatusText();
    }

    public ObservableCollection<FlashcardCombineItem> Sets => _sets;
    public ObservableCollection<FlashcardCombineItem> VisibleSets => _visibleSets;
    public ObservableCollection<FlashcardCombineItem> SelectedSets => _selectedSets;
    public ObservableCollection<TagFilterItemViewModel> CategoryFilters => _categoryFilters;
    public string FilteredCountText => string.Format(CultureInfo.CurrentCulture, "{0} sets", _visibleSets.Count);
    public string SelectedCountText => string.Format(CultureInfo.CurrentCulture, "{0} selected", _selectedSets.Count);
    public string SelectedCardCountText => string.Format(CultureInfo.CurrentCulture, "{0} cards in combined set", GetCombinedCardCount());
    public bool HasSelectedSets => _selectedSets.Count > 0;
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
                return;

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
                return;

            _searchText = value ?? string.Empty;
            UpdateFilteredVisibility();
            OnPropertyChanged();
        }
    }

    public string CombinedTitle
    {
        get => _combinedTitle;
        set
        {
            if (_combinedTitle == value)
                return;

            _combinedTitle = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public bool SkipDuplicateCards
    {
        get => _skipDuplicateCards;
        set
        {
            if (_skipDuplicateCards == value)
                return;

            _skipDuplicateCards = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedCardCountText));
        }
    }

    public bool ShuffleCards
    {
        get => _shuffleCards;
        set
        {
            if (_shuffleCards == value)
                return;

            _shuffleCards = value;
            OnPropertyChanged();
        }
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlashcardCombineItem.IsSelected)
            && !_isClearingSelection
            && sender is FlashcardCombineItem item)
        {
            if (item.IsSelected)
            {
                UpdateSelectedCardCounts(item, 1);
                _selectedSets.Add(item);
            }
            else
            {
                UpdateSelectedCardCounts(item, -1);
                _selectedSets.Remove(item);
            }
            OnPropertyChanged(nameof(SelectedCountText));
            OnPropertyChanged(nameof(SelectedCardCountText));
            OnPropertyChanged(nameof(HasSelectedSets));
        }
    }

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FlashcardTagFilters)
            || e.PropertyName == nameof(MainViewModel.ActiveTagFilters)
            || e.PropertyName == nameof(MainViewModel.ActiveTagFilterButtonText))
        {
            LoadCategories();
            UpdateFilteredVisibility();
            UpdateStatusText();
        }
    }

    private void LoadCategories()
    {
        var filters = new List<TagFilterItemViewModel>();
        foreach (var filter in _mainViewModel.FlashcardTagFilters)
        {
            var existing = _selectedTags.Contains(filter.Tag);
            filters.Add(new TagFilterItemViewModel(filter.Tag, existing, SetTagSelected));
        }
        _categoryFilters.ReplaceAll(filters);
    }

    private void SetTagSelected(string tag, bool isSelected)
    {
        if (isSelected)
            _selectedTags.Add(tag);
        else
            _selectedTags.Remove(tag);

        UpdateFilteredVisibility();
        UpdateStatusText();
    }

    private async void UpdateFilteredVisibility()
    {
        var generation = ++_filterGeneration;
        var tags = _selectedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var search = _searchText;
        if (tags.Count == 0 && string.IsNullOrWhiteSpace(search))
        {
            _visibleSets.ReplaceAll(_sets);
            OnPropertyChanged(nameof(FilteredCountText));
            UpdateStatusText();
            return;
        }

        var visible = new List<FlashcardCombineItem>();
        var sliceTimer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var set in _sets)
        {
            if (generation != _filterGeneration)
                return;
            if (MatchesFilter(set, tags, search))
                visible.Add(set);
            if (sliceTimer.ElapsedMilliseconds >= 4)
            {
                sliceTimer.Restart();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        if (generation != _filterGeneration)
            return;
        _visibleSets.ReplaceAll(visible);

        OnPropertyChanged(nameof(FilteredCountText));
        UpdateStatusText();
    }

    private static bool MatchesFilter(FlashcardCombineItem set, HashSet<string> selectedTags, string search)
    {
        if (selectedTags.Count > 0)
        {
            var tags = set.Document.Tags ?? [];
            if (!tags.Any(selectedTags.Contains))
                return false;
        }

        if (string.IsNullOrWhiteSpace(search))
            return true;

        return set.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
               || set.TagsDisplay.Contains(search, StringComparison.OrdinalIgnoreCase)
               || set.Document.Cards.Any(card =>
                   (card.Question ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
                   || (card.Answer ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
                   || (card.Category ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateStatusText()
    {
        if (_isLoading)
            return;

        var visibleCount = _visibleSets.Count;
        StatusText = string.Format(CultureInfo.CurrentCulture, "{0} of {1} sets shown", visibleCount, _sets.Count);
    }

    private int GetCombinedCardCount()
        => SkipDuplicateCards ? _selectedCardSignatures.Count : _selectedCardCount;

    private void UpdateSelectedCardCounts(FlashcardCombineItem item, int direction)
    {
        foreach (var card in item.Document.Cards ?? [])
        {
            _selectedCardCount += direction;
            var signature = BuildCardSignature(card);
            _selectedCardSignatures.TryGetValue(signature, out var count);
            if (count + direction == 0)
                _selectedCardSignatures.Remove(signature);
            else
                _selectedCardSignatures[signature] = count + direction;
        }
    }

    private void SetItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: FlashcardCombineItem item })
            item.IsSelected = !item.IsSelected;
    }

    private void SelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        _isClearingSelection = true;
        try
        {
            foreach (var set in _selectedSets)
                set.IsSelected = false;
        }
        finally
        {
            _isClearingSelection = false;
        }

        _selectedSets.ReplaceAll([]);
        _selectedCardSignatures.Clear();
        _selectedCardCount = 0;
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SelectedCardCountText));
        OnPropertyChanged(nameof(HasSelectedSets));
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedTags.Clear();
        foreach (var filter in _categoryFilters)
            filter.IsSelected = false;

        SearchText = string.Empty;
        UpdateFilteredVisibility();
        UpdateStatusText();
    }

    private void CreateCombinedButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedSets = _selectedSets.Select(set => set.Model).ToList();
        if (selectedSets.Count == 0)
        {
            ModernMessageBox.Show("Select at least one flashcard set first.", "Combine flashcards", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var document = BuildCombinedDocument(selectedSets);
        var mainWindow = Application.Current.MainWindow as MainWindow;
        var editor = new FlashcardsPreviewWindow(
            document.Cards,
            _mainViewModel.Notes.Select(note => new FlashcardsPreviewWindow.FlashcardNoteLinkOption(note.Document.Id, note.Document.Title)),
            document.AiModelDisplayName,
            document.Title,
            document.Tags,
            document.SetNames,
            document.StudySession,
            openNoteAction: mainWindow is not null
                ? noteId => mainWindow.OpenNoteById(_mainViewModel, noteId)
                : null)
        {
            Owner = this
        };

        if (editor.ShowDialog() == true)
            _mainViewModel.AddOrUpdateFlashcardSet(editor.ToDocument());
    }

    private FlashcardSetDocument BuildCombinedDocument(IReadOnlyList<FlashcardSetViewModel> selectedSets)
    {
        var cards = new List<FlashcardItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var setNames = new Dictionary<int, string>();
        var setIndex = 1;

        foreach (var set in selectedSets)
        {
            setNames[setIndex] = set.Title;
            foreach (var card in set.Document.Cards ?? [])
            {
                if (SkipDuplicateCards && !seen.Add(BuildCardSignature(card)))
                    continue;

                cards.Add(new FlashcardItem
                {
                    Id = Guid.NewGuid(),
                    Question = card.Question,
                    Answer = card.Answer,
                    Category = card.Category,
                    Hint = card.Hint,
                    SetIndex = setIndex,
                    IsStarred = card.IsStarred,
                    LinkedNoteId = card.LinkedNoteId,
                    QuestionImageFileName = card.QuestionImageFileName,
                    QuestionAudioFileName = card.QuestionAudioFileName,
                    AnswerImageFileName = card.AnswerImageFileName,
                    AnswerAudioFileName = card.AnswerAudioFileName
                });
            }

            setIndex++;
        }

        if (ShuffleCards)
            Shuffle(cards);

        return new FlashcardSetDocument
        {
            Title = string.IsNullOrWhiteSpace(CombinedTitle) ? "Combined flashcards" : CombinedTitle.Trim(),
            Tags = selectedSets.SelectMany(set => set.Document.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SetNames = setNames,
            Cards = cards,
            CreatedAt = DateTime.UtcNow,
            LastModified = DateTime.Now
        };
    }

    private static string BuildCardSignature(FlashcardItem card)
    {
        return string.Join("|", Normalize(card.Question), Normalize(card.Answer));
    }

    private static string Normalize(string? value)
    {
        return string.Join(" ", (value ?? string.Empty).Trim().Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
    }

    private static void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
