using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Controls;
using Scriblucent.Services;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace Scriblucent.Views;

public partial class QuizLibraryWindow : Window, INotifyPropertyChanged, ICombineWorkspaceHost
{
    private PickerSelectionBinding<QuizLibraryItemViewModel>? _pickerSelection;
    public bool? PickerAllChecked => _pickerSelection is null ? false : _pickerSelection.AllChecked;
    public CombineWorkspacePresentation Workspace { get; } = new(false);
    private bool _selectionViewRefreshQueued;
    private readonly MainViewModel _mainViewModel;
    private readonly BulkObservableCollection<QuizLibraryItemViewModel> _quizzes = new();
    private readonly BulkObservableCollection<QuizLibraryItemViewModel> _visibleQuizzes = new();
    private readonly BulkObservableCollection<QuizLibraryItemViewModel> _selectedQuizzes = new();
    private readonly BulkObservableCollection<TagFilterItemViewModel> _categoryFilters = new();
    private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private string _statusText = string.Empty;
    private string _searchText = string.Empty;
    private string _combinedQuizTitle = LocalizationService.GetString("PremiumCombinedQuiz");
    private bool _shuffleCombinedQuestions = true;
    private bool _skipDuplicateQuestions = true;
    private DispatcherTimer? _searchFilterDebounceTimer;
    private int _filterGeneration;
    private readonly Dictionary<string, int> _selectedQuestionSignatures = new(StringComparer.OrdinalIgnoreCase);
    private int _selectedQuestionCount;
    private bool _isLoading;
    private bool _isClosed;

    public string QuizSearchQuery
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value ?? string.Empty;
            OnPropertyChanged(nameof(QuizSearchQuery));

            // MatchesFilter scans every question/option of every quiz in the library - debounce
            // instead of running that on every keystroke (mirrors MainViewModel's dashboard search).
            _searchFilterDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
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

    public QuizLibraryWindow(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        Workspace.Configure(_visibleQuizzes, _categoryFilters,
            item => _pickerSelection?.Toggle((QuizLibraryItemViewModel)item),
            () => _pickerSelection?.ToggleMatching(), () => _pickerSelection?.Selection.Clear(),
            ClearFilters,
            CreateCombinedQuiz, Close);
        Workspace.PropertyChanged += Workspace_PropertyChanged;
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        Scriblucent.Services.FeatureWindowBounds.Attach(this);
        DataContext = this;
        _pickerSelection = new(this, () => _quizzes, () => _visibleQuizzes, item => item.Document.Id,
            (item, value) => item.IsSelected = value, UpdateSelectedQuestionCounts, items =>
        {
            _selectedQuizzes.ReplaceAll(items);
            NotifySelectedQuizzesChanged();
            OnPropertyChanged(nameof(PickerAllChecked));
        });

        LoadQuizzes();
        LoadCategories();
        UpdateFilteredVisibility();
        _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
        WindowContentEnterHelper.AnimateOnLoaded(this);
        Closed += QuizLibraryWindow_Closed;
    }

    private void QuizLibraryWindow_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        Workspace.PropertyChanged -= Workspace_PropertyChanged;
        Workspace.Dispose();
        _pickerSelection?.Dispose();
        _filterGeneration++;
        _searchFilterDebounceTimer?.Stop();
        Closed -= QuizLibraryWindow_Closed;
        _mainViewModel.PropertyChanged -= MainViewModel_PropertyChanged;
        foreach (var quiz in _quizzes)
            quiz.PropertyChanged -= QuizWrapper_PropertyChanged;
    }

    public ObservableCollection<global::Scriblucent.Views.QuizLibraryItemViewModel> Quizzes => _quizzes;
    public ObservableCollection<QuizLibraryItemViewModel> VisibleQuizzes => _visibleQuizzes;
    public ObservableCollection<QuizLibraryItemViewModel> SelectedQuizzes => _selectedQuizzes;

    public ObservableCollection<TagFilterItemViewModel> CategoryFilters => _categoryFilters;

    public string FilteredCountText => string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("PremiumQuizzesCount"), _visibleQuizzes.Count);
    public string SelectedCountText => string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("PremiumSelectedCount"), _selectedQuizzes.Count);
    public string SelectedQuestionCountText => string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("PremiumQuestionsCount"), GetCombinedQuestionCount());
    public bool HasSelectedQuizzes => _selectedQuizzes.Count > 0;

    public string CombinedQuizTitle
    {
        get => _combinedQuizTitle;
        set
        {
            if (_combinedQuizTitle == value)
                return;

            _combinedQuizTitle = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public bool ShuffleCombinedQuestions
    {
        get => _shuffleCombinedQuestions;
        set => SetOptionProperty(ref _shuffleCombinedQuestions, value);
    }

    public bool SkipDuplicateQuestions
    {
        get => _skipDuplicateQuestions;
        set => SetOptionProperty(ref _skipDuplicateQuestions, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
                return;

            _statusText = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private void Workspace_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CombineWorkspacePresentation.SearchText): QuizSearchQuery = Workspace.SearchText; break;
            case nameof(CombineWorkspacePresentation.Title): CombinedQuizTitle = Workspace.Title; break;
            case nameof(CombineWorkspacePresentation.Shuffle): ShuffleCombinedQuestions = Workspace.Shuffle; break;
            case nameof(CombineWorkspacePresentation.SkipDuplicates): SkipDuplicateQuestions = Workspace.SkipDuplicates; break;
            case nameof(CombineWorkspacePresentation.ShowSelectedOnly): _searchFilterDebounceTimer?.Stop(); UpdateFilteredVisibility(); break;
        }
    }
    private void RefreshWorkspaceLibrary() => Workspace.UpdateLibrary(_quizzes.Count, _visibleQuizzes.Count,
        _categoryFilters.Count > 0, _isLoading, _selectedTags.Count);
    private void RefreshWorkspaceSelection()
    {
        Workspace.UpdateSelection(_selectedQuizzes.Count, GetCombinedQuestionCount(), _selectedQuestionCount);
        if (!Workspace.ShowSelectedOnly || _selectionViewRefreshQueued || _visibleQuizzes.SequenceEqual(_selectedQuizzes)) return;
        // Refresh matching IDs after the current selection transaction finishes.
        _selectionViewRefreshQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, () =>
        {
            _selectionViewRefreshQueued = false;
            if (!_isClosed && Workspace.ShowSelectedOnly) UpdateFilteredVisibility();
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.QuizTagFilters)
            || e.PropertyName == nameof(MainViewModel.ActiveTagFilters)
            || e.PropertyName == nameof(MainViewModel.ActiveTagFilterButtonText))
        {
            LoadCategories();
            UpdateFilteredVisibility();
        }
    }

    private async void LoadQuizzes()
    {
        foreach (var quiz in _quizzes)
            quiz.PropertyChanged -= QuizWrapper_PropertyChanged;

        var source = _mainViewModel.Quizzes.ToArray();
        var wrappers = new List<QuizLibraryItemViewModel>(source.Length);
        if (source.Length >= 10_000)
        {
            _isLoading = true;
            StatusText = LocalizationService.GetString("PremiumLoadingQuizzes");
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Loaded);
        }

        var sliceTimer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var quiz in source)
        {
            if (_isClosed)
            {
                foreach (var createdWrapper in wrappers)
                    createdWrapper.PropertyChanged -= QuizWrapper_PropertyChanged;
                return;
            }

            var wrapper = new QuizLibraryItemViewModel(quiz);
            wrapper.PropertyChanged += QuizWrapper_PropertyChanged;
            wrappers.Add(wrapper);
            if (_isLoading && sliceTimer.ElapsedMilliseconds >= 4)
            {
                sliceTimer.Restart();
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        _quizzes.ReplaceAll(wrappers);
        _selectedQuizzes.ReplaceAll([]);
        _selectedQuestionSignatures.Clear();
        _selectedQuestionCount = 0;
        _isLoading = false;
        UpdateFilteredVisibility();
    }

    private void QuizWrapper_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuizLibraryItemViewModel.IsSelected) && sender is QuizLibraryItemViewModel item) _pickerSelection?.Toggle(item);
    }

    private void LoadCategories()
    {
        var filters = new List<TagFilterItemViewModel>();
        foreach (var filter in _mainViewModel.QuizTagFilters)
        {
            var existing = _selectedTags.Contains(filter.Tag);
            var wrapper = new TagFilterItemViewModel(filter.Tag, existing, SetTagSelected);
            filters.Add(wrapper);
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
    }

    private async void UpdateFilteredVisibility()
    {
        var generation = ++_filterGeneration;
        if (Workspace.ShowSelectedOnly)
        {
            if (!_visibleQuizzes.SequenceEqual(_selectedQuizzes)) _visibleQuizzes.ReplaceAll(_selectedQuizzes);
            _pickerSelection?.RefreshItems();
            OnPropertyChanged(nameof(FilteredCountText));
            UpdateStatusText();
            return;
        }
        var tags = _selectedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var search = _searchText;
        if (tags.Count == 0 && string.IsNullOrWhiteSpace(search))
        {
            if (!_visibleQuizzes.SequenceEqual(_quizzes)) _visibleQuizzes.ReplaceAll(_quizzes);
            _pickerSelection?.RefreshItems();
            OnPropertyChanged(nameof(FilteredCountText));
            UpdateStatusText();
            return;
        }

        var visible = new List<QuizLibraryItemViewModel>();
        var sliceTimer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var quiz in _quizzes)
        {
            if (generation != _filterGeneration)
                return;
            if (MatchesFilter(quiz, tags, search))
                visible.Add(quiz);
            if (sliceTimer.ElapsedMilliseconds >= 4)
            {
                sliceTimer.Restart();
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        if (generation != _filterGeneration)
            return;
        if (!_visibleQuizzes.SequenceEqual(visible)) _visibleQuizzes.ReplaceAll(visible);
        _pickerSelection?.RefreshItems();
        OnPropertyChanged(nameof(FilteredCountText));
        UpdateStatusText();
    }

    private void NotifySelectedQuizzesChanged()
    {
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(SelectedQuestionCountText));
        OnPropertyChanged(nameof(HasSelectedQuizzes));
    }

    private static bool MatchesFilter(QuizLibraryItemViewModel quiz, HashSet<string> selectedTags, string search)
    {
        if (selectedTags.Count > 0)
        {
            var tags = quiz.Document.Tags ?? [];
            if (!tags.Any(selectedTags.Contains))
                return false;
        }

        if (string.IsNullOrWhiteSpace(search))
            return true;

        return quiz.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
               || quiz.TagsDisplay.Contains(search, StringComparison.OrdinalIgnoreCase)
               || quiz.Document.Questions.Any(question =>
                   (question.Question ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
                   || (question.Explanation ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)
                   || question.Options.Any(option => (option.Text ?? string.Empty).Contains(search, StringComparison.OrdinalIgnoreCase)));
    }

    private void UpdateStatusText()
    {
        if (_isLoading)
            return;

        var visibleCount = _visibleQuizzes.Count;
        StatusText = string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("PremiumQuizzesShown"), visibleCount, _quizzes.Count);
    }

    private void ClearFilters()
    {
        _selectedTags.Clear();
        foreach (var filter in _categoryFilters)
            filter.IsSelected = false;

        QuizSearchQuery = string.Empty;
        _searchFilterDebounceTimer?.Stop();
        UpdateFilteredVisibility();
        UpdateStatusText();
    }

    private void CreateCombinedQuiz()
    {
        var selectedQuizzes = _selectedQuizzes.Select(quiz => quiz.Model).ToList();
        if (selectedQuizzes.Count == 0)
        {
            ModernMessageBox.Show(LocalizationService.GetString("PremiumSelectQuizFirst"), LocalizationService.GetString("PremiumCombineQuizzes"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var combined = BuildCombinedQuiz(
            selectedQuizzes,
            CombinedQuizTitle,
            ShuffleCombinedQuestions,
            SkipDuplicateQuestions);
        if (Application.Current.MainWindow is MainWindow main)
        {
            main.EditorWorkspace.OpenQuiz(combined, draft: true);
            WindowContentEnterHelper.CloseWithDialogResult(this, true);
        }
    }

    private static QuizDocument BuildCombinedQuiz(
        IEnumerable<QuizViewModel> selectedQuizzes,
        string title,
        bool shuffleQuestions,
        bool skipDuplicateQuestions)
    {
        var documents = selectedQuizzes.Select(q => q.Document).ToList();
        var combinedTags = documents.SelectMany(d => d.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var combinedQuestions = new List<QuizQuestion>();
        var seenQuestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var document in documents)
        {
            foreach (var question in document.Questions)
            {
                if (skipDuplicateQuestions && !seenQuestions.Add(BuildQuestionSignature(question)))
                    continue;

                combinedQuestions.Add(new QuizQuestion
                {
                    // Preserved, not regenerated - each combined entry maps 1:1 to exactly one
                    // source question (skipDuplicateQuestions already prevents two different
                    // source questions collapsing into one), so keeping its Id lets
                    // LastAttemptCorrect / "Practice Missed Questions" carry over into the
                    // combined quiz instead of resetting.
                    Id = question.Id,
                    Type = question.Type,
                    Question = question.Question,
                    Explanation = question.Explanation,
                    Hint = question.Hint,
                    PlainTextAnswer = question.PlainTextAnswer,
                    PlainTextCharLimit = question.PlainTextCharLimit,
                    PlainTextMatchCase = question.PlainTextMatchCase,
                    PlainTextIgnorePunctuation = question.PlainTextIgnorePunctuation,
                    LastAttemptCorrect = question.LastAttemptCorrect,
                    Options = question.Options.Select(option => new QuizOption
                    {
                        Text = option.Text,
                        IsCorrect = option.IsCorrect
                    }).ToList()
                });
            }
        }

        if (shuffleQuestions)
            Shuffle(combinedQuestions);

        for (var i = 0; i < combinedQuestions.Count; i++)
            combinedQuestions[i].SetIndex = i + 1;

        return new QuizDocument
        {
            Title = string.IsNullOrWhiteSpace(title) ? LocalizationService.GetString("PremiumCombinedQuiz") : title.Trim(),
            Tags = combinedTags,
            Questions = combinedQuestions,
            CreatedAt = DateTime.UtcNow,
            LastModified = DateTime.Now
        };
    }

    private static string BuildQuestionSignature(QuizQuestion question)
    {
        var options = question.Options
            .Select(option => $"{NormalizeForSignature(option.Text)}:{option.IsCorrect}")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);

        return string.Join("|",
            question.Type,
            NormalizeForSignature(question.Question),
            NormalizeForSignature(question.PlainTextAnswer),
            question.PlainTextMatchCase,
            question.PlainTextIgnorePunctuation,
            string.Join(";", options));
    }

    private static string NormalizeForSignature(string? value)
    {
        return string.Join(" ", (value ?? string.Empty).Trim().Split(Array.Empty<char>(), StringSplitOptions.RemoveEmptyEntries));
    }

    internal static void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private bool SetOptionProperty(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (field == value)
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(SelectedQuestionCountText));
        return true;
    }

    private int GetCombinedQuestionCount()
        => SkipDuplicateQuestions ? _selectedQuestionSignatures.Count : _selectedQuestionCount;

    private void UpdateSelectedQuestionCounts(QuizLibraryItemViewModel quiz, int direction)
    {
        foreach (var question in quiz.Document.Questions ?? [])
        {
            _selectedQuestionCount += direction;
            var signature = BuildQuestionSignature(question);
            _selectedQuestionSignatures.TryGetValue(signature, out var count);
            if (count + direction == 0)
                _selectedQuestionSignatures.Remove(signature);
            else
                _selectedQuestionSignatures[signature] = count + direction;
        }
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        switch (propertyName)
        {
            case nameof(QuizSearchQuery): Workspace.SearchText = QuizSearchQuery; break;
            case nameof(CombinedQuizTitle): Workspace.Title = CombinedQuizTitle; break;
            case nameof(ShuffleCombinedQuestions): Workspace.Shuffle = ShuffleCombinedQuestions; break;
            case nameof(SkipDuplicateQuestions): Workspace.SkipDuplicates = SkipDuplicateQuestions; RefreshWorkspaceSelection(); break;
            case nameof(SelectedCountText):
            case nameof(SelectedQuestionCountText): RefreshWorkspaceSelection(); break;
            case nameof(FilteredCountText):
            case nameof(StatusText): RefreshWorkspaceLibrary(); break;
        }
    }

    }
