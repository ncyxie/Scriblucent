using Scriblucent.Localization;
using Scriblucent.Controls;
using Scriblucent.Services;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;

namespace Scriblucent.Views;

public partial class FlashcardCombineWindow : Window, INotifyPropertyChanged, ICombineWorkspaceHost
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
        public string ItemCountText => CombineWorkspacePresentation.ContentCount(true, Document.Cards?.Count ?? 0);
        public string TagsDisplay => Model.TagsDisplay;
        public bool HasTags => Model.HasTags;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

    }

    private PickerSelectionBinding<FlashcardCombineItem>? _pickerSelection;
    public bool? PickerAllChecked => _pickerSelection is null ? false : _pickerSelection.AllChecked;
    public CombineWorkspacePresentation Workspace { get; } = new(true);
    private bool _selectionViewRefreshQueued;
    private readonly MainViewModel _mainViewModel;
    private readonly BulkObservableCollection<FlashcardCombineItem> _sets = new();
    private readonly BulkObservableCollection<FlashcardCombineItem> _visibleSets = new();
    private readonly BulkObservableCollection<FlashcardCombineItem> _selectedSets = new();
    private readonly BulkObservableCollection<TagFilterItemViewModel> _categoryFilters = new();
    private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private string _searchText = string.Empty;
    private string _combinedTitle = LocalizationService.GetString("PremiumCombinedFlashcards");
    private string _statusText = string.Empty;
    private bool _skipDuplicateCards = true;
    private bool _shuffleCards = true;
    private System.Windows.Threading.DispatcherTimer? _searchFilterDebounceTimer;
    private int _filterGeneration;
    private readonly Dictionary<string, int> _selectedCardSignatures = new(StringComparer.OrdinalIgnoreCase);
    private int _selectedCardCount;
    private bool _isLoading;
    private bool _isClosed;

    public FlashcardCombineWindow(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        Workspace.Configure(_visibleSets, _categoryFilters,
            item => _pickerSelection?.Toggle((FlashcardCombineItem)item),
            () => _pickerSelection?.ToggleMatching(), () => _pickerSelection?.Selection.Clear(),
            ClearFilters,
            CreateCombinedSet, Close);
        Workspace.PropertyChanged += Workspace_PropertyChanged;
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        Scriblucent.Services.FeatureWindowBounds.Attach(this);
        DataContext = this;
        _pickerSelection = new(this, () => _sets, () => _visibleSets, item => item.Document.Id,
            (item, value) => item.IsSelected = value, UpdateSelectedCardCounts, items =>
        {
            _selectedSets.ReplaceAll(items);
            OnPropertyChanged(nameof(SelectedCountText)); OnPropertyChanged(nameof(SelectedCardCountText)); OnPropertyChanged(nameof(HasSelectedSets));
            OnPropertyChanged(nameof(PickerAllChecked));
        });

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
        _searchFilterDebounceTimer?.Stop();
        Workspace.PropertyChanged -= Workspace_PropertyChanged;
        Workspace.Dispose();
        _pickerSelection?.Dispose();
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
            StatusText = LocalizationService.GetString("PremiumLoadingSets");
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
    public string FilteredCountText => string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("PremiumSetsCount"), _visibleSets.Count);
    public string SelectedCountText => string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("PremiumSelectedCount"), _selectedSets.Count);
    public string SelectedCardCountText => string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("PremiumCardsCount"), GetCombinedCardCount());
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
            OnPropertyChanged();
            _searchFilterDebounceTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchFilterDebounceTimer.Stop();
            _searchFilterDebounceTimer.Tick -= SearchFilterDebounceTimer_Tick;
            _searchFilterDebounceTimer.Tick += SearchFilterDebounceTimer_Tick;
            _searchFilterDebounceTimer.Start();
        }
    }

    private void SearchFilterDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchFilterDebounceTimer?.Stop();
        UpdateFilteredVisibility();
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
        if (e.PropertyName == nameof(FlashcardCombineItem.IsSelected) && sender is FlashcardCombineItem item) _pickerSelection?.Toggle(item);
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
        _selectedTags.IntersectWith(filters.Select(filter => filter.Tag));
        RefreshWorkspaceLibrary();
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
        if (Workspace.ShowSelectedOnly)
        {
            if (!_visibleSets.SequenceEqual(_selectedSets)) _visibleSets.ReplaceAll(_selectedSets);
            _pickerSelection?.RefreshItems();
            OnPropertyChanged(nameof(FilteredCountText));
            UpdateStatusText();
            return;
        }
        var tags = _selectedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var search = _searchText;
        if (tags.Count == 0 && string.IsNullOrWhiteSpace(search))
        {
            if (!_visibleSets.SequenceEqual(_sets)) _visibleSets.ReplaceAll(_sets);
            _pickerSelection?.RefreshItems();
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
        if (!_visibleSets.SequenceEqual(visible)) _visibleSets.ReplaceAll(visible);
        _pickerSelection?.RefreshItems();

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
        StatusText = string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("PremiumSetsShown"), visibleCount, _sets.Count);
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

    private void ClearFilters()
    {
        _selectedTags.Clear();
        foreach (var filter in _categoryFilters)
            filter.IsSelected = false;

        SearchText = string.Empty;
        _searchFilterDebounceTimer?.Stop();
        UpdateFilteredVisibility();
        UpdateStatusText();
    }

    private void CreateCombinedSet()
    {
        var selectedSets = _selectedSets.Select(set => set.Model).ToList();
        if (selectedSets.Count == 0)
        {
            ModernMessageBox.Show(LocalizationService.GetString("PremiumSelectSetFirst"), LocalizationService.GetString("PremiumCombineFlashcards"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var document = BuildCombinedDocument(selectedSets);
        if (Application.Current.MainWindow is MainWindow main)
        {
            main.EditorWorkspace.OpenFlashcards(document, draft: true);
            WindowContentEnterHelper.CloseWithDialogResult(this, true);
        }
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
            Title = string.IsNullOrWhiteSpace(CombinedTitle) ? LocalizationService.GetString("PremiumCombinedFlashcards") : CombinedTitle.Trim(),
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

    private void Workspace_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CombineWorkspacePresentation.SearchText): SearchText = Workspace.SearchText; break;
            case nameof(CombineWorkspacePresentation.Title): CombinedTitle = Workspace.Title; break;
            case nameof(CombineWorkspacePresentation.Shuffle): ShuffleCards = Workspace.Shuffle; break;
            case nameof(CombineWorkspacePresentation.SkipDuplicates): SkipDuplicateCards = Workspace.SkipDuplicates; break;
            case nameof(CombineWorkspacePresentation.ShowSelectedOnly): _searchFilterDebounceTimer?.Stop(); UpdateFilteredVisibility(); break;
        }
    }
    private void RefreshWorkspaceLibrary() => Workspace.UpdateLibrary(_sets.Count, _visibleSets.Count,
        _categoryFilters.Count > 0, _isLoading, _selectedTags.Count);
    private void RefreshWorkspaceSelection()
    {
        Workspace.UpdateSelection(_selectedSets.Count, GetCombinedCardCount(), _selectedCardCount);
        if (!Workspace.ShowSelectedOnly || _selectionViewRefreshQueued || _visibleSets.SequenceEqual(_selectedSets)) return;
        // Refresh matching IDs after the current selection transaction finishes.
        _selectionViewRefreshQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, () =>
        {
            _selectionViewRefreshQueued = false;
            if (!_isClosed && Workspace.ShowSelectedOnly) UpdateFilteredVisibility();
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        switch (propertyName)
        {
            case nameof(SearchText): Workspace.SearchText = SearchText; break;
            case nameof(CombinedTitle): Workspace.Title = CombinedTitle; break;
            case nameof(ShuffleCards): Workspace.Shuffle = ShuffleCards; break;
            case nameof(SkipDuplicateCards): Workspace.SkipDuplicates = SkipDuplicateCards; RefreshWorkspaceSelection(); break;
            case nameof(SelectedCountText):
            case nameof(SelectedCardCountText): RefreshWorkspaceSelection(); break;
            case nameof(FilteredCountText):
            case nameof(StatusText): RefreshWorkspaceLibrary(); break;
        }
    }
}
