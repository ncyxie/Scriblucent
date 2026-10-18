using System.Collections;
using System.Windows.Input;
using Scriblucent.Controls;
using Scriblucent.Icons;
using Scriblucent.Localization;

namespace Scriblucent.ViewModels;

/// <summary>The common interaction and presentation contract for both combine windows.</summary>
public sealed class CombineWorkspacePresentation : ViewModelBase, IDisposable
{
    private readonly bool _flashcards;
    private string _searchText = "", _title;
    private bool _showSelectedOnly, _shuffle = true, _skipDuplicates = true;
    private int _total, _visible, _selected, _result, _raw, _activeTags;
    private bool _hasTags, _loading;

    public CombineWorkspacePresentation(bool flashcards)
    {
        _flashcards = flashcards;
        _title = Text(flashcards ? "PremiumCombinedFlashcards" : "PremiumCombinedQuiz");
        ShowAllCommand = new RelayCommand(() => ShowSelectedOnly = false);
    }

    public IEnumerable Sources { get; private set; } = Array.Empty<object>();
    public IEnumerable Tags { get; private set; } = Array.Empty<object>();
    public ICommand ToggleSourceCommand { get; private set; } = null!;
    public ICommand SelectAllCommand { get; private set; } = null!;
    public ICommand ClearSelectionCommand { get; private set; } = null!;
    public ICommand ClearFiltersCommand { get; private set; } = null!;
    public ICommand CreateCommand { get; private set; } = null!;
    public ICommand CloseCommand { get; private set; } = null!;
    public ICommand ShowAllCommand { get; }

    internal void Configure(IEnumerable sources, IEnumerable tags, Action<object> toggle,
        Action selectAll, Action clearSelection, Action clearFilters, Action create, Action close)
    {
        Sources = sources; Tags = tags;
        ToggleSourceCommand = new RelayCommand<object>(item => { if (item is not null) toggle(item); });
        SelectAllCommand = new RelayCommand(selectAll);
        ClearSelectionCommand = new RelayCommand(clearSelection);
        ClearFiltersCommand = new RelayCommand(clearFilters);
        CreateCommand = new RelayCommand(create); CloseCommand = new RelayCommand(close);
        LocalizationService.CultureChanged += CultureChanged;
    }

    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value ?? "")) NotifyLibrary(); } }
    public string Title { get => _title; set => SetProperty(ref _title, value ?? ""); }
    public bool Shuffle { get => _shuffle; set => SetProperty(ref _shuffle, value); }
    public bool SkipDuplicates { get => _skipDuplicates; set => SetProperty(ref _skipDuplicates, value); }
    public bool ShowSelectedOnly
    {
        get => _showSelectedOnly;
        set { if (SetProperty(ref _showSelectedOnly, value)) { OnPropertyChanged(nameof(IsAllView)); NotifyLibrary(); } }
    }
    public bool IsAllView { get => !ShowSelectedOnly; set { if (value) ShowSelectedOnly = false; } }
    public bool HasSelection => _selected > 0;
    public bool HasTags => _hasTags;
    public bool HasSources => _total > 0;
    public bool IsLoading => _loading;
    public bool IsSourceListEmpty => !_loading && _visible == 0;
    public bool HasFilters => _activeTags > 0 || !string.IsNullOrWhiteSpace(SearchText);
    public bool HasActiveTags => _activeTags > 0;
    public bool CanClearFilters => HasFilters && IsAllView;
    public bool HasRemovedDuplicates => SkipDuplicates && _raw > _result;
    public string RendererKey => _flashcards ? "CombineRow" : "QuizLibraryRow";
    public AppIconKind ContentIcon => _flashcards ? AppIconKind.Flashcards : AppIconKind.Quizzes;
    public StateIllustrationKind SelectionIllustration => _flashcards ? StateIllustrationKind.FlashcardSelection : StateIllustrationKind.Selection;
    public string Heading => Text(_flashcards ? "PremiumCombineFlashcards" : "PremiumCombineQuizzes");
    public string Help => Text(_flashcards ? "PremiumCombineFlashcardsHelp" : "PremiumCombineQuizzesHelp");
    public string SourcesHeading => ShowSelectedOnly ? SelectedViewLabel : Text(_flashcards ? "PremiumAllFlashcardSets" : "PremiumAllQuizzes");
    public string SearchHint => Text(_flashcards ? "PremiumSearchFlashcards" : "PremiumSearchQuizzes");
    public string TitleLabel => Text(_flashcards ? "PremiumSetTitle" : "PremiumQuizTitle");
    public string ShuffleLabel => Text(_flashcards ? "PremiumShuffleCards" : "PremiumShuffleQuestions");
    public string SkipDuplicatesLabel => Text(_flashcards ? "PremiumSkipCards" : "PremiumSkipQuestions");
    public string CreateLabel => Text(_flashcards ? "PremiumCreateSet" : "PremiumCreateQuiz");
    public string SelectionTitle => Text(_flashcards ? "CombineBuildSetTitle" : "MotionStateSelectionTitle");
    public string SelectionDescription => Text(_flashcards ? "CombineBuildSetDescription" : "CombineBuildQuizDescription");
    public string SelectedViewLabel => Format("CombineSelectedView", _selected);
    public string VisibleCountText => _visible == 1 ? Text(_flashcards ? "CombineOneSet" : "CombineOneQuiz") : Format(_flashcards ? "PremiumSetsCount" : "PremiumQuizzesCount", _visible);
    public string ResultCountText => ContentCount(_flashcards, _result);
    public string ResultSourcesText => _selected == 1 ? Text(_flashcards ? "CombineFromOneSet" : "CombineFromOneQuiz") : Format(_flashcards ? "CombineSourceSets" : "CombineSourceQuizzes", _selected);
    public string DuplicatesText => _raw - _result == 1 ? Text("CombineOneDuplicate") : Format("CombineDuplicatesRemoved", Math.Max(0, _raw - _result));
    public string TagFilterText => _activeTags == 0 ? Text("PremiumFilterTag") : Format("CombineActiveTags", _activeTags);
    public string EmptyTitle => Text(_total == 0 ? (_flashcards ? "CombineNoSets" : "CombineNoQuizzes")
        : ShowSelectedOnly ? "CombineNothingSelected" : (_flashcards ? "CombineNoMatchingSets" : "CombineNoMatchingQuizzes"));
    public string EmptyDescription => Text(_total == 0 ? "CombineLibraryEmptyDescription"
        : ShowSelectedOnly ? "CombineSelectionEmptyDescription" : "CombineNoMatchesDescription");

    internal void UpdateLibrary(int total, int visible, bool hasTags, bool loading, int activeTags)
    {
        _total = total; _visible = visible; _hasTags = hasTags; _loading = loading; _activeTags = activeTags;
        NotifyLibrary();
    }

    internal void UpdateSelection(int selected, int result, int raw)
    {
        var hadSelection = HasSelection;
        _selected = selected; _result = result; _raw = raw;
        if (hadSelection != HasSelection) OnPropertyChanged(nameof(HasSelection));
        foreach (var name in new[] { nameof(SelectedViewLabel), nameof(ResultCountText), nameof(ResultSourcesText), nameof(DuplicatesText), nameof(HasRemovedDuplicates) })
            OnPropertyChanged(name);
    }

    private void NotifyLibrary()
    {
        foreach (var name in new[] { nameof(HasTags), nameof(HasSources), nameof(IsLoading), nameof(IsSourceListEmpty), nameof(HasFilters), nameof(HasActiveTags), nameof(CanClearFilters),
            nameof(VisibleCountText), nameof(SourcesHeading), nameof(TagFilterText), nameof(EmptyTitle), nameof(EmptyDescription) }) OnPropertyChanged(name);
    }
    internal static string ContentCount(bool flashcards, int count) => count == 1 ? Text(flashcards ? "CombineOneCard" : "CombineOneQuestion") : Format(flashcards ? "CombineCardCount" : "CombineQuestionCount", count);
    private static string Text(string key) => LocalizationService.GetString(key);
    private static string Format(string key, object value) => string.Format(System.Globalization.CultureInfo.CurrentCulture, Text(key), value);
    private void CultureChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);
    public void Dispose() => LocalizationService.CultureChanged -= CultureChanged;
}
