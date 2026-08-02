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
    private readonly ObservableCollection<global::Scriblucent.Views.QuizLibraryItemViewModel> _quizzes = new();
    private readonly ObservableCollection<TagFilterItemViewModel> _categoryFilters = new();
    private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private string _statusText = string.Empty;
    private string _searchText = string.Empty;
    private string _combinedQuizTitle = "Combined quiz";
    private bool _shuffleCombinedQuestions = true;
    private bool _skipDuplicateQuestions = true;
    private DispatcherTimer? _searchFilterDebounceTimer;

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
        UpdateStatusText();
    }

    public QuizLibraryWindow(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        DataContext = this;

        LoadQuizzes();
        LoadCategories();
        UpdateStatusText();
        UpdateFilteredVisibility();
        _mainViewModel.PropertyChanged += MainViewModel_PropertyChanged;
        WindowContentEnterHelper.AnimateOnLoaded(this);
        Closed += QuizLibraryWindow_Closed;
    }

    private void QuizLibraryWindow_Closed(object? sender, EventArgs e)
    {
        Closed -= QuizLibraryWindow_Closed;
        _mainViewModel.PropertyChanged -= MainViewModel_PropertyChanged;
        foreach (var quiz in _quizzes)
            quiz.PropertyChanged -= QuizWrapper_PropertyChanged;
    }

    public ObservableCollection<global::Scriblucent.Views.QuizLibraryItemViewModel> Quizzes => _quizzes;

    public ObservableCollection<TagFilterItemViewModel> CategoryFilters => _categoryFilters;

    public string FilteredCountText => string.Format(CultureInfo.CurrentCulture, "{0} quizzes", _quizzes.Count(q => q.IsVisible));
    public string SelectedCountText => string.Format(CultureInfo.CurrentCulture, "{0} selected", _quizzes.Count(q => q.IsSelected));
    public string SelectedQuestionCountText => string.Format(CultureInfo.CurrentCulture, "{0} questions in combined quiz", GetCombinedQuestionCount());
    public bool HasSelectedQuizzes => _quizzes.Any(q => q.IsSelected);

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
            UpdateStatusText();
        }
    }

    private void LoadQuizzes()
    {
        _quizzes.Clear();
        foreach (var quiz in _mainViewModel.Quizzes)
        {
            var wrapper = new global::Scriblucent.Views.QuizLibraryItemViewModel(quiz);
            wrapper.PropertyChanged += QuizWrapper_PropertyChanged;
            _quizzes.Add(wrapper);
        }
    }

    private void QuizWrapper_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QuizLibraryItemViewModel.IsVisible)
            or nameof(QuizLibraryItemViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(FilteredCountText));
            OnPropertyChanged(nameof(SelectedCountText));
            OnPropertyChanged(nameof(SelectedQuestionCountText));
            OnPropertyChanged(nameof(HasSelectedQuizzes));
            UpdateStatusText();
        }
    }

    private void LoadCategories()
    {
        _categoryFilters.Clear();
        foreach (var filter in _mainViewModel.QuizTagFilters)
        {
            var existing = _selectedTags.Contains(filter.Tag);
            var wrapper = new TagFilterItemViewModel(filter.Tag, existing, SetTagSelected);
            _categoryFilters.Add(wrapper);
        }
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

    private void UpdateFilteredVisibility()
    {
        foreach (var quiz in _quizzes)
            quiz.IsVisible = MatchesFilter(quiz);

        OnPropertyChanged(nameof(FilteredCountText));
    }

    private bool MatchesFilter(QuizLibraryItemViewModel quiz)
    {
        if (_selectedTags.Count > 0)
        {
            var tags = quiz.Document.Tags ?? [];
            if (!tags.Any(tag => _selectedTags.Contains(tag)))
                return false;
        }

        if (string.IsNullOrWhiteSpace(_searchText))
            return true;

        return quiz.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
               || quiz.TagsDisplay.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
               || quiz.Document.Questions.Any(question =>
                   (question.Question ?? string.Empty).Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                   || (question.Explanation ?? string.Empty).Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                   || question.Options.Any(option => (option.Text ?? string.Empty).Contains(_searchText, StringComparison.OrdinalIgnoreCase)));
    }

    private void UpdateStatusText()
    {
        var visibleCount = _quizzes.Count(quiz => quiz.IsVisible);
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
        foreach (var quiz in _quizzes)
            quiz.IsSelected = false;
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
        var selectedQuizzes = _quizzes.Where(quiz => quiz.IsSelected).Select(quiz => quiz.Model).ToList();
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
    {
        var questions = _quizzes
            .Where(quiz => quiz.IsSelected)
            .SelectMany(quiz => quiz.Document.Questions ?? []);

        if (!SkipDuplicateQuestions)
            return questions.Count();

        return questions
            .Select(BuildQuestionSignature)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
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
