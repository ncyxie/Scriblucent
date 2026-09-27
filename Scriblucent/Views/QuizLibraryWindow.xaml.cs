using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Scriblucent.Views;

public partial class QuizLibraryWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _mainViewModel;
    private readonly BulkObservableCollection<QuizLibraryItemViewModel> _quizzes = new();
    private readonly BulkObservableCollection<QuizLibraryItemViewModel> _visibleQuizzes = new();
    private readonly BulkObservableCollection<QuizLibraryItemViewModel> _selectedQuizzes = new();
    private readonly BulkObservableCollection<TagFilterItemViewModel> _categoryFilters = new();
    private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private string _statusText = string.Empty;
    private string _searchText = string.Empty;
    private string _combinedQuizTitle = "Combined quiz";
    private bool _shuffleCombinedQuestions = true;
    private bool _skipDuplicateQuestions = true;
    private DispatcherTimer? _searchFilterDebounceTimer;
    private int _filterGeneration;
    private bool _isClearingSelection;
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
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        DataContext = this;

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

    public string FilteredCountText => string.Format(CultureInfo.CurrentCulture, "{0} quizzes", _visibleQuizzes.Count);
    public string SelectedCountText => string.Format(CultureInfo.CurrentCulture, "{0} selected", _selectedQuizzes.Count);
    public string SelectedQuestionCountText => string.Format(CultureInfo.CurrentCulture, "{0} questions in combined quiz", GetCombinedQuestionCount());
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
            StatusText = "Loading quizzes...";
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
        if (e.PropertyName == nameof(QuizLibraryItemViewModel.IsSelected)
            && !_isClearingSelection
            && sender is QuizLibraryItemViewModel quiz)
        {
            if (quiz.IsSelected)
            {
                UpdateSelectedQuestionCounts(quiz, 1);
                _selectedQuizzes.Add(quiz);
            }
            else
            {
                UpdateSelectedQuestionCounts(quiz, -1);
                _selectedQuizzes.Remove(quiz);
            }
            NotifySelectedQuizzesChanged();
        }
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
        var tags = _selectedTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var search = _searchText;
        if (tags.Count == 0 && string.IsNullOrWhiteSpace(search))
        {
            _visibleQuizzes.ReplaceAll(_quizzes);
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
        _visibleQuizzes.ReplaceAll(visible);
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
        StatusText = string.Format(CultureInfo.CurrentCulture, "{0} of {1} quizzes shown", visibleCount, _quizzes.Count);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
            QuizSearchQuery = tb.Text;
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedTags.Clear();
        foreach (var filter in _categoryFilters)
            filter.IsSelected = false;

        QuizSearchQuery = string.Empty;
        UpdateFilteredVisibility();
        UpdateStatusText();
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        _isClearingSelection = true;
        try
        {
            foreach (var quiz in _selectedQuizzes.ToArray())
                quiz.IsSelected = false;
        }
        finally
        {
            _isClearingSelection = false;
        }

        _selectedQuizzes.ReplaceAll([]);
        _selectedQuestionSignatures.Clear();
        _selectedQuestionCount = 0;
        NotifySelectedQuizzesChanged();
    }

    private void QuizItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is global::Scriblucent.Views.QuizLibraryItemViewModel quiz)
            quiz.IsSelected = !quiz.IsSelected;
    }

    private void SelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void CreateCombinedQuizButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedQuizzes = _selectedQuizzes.Select(quiz => quiz.Model).ToList();
        if (selectedQuizzes.Count == 0)
        {
            ModernMessageBox.Show("Select at least one quiz first.", "Combine quizzes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var combined = BuildCombinedQuiz(
            selectedQuizzes,
            CombinedQuizTitle,
            ShuffleCombinedQuestions,
            SkipDuplicateQuestions);
        var editor = new QuizPreviewWindow(
            combined,
            combined.AiModelDisplayName,
            combined.Title)
        {
            Owner = this
        };

        if (editor.ShowDialog() == true)
            _mainViewModel.AddOrUpdateQuiz(editor.ToDocument());
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
            Title = string.IsNullOrWhiteSpace(title) ? "Combined quiz" : title.Trim(),
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    }
