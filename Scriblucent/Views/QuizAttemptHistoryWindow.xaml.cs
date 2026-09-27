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
    private readonly BulkObservableCollection<AttemptDisplayItem> _attempts = new();
    private int _refreshGeneration;
    private int _quizCount;
    private double _averageScore;
    private double _bestScore;
    private double _passRate;
    private bool _isLoading;

    private sealed record QuizSnapshot(QuizViewModel Quiz, Guid Id, string Title, QuizAttempt[] Attempts);
    private sealed record HistoryResult(List<AttemptDisplayItem> Items, int QuizCount,
        double AverageScore, double BestScore, double PassRate);

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
    public bool IsLoading => _isLoading;
    public bool ShowEmpty => !_isLoading && !HasAttempts;

    public string AverageScoreText => HasAttempts
        ? string.Format(CultureInfo.CurrentCulture, "{0:F1}%", _averageScore)
        : "-";

    public string BestScoreText => HasAttempts
        ? string.Format(CultureInfo.CurrentCulture, "{0:F1}%", _bestScore)
        : "-";

    public string PassRateText => HasAttempts
        ? string.Format(CultureInfo.CurrentCulture, "{0:F1}%", _passRate)
        : "-";

    public string ClearButtonText => _singleQuizDocument is not null
        ? "Clear this quiz history"
        : "Clear history";

    public string SummaryText => _isLoading
        ? "Loading history..."
        : _attempts.Count == 0
        ? "No quiz attempts yet"
        : _singleQuizDocument is not null
            ? string.Format(CultureInfo.CurrentCulture, "{0} attempts for this quiz", _attempts.Count)
            : string.Format(CultureInfo.CurrentCulture, "{0} attempts across {1} quizzes", _attempts.Count, _quizCount);

    private async void RefreshList()
    {
        var generation = ++_refreshGeneration;
        var singleQuiz = _singleQuizDocument;
        var singleAttempts = singleQuiz?.Attempts?.ToArray();
        var singleTitle = singleQuiz is null ? string.Empty : string.IsNullOrWhiteSpace(singleQuiz.Title) ? "Untitled Quiz" : singleQuiz.Title.Trim();
        var snapshots = singleQuiz is null
            ? _mainViewModel.Quizzes
                .Where(quiz => quiz.Document.Attempts is { Count: > 0 })
                .Select(quiz => new QuizSnapshot(
                    quiz, quiz.Document.Id, quiz.Title, quiz.Document.Attempts.ToArray())).ToArray()
            : Array.Empty<QuizSnapshot>();
        var count = singleAttempts?.Length ?? snapshots.Sum(snapshot => snapshot.Attempts.Length);

        HistoryResult result;
        if (count >= 10_000)
        {
            _isLoading = true;
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(SummaryText));
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);
            result = await Task.Run(() => BuildResult(singleAttempts, singleTitle, snapshots));
            if (generation != _refreshGeneration || !IsVisible)
                return;
        }
        else
        {
            result = BuildResult(singleAttempts, singleTitle, snapshots);
        }

        _quizCount = result.QuizCount;
        _averageScore = result.AverageScore;
        _bestScore = result.BestScore;
        _passRate = result.PassRate;
        _attempts.ReplaceAll(result.Items);
        _isLoading = false;

        OnPropertyChanged(nameof(HasAttempts));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowEmpty));
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

    private static HistoryResult BuildResult(QuizAttempt[]? singleAttempts, string singleTitle, QuizSnapshot[] snapshots)
    {
        var items = singleAttempts is not null
            ? BuildSingleQuizItems(singleAttempts, singleTitle)
            : BuildAllQuizItems(snapshots);

        var quizCount = singleAttempts is not null ? 1 : snapshots.Select(snapshot => snapshot.Id).Distinct().Count();
        double scoreSum = 0;
        double bestScore = 0;
        var passCount = 0;
        foreach (var item in items)
        {
            scoreSum += item.Attempt.Percentage;
            bestScore = Math.Max(bestScore, item.Attempt.Percentage);
            if (item.Passed)
                passCount++;
        }

        return new HistoryResult(items, quizCount,
            items.Count == 0 ? 0 : scoreSum / items.Count,
            bestScore,
            items.Count == 0 ? 0 : passCount / (double)items.Count * 100);
    }

    private static List<AttemptDisplayItem> BuildAllQuizItems(QuizSnapshot[] snapshots)
    {
        return snapshots
            .SelectMany(snapshot => snapshot.Attempts.Select(attempt => new AttemptDisplayItem
            {
                Quiz = snapshot.Quiz,
                Attempt = attempt,
                QuizTitle = string.IsNullOrWhiteSpace(attempt.QuizTitle)
                    ? snapshot.Title
                    : attempt.QuizTitle.Trim(),
                ScoreDisplay = FormatScoreDisplay(attempt),
                DateDisplay = attempt.Date.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
                TimeDisplay = attempt.TimeTaken.ToString(@"hh\:mm\:ss", CultureInfo.CurrentCulture),
                Passed = attempt.Percentage >= attempt.PassingScorePercent
            }))
            .OrderByDescending(item => item.Attempt.Date)
            .ToList();
    }

    private static List<AttemptDisplayItem> BuildSingleQuizItems(QuizAttempt[] attempts, string quizTitle)
    {
        return attempts
            .Select(attempt => new AttemptDisplayItem
            {
                Attempt = attempt,
                QuizTitle = string.IsNullOrWhiteSpace(attempt.QuizTitle)
                    ? quizTitle
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
