using Scriblucent.Animations;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace Scriblucent.Views;

public partial class QuizAttemptHistoryWindow : Window, INotifyPropertyChanged
{
    public sealed class AttemptDisplayItem
    {
        public QuizViewModel Quiz { get; init; } = null!;
        public QuizAttempt Attempt { get; init; } = null!;
        public string QuizTitle { get; init; } = string.Empty;
        public string ScoreDisplay { get; init; } = string.Empty;
        public string DateDisplay { get; init; } = string.Empty;
        public string TimeDisplay { get; init; } = string.Empty;
        public bool Passed { get; init; }
    }

    private readonly MainViewModel _mainViewModel;
    private readonly QuizDocument? _singleQuizDocument;
    private readonly ObservableCollection<AttemptDisplayItem> _attempts = new();

    public QuizAttemptHistoryWindow(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        DataContext = this;
        RefreshList();
        WindowContentEnterHelper.AnimateOnLoaded(this);
    }

    public QuizAttemptHistoryWindow(QuizDocument quizDocument)
    {
        _mainViewModel = null!;
        _singleQuizDocument = quizDocument;
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        DataContext = this;
        RefreshList();
        WindowContentEnterHelper.AnimateOnLoaded(this);
    }

    public ObservableCollection<AttemptDisplayItem> Attempts => _attempts;

    public bool HasAttempts => _attempts.Count > 0;

    public string AverageScoreText => HasAttempts
        ? string.Format(CultureInfo.CurrentCulture, "{0:F1}%", _attempts.Average(item => item.Attempt.Percentage))
        : "-";

    public string BestScoreText => HasAttempts
        ? string.Format(CultureInfo.CurrentCulture, "{0:F1}%", _attempts.Max(item => item.Attempt.Percentage))
        : "-";

    public string PassRateText => HasAttempts
        ? string.Format(CultureInfo.CurrentCulture, "{0:F1}%", _attempts.Count(item => item.Passed) / (double)_attempts.Count * 100)
        : "-";

    public string ClearButtonText => _singleQuizDocument is not null
        ? "Clear this quiz history"
        : "Clear history";

    public string SummaryText => _attempts.Count == 0
        ? "No quiz attempts yet"
        : _singleQuizDocument is not null
            ? string.Format(CultureInfo.CurrentCulture, "{0} attempts for this quiz", _attempts.Count)
            : string.Format(CultureInfo.CurrentCulture, "{0} attempts across {1} quizzes", _attempts.Count, _attempts.Select(item => item.Quiz.Document.Id).Distinct().Count());

    private void RefreshList()
    {
        _attempts.Clear();

        var items = _singleQuizDocument is not null
            ? BuildSingleQuizItems(_singleQuizDocument)
            : BuildAllQuizItems()
            .OrderByDescending(item => item.Attempt.Date)
            .ToList();

        foreach (var item in items)
            _attempts.Add(item);

        OnPropertyChanged(nameof(HasAttempts));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(AverageScoreText));
        OnPropertyChanged(nameof(BestScoreText));
        OnPropertyChanged(nameof(PassRateText));
    }

    // Shows the fractional earned points instead of the whole-question count only when partial
    // credit actually produced a non-whole value - keeps old/ordinary attempts (whole CorrectCount)
    // displaying exactly as before.
    private static string FormatScoreDisplay(QuizAttempt attempt)
    {
        return attempt.EarnedPoints != Math.Floor(attempt.EarnedPoints)
            ? string.Format(CultureInfo.CurrentCulture, "{0:0.##}/{1} ({2:F1}%)", attempt.EarnedPoints, attempt.TotalQuestions, attempt.Percentage)
            : string.Format(CultureInfo.CurrentCulture, "{0}/{1} ({2:F1}%)", attempt.CorrectCount, attempt.TotalQuestions, attempt.Percentage);
    }

    private List<AttemptDisplayItem> BuildAllQuizItems()
    {
        return _mainViewModel.Quizzes
            .SelectMany(quiz => (quiz.Document.Attempts ?? new List<QuizAttempt>()).Select(attempt => new AttemptDisplayItem
            {
                Quiz = quiz,
                Attempt = attempt,
                QuizTitle = string.IsNullOrWhiteSpace(attempt.QuizTitle)
                    ? quiz.Title
                    : attempt.QuizTitle.Trim(),
                ScoreDisplay = FormatScoreDisplay(attempt),
                DateDisplay = attempt.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                TimeDisplay = attempt.TimeTaken.ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture),
                Passed = attempt.Percentage >= attempt.PassingScorePercent
            }))
            .OrderByDescending(item => item.Attempt.Date)
            .ToList();
    }

    private static List<AttemptDisplayItem> BuildSingleQuizItems(QuizDocument quizDocument)
    {
        return (quizDocument.Attempts ?? new List<QuizAttempt>())
            .Select(attempt => new AttemptDisplayItem
            {
                Attempt = attempt,
                QuizTitle = string.IsNullOrWhiteSpace(attempt.QuizTitle)
                    ? string.IsNullOrWhiteSpace(quizDocument.Title) ? "Untitled Quiz" : quizDocument.Title.Trim()
                    : attempt.QuizTitle.Trim(),
                ScoreDisplay = FormatScoreDisplay(attempt),
                DateDisplay = attempt.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                TimeDisplay = attempt.TimeTaken.ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture),
                Passed = attempt.Percentage >= attempt.PassingScorePercent
            })
            .OrderByDescending(item => item.Attempt.Date)
            .ToList();
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_attempts.Count == 0)
            return;

        var result = ModernMessageBox.Show(
            _singleQuizDocument is not null
                ? "Clear this quiz's attempt history?"
                : "Clear all quiz attempt history?",
            "Clear History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        if (_singleQuizDocument is not null)
        {
            _singleQuizDocument.Attempts.Clear();
            RefreshList();
            return;
        }

        foreach (var quiz in _mainViewModel.Quizzes)
        {
            quiz.Document.Attempts.Clear();
            quiz.NotifyChanged();
        }

        _mainViewModel.SaveQuizzes();
        RefreshList();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
