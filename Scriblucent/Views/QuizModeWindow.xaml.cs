using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Icons;
using Scriblucent.Localization;
using Scriblucent;
using Scriblucent.Models;
using Scriblucent.Services;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Scriblucent.Views;

public partial class QuizModeWindow : Window
{
    private const string PlainTextAnswerMarker = "__PLAINTEXT__:";

    private QuizDocument _quiz;
    private int _currentQuestionIndex;
    private readonly Dictionary<int, List<QuizOption>> _userAnswers;
    private DispatcherTimer? _timer;
    private TimeSpan _elapsedTime;
    private bool _isResultsView;
    private readonly int _timeLimitSeconds; // 0 = be limito
    private bool _isCountdown;
    private TimeSpan _remainingTime;
    private readonly HashSet<int> _flaggedQuestions = new();
    private readonly HashSet<int> _hintUsedQuestions = new();
    public QuizAttempt? LastAttempt { get; private set; }

    // Fires the instant a score is computed (Submit confirmed, or the timer running out) - this is
    // the actual status of the quiz, independent of how the window happens to get closed
    // afterward (Done button, system X, Alt+F4, ...). The caller should record the attempt here
    // rather than inferring completion from LastAttempt after ShowDialog() returns.
    public event Action<QuizAttempt>? AttemptSubmitted;
    private bool _attemptSubmittedFired;
    private List<int> _wrongQuestionIndices = new();
    private bool _timerWarningActive;

    public QuizModeWindow(QuizDocument quiz, int timeLimitSeconds = 0, QuizProgressSession? resumeFrom = null)
    {
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);

        _quiz = quiz ?? throw new System.ArgumentNullException(nameof(quiz));

        if (resumeFrom is { Questions.Count: > 0 })
        {
            // Resume with the frozen, already-presented snapshot instead of re-shuffling (which
            // would desync the stored per-question answers) or trusting the freshly-rebuilt `quiz`
            // (which may no longer match if the quiz was edited since the session was captured).
            _quiz.Questions = resumeFrom.Questions;
        }
        else
        {
            ApplyShufflePreferences(_quiz);
        }

        _timeLimitSeconds = timeLimitSeconds;
        _currentQuestionIndex = resumeFrom is not null
            ? Math.Clamp(resumeFrom.CurrentQuestionIndex, 0, Math.Max(0, _quiz.Questions.Count - 1))
            : 0;
        _userAnswers = BuildUserAnswersFromProgress(resumeFrom);
        RestoreFlaggedQuestionsFromProgress(resumeFrom);
        _elapsedTime = resumeFrom is not null ? TimeSpan.FromSeconds(resumeFrom.ElapsedSeconds) : TimeSpan.Zero;
        _isResultsView = false;

        Title = LocalizationService.GetString("QuizModeTitle");

        StartTimer();
        RebuildQuestionNavigator();
        LoadQuestion();
        WindowContentEnterHelper.AnimateOnLoaded(this);
        Closed += QuizModeWindow_Closed;
    }

    private Dictionary<int, List<QuizOption>> BuildUserAnswersFromProgress(QuizProgressSession? resumeFrom)
    {
        var answers = new Dictionary<int, List<QuizOption>>();
        if (resumeFrom is null)
            return answers;

        for (var i = 0; i < _quiz.Questions.Count; i++)
        {
            var question = _quiz.Questions[i];
            if (question.Type == QuizQuestionType.PlainText)
            {
                if (resumeFrom.PlainTextAnswersByQuestionId.TryGetValue(question.Id, out var text)
                    && !string.IsNullOrEmpty(text))
                {
                    answers[i] = new List<QuizOption>
                    {
                        new() { Text = $"{PlainTextAnswerMarker}{text}", IsCorrect = false }
                    };
                }
                continue;
            }

            if (!resumeFrom.SelectedOptionIndexesByQuestionId.TryGetValue(question.Id, out var indexes))
                continue;

            var selected = indexes
                .Where(index => index >= 0 && index < question.Options.Count)
                .Select(index => question.Options[index])
                .ToList();
            if (selected.Count > 0)
                answers[i] = selected;
        }

        return answers;
    }

    private void RestoreFlaggedQuestionsFromProgress(QuizProgressSession? resumeFrom)
    {
        _flaggedQuestions.Clear();
        if (resumeFrom?.FlaggedQuestionIds is not { Count: > 0 })
            return;

        var flaggedIds = new HashSet<Guid>(resumeFrom.FlaggedQuestionIds);
        for (var i = 0; i < _quiz.Questions.Count; i++)
        {
            if (flaggedIds.Contains(_quiz.Questions[i].Id))
                _flaggedQuestions.Add(i);
        }
    }

    // Snapshots the in-progress attempt for later resume - null once results are showing (a
    // finished attempt has nothing left to resume) or if there are no questions to resume into.
    public QuizProgressSession? CaptureProgress()
    {
        if (_isResultsView || _quiz.Questions is not { Count: > 0 })
            return null;

        var elapsed = _isCountdown
            ? TimeSpan.FromSeconds(_timeLimitSeconds) - _remainingTime
            : _elapsedTime;

        var session = new QuizProgressSession
        {
            Questions = _quiz.Questions,
            CurrentQuestionIndex = _currentQuestionIndex,
            TimeLimitSeconds = _timeLimitSeconds,
            ElapsedSeconds = (long)Math.Max(0, elapsed.TotalSeconds)
        };

        for (var i = 0; i < _quiz.Questions.Count; i++)
        {
            if (_flaggedQuestions.Contains(i))
                session.FlaggedQuestionIds.Add(_quiz.Questions[i].Id);

            if (!_userAnswers.TryGetValue(i, out var selected) || selected.Count == 0)
                continue;

            var question = _quiz.Questions[i];
            if (question.Type == QuizQuestionType.PlainText)
            {
                if (TryGetPlainTextAnswer(selected, out var text) && !string.IsNullOrEmpty(text))
                    session.PlainTextAnswersByQuestionId[question.Id] = text;
                continue;
            }

            var indexes = selected
                .Select(option => question.Options.IndexOf(option))
                .Where(index => index >= 0)
                .ToList();
            if (indexes.Count > 0)
                session.SelectedOptionIndexesByQuestionId[question.Id] = indexes;
        }

        return session;
    }

    // Reorders in place, once, before the first question loads - reuses QuizLibraryWindow's
    // combine-time Fisher-Yates helper so there's one shuffle implementation, not two.
    private static void ApplyShufflePreferences(QuizDocument quiz)
    {
        if (quiz.Questions is not { Count: > 0 })
            return;

        if (quiz.ShuffleQuestions)
            QuizLibraryWindow.Shuffle(quiz.Questions);

        if (quiz.ShuffleOptions)
        {
            foreach (var question in quiz.Questions)
            {
                if (question.Options is { Count: > 0 })
                    QuizLibraryWindow.Shuffle(question.Options);
            }
        }
    }

    private void QuizModeWindow_Closed(object? sender, System.EventArgs e)
    {
        Closed -= QuizModeWindow_Closed;
        _timer?.Stop();
    }

    private void StartTimer()
    {
        _isCountdown = _timeLimitSeconds > 0;
        if (_isCountdown)
            _remainingTime = TimeSpan.FromSeconds(_timeLimitSeconds) - _elapsedTime;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (s, e) =>
        {
            if (_isCountdown)
            {
                _remainingTime -= TimeSpan.FromSeconds(1);
                UpdateTimerDisplay(_remainingTime);

                if (_remainingTime.TotalSeconds <= 30 && !_timerWarningActive)
                    HighlightTimerWarning();

                if (_remainingTime <= TimeSpan.Zero)
                {
                    _timer.Stop();
                    ShowResults();
                }
            }
            else
            {
                _elapsedTime += TimeSpan.FromSeconds(1);
                UpdateTimerDisplay(_elapsedTime);
            }
        };
        _timer.Start();
    }

    private void HighlightTimerWarning()
    {
        if (FindName("TimerText") is not TextBlock timerText)
            return;

        _timerWarningActive = true;
        timerText.Foreground = new SolidColorBrush(Colors.OrangeRed);

        if (!UiMotion.PrefersReducedMotion)
            UiMotion.PulseOpacity(timerText, timerText, 0.72, 1.0, 1400);
    }
    private void UpdateTimerDisplay(TimeSpan time)
    {
        if (FindName("TimerText") is TextBlock timerText)
            timerText.Text = time.ToString(@"hh\:mm\:ss");
    }

    private void LoadQuestion()
    {
        if (_quiz.Questions == null || _quiz.Questions.Count == 0 || _currentQuestionIndex >= _quiz.Questions.Count)
        {
            ShowResults();
            return;
        }

        var question = _quiz.Questions[_currentQuestionIndex];

        // Show question view, hide results view
        if (FindName("QuestionView") is Border questionView) questionView.Visibility = Visibility.Visible;
        if (FindName("ResultsView") is Border resultsView) resultsView.Visibility = Visibility.Collapsed;

        // Update progress
        if (FindName("ProgressText") is TextBlock progressText)
            progressText.Text = string.Format(LocalizationService.GetString("QuizQuestionProgressFormat"), _currentQuestionIndex + 1, _quiz.Questions.Count);

        // Set question text
        if (FindName("QuestionTextBlock") is TextBlock questionTextBlock)
            questionTextBlock.Text = question.Question ?? string.Empty;

        if (FindName("QuestionTypeIndicator") is StackPanel typeIndicator)
        {
            if (question.Type == QuizQuestionType.MultipleChoice)
            {
                typeIndicator.Visibility = Visibility.Visible;
                if (FindName("QuestionTypeText") is TextBlock typeText)
                    typeText.Text = LocalizationService.GetString("QuizMultipleChoiceInstruction");
            }
            else
            {
                typeIndicator.Visibility = Visibility.Collapsed;
            }
        }

        // Clear and populate options or text input for PlainText questions
        if (FindName("OptionsPanel") is StackPanel optionsPanel)
        {
            optionsPanel.Children.Clear();

            if (question.Type == QuizQuestionType.PlainText)
            {
                optionsPanel.Children.Add(CreatePlainTextAnswerCard(question));
            }
            else if (question.Options != null)
            {
                var optionIndex = 0;
                foreach (var option in question.Options)
                {
                    if (option == null || string.IsNullOrWhiteSpace(option.Text)) continue;

                    var button = new Button
                    {
                        Tag = option,
                        FontSize = TypeScale.Px(TypeScale.Body, 14),
                        FontWeight = FontWeights.Normal,
                        Content = CreateOptionButtonContent(optionIndex, option.Text)
                    };

                    if (TryFindResource("QuizOptionButtonBaseStyle") is Style optionStyle)
                        button.Style = optionStyle;

                    button.Click += OptionButton_Click;
                    optionsPanel.Children.Add(button);
                    optionIndex++;
                }
            }
        }

        // Restore previously selected answer(s)
        RestoreSelectedAnswer();
        LoadHintSection();
        UpdateNavigationButtons();
        UpdateFlagQuestionButton();
        RefreshQuestionNavigator();
    }

    private void RebuildQuestionNavigator()
    {
        if (FindName("QuestionNavButtonsPanel") is not WrapPanel panel)
            return;

        panel.Children.Clear();
        var count = _quiz.Questions?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            var button = new Button
            {
                Tag = i,
                Content = CreateQuestionNavButtonContent(i)
            };
            if (TryFindResource("QuizNavQuestionButtonStyle") is Style navStyle)
                button.Style = navStyle;

            button.Click += QuestionNavButton_Click;
            panel.Children.Add(button);
        }

        RefreshQuestionNavigator();
    }

    private FrameworkElement CreateQuestionNavButtonContent(int index)
    {
        var root = new Grid();
        var number = new TextBlock
        {
            Text = (index + 1).ToString(CultureInfo.CurrentCulture),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        root.Children.Add(number);

        // Moodle-style red corner flag marker
        var flagMark = new Polygon
        {
            Name = "FlagMark",
            Points = new PointCollection
            {
                new Point(0, 0),
                new Point(10, 0),
                new Point(10, 10)
            },
            Fill = (Brush)FindResource("AccentDanger"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -1, -1, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(flagMark);
        return root;
    }

    private static Polygon? TryGetNavFlagMark(Button button)
        => button.Content is Grid grid
            ? grid.Children.OfType<Polygon>().FirstOrDefault()
            : null;

    private void RefreshQuestionNavigator()
    {
        var showNav = !_isResultsView;
        if (FindName("QuestionNavPanel") is Border navPanel)
            navPanel.Visibility = showNav ? Visibility.Visible : Visibility.Collapsed;

        var contentMargin = showNav ? new Thickness(0, 0, 12, 0) : new Thickness(0);
        if (FindName("QuestionView") is Border questionView)
        {
            questionView.Margin = contentMargin;
            Grid.SetColumnSpan(questionView, showNav ? 1 : 2);
        }

        if (FindName("ResultsView") is Border resultsView)
        {
            resultsView.Margin = contentMargin;
            Grid.SetColumnSpan(resultsView, showNav ? 1 : 2);
        }

        if (FindName("QuestionNavButtonsPanel") is not WrapPanel panel)
            return;

        var examMode = _quiz.ExamMode;
        foreach (var child in panel.Children)
        {
            if (child is not Button button || button.Tag is not int index)
                continue;

            var isCurrent = index == _currentQuestionIndex;
            var isAnswered = IsQuestionAnswered(index);
            var isFlagged = _flaggedQuestions.Contains(index);
            var canJump = !examMode;

            button.IsEnabled = canJump || isCurrent;
            button.Opacity = canJump || isCurrent ? 1.0 : 0.45;
            button.FontWeight = isCurrent ? FontWeights.Bold : FontWeights.SemiBold;
            button.Background = isCurrent || isAnswered
                ? (Brush)FindResource("SelectionBackground")
                : (Brush)FindResource("WindowBackground");
            button.BorderBrush = isCurrent
                ? (Brush)FindResource("AccentPrimary")
                : Brushes.Transparent;
            button.BorderThickness = isCurrent ? new Thickness(2) : new Thickness(0);
            button.Foreground = (Brush)FindResource("TextColor");

            if (TryGetNavFlagMark(button) is Polygon flagMark)
                flagMark.Visibility = isFlagged ? Visibility.Visible : Visibility.Collapsed;

            if (examMode && !isCurrent)
            {
                button.ToolTip = LocalizationService.GetString("QuizExamNavLockedTooltip");
            }
            else if (isFlagged)
            {
                button.ToolTip = string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizationService.GetString("QuizFlaggedNavTooltipFormat"),
                    index + 1);
            }
            else
            {
                button.ToolTip = string.Format(
                    CultureInfo.CurrentCulture,
                    LocalizationService.GetString("QuizJumpToQuestionTooltipFormat"),
                    index + 1);
            }
        }
    }

    private void UpdateFlagQuestionButton()
    {
        var isFlagged = _flaggedQuestions.Contains(_currentQuestionIndex);
        if (FindName("FlagQuestionLabel") is TextBlock label)
        {
            label.Text = LocalizationService.GetString(isFlagged ? "QuizUnflagQuestion" : "QuizFlagQuestion");
        }

        if (FindName("FlagQuestionIcon") is AppIcon icon)
        {
            icon.Foreground = isFlagged
                ? (Brush)FindResource("AccentDanger")
                : (Brush)FindResource("TextColor");
        }

        if (FindName("FlagQuestionButton") is Button flagButton)
        {
            flagButton.Background = isFlagged
                ? (Brush)FindResource("AccentDangerSubtle")
                : (Brush)FindResource("CardBackground");
            flagButton.Foreground = isFlagged
                ? (Brush)FindResource("AccentDangerSubtleForeground")
                : (Brush)FindResource("TextColor");
        }
    }

    private void FlagQuestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isResultsView)
            return;

        if (!_flaggedQuestions.Add(_currentQuestionIndex))
            _flaggedQuestions.Remove(_currentQuestionIndex);

        UpdateFlagQuestionButton();
        RefreshQuestionNavigator();
    }

    private bool IsQuestionAnswered(int index)
    {
        if (!_userAnswers.TryGetValue(index, out var selected) || selected.Count == 0)
            return false;

        var question = _quiz.Questions[index];
        if (question.Type == QuizQuestionType.PlainText)
            return TryGetPlainTextAnswer(selected, out var text) && !string.IsNullOrWhiteSpace(text);

        return true;
    }

    private void QuestionNavButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isResultsView || sender is not Button button || button.Tag is not int index)
            return;

        if (_quiz.ExamMode)
            return;

        if (index < 0 || index >= (_quiz.Questions?.Count ?? 0) || index == _currentQuestionIndex)
            return;

        var direction = index > _currentQuestionIndex ? 1 : -1;
        _currentQuestionIndex = index;
        LoadQuestionAnimated(direction);
    }

    private FrameworkElement CreateOptionButtonContent(int optionIndex, string optionText)
    {
        var question = _quiz.Questions[_currentQuestionIndex];
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var chip = new Border
        {
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (TryFindResource("QuizOptionLetterChipBaseStyle") is Style chipStyle)
            chip.Style = chipStyle;

        // Local radius wins over the base style default so multi stays rounded-square / single stays circle.
        chip.CornerRadius = new CornerRadius(QuizOptionMarker.MarkerCornerRadius(question.Type));

        var letter = new TextBlock
        {
            Text = QuizOptionMarker.LetterFromIndex(optionIndex),
            FontSize = TypeScale.Px(TypeScale.Caption, 12),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextColor")
        };
        chip.Child = letter;
        grid.Children.Add(chip);

        var text = new TextBlock
        {
            Text = optionText,
            TextWrapping = TextWrapping.Wrap,
            FontSize = TypeScale.Px(TypeScale.Meta, 13),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextColor")
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private void RestoreSelectedAnswer()
    {
        _userAnswers.TryGetValue(_currentQuestionIndex, out var selectedOptions);
        UpdateOptionVisuals(selectedOptions ?? new List<QuizOption>(), animate: false);
    }

    private Border CreatePlainTextAnswerCard(QuizQuestion question)
    {
        var answerBorder = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var content = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = LocalizationService.GetString("QuizPlainTextYourAnswerLabel"),
            FontSize = TypeScale.Px(TypeScale.Caption, 12),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextColorSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(label);

        var charCountLabel = new TextBlock
        {
            FontSize = TypeScale.Px(TypeScale.Caption, 12),
            Foreground = (Brush)FindResource("TextColorSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(charCountLabel, 1);
        header.Children.Add(charCountLabel);
        content.Children.Add(header);

        var inputBorder = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            MinHeight = 146
        };
        inputBorder.SetResourceReference(StyleProperty, "ChromeFieldBorderStyle");
        // Inset on the white question plate so the field (and its focus ring) read clearly.
        inputBorder.SetResourceReference(BackgroundProperty, "WindowBackground");
        FieldFocusRingBehavior.SetIsEnabled(inputBorder, true);

        var inputGrid = new Grid();
        var textBox = new TextBox
        {
            Name = "PlainTextAnswerBox",
            Padding = new Thickness(0),
            FontSize = TypeScale.Px(TypeScale.SectionTitle, 15),
            Foreground = (Brush)FindResource("TextColor"),
            CaretBrush = (Brush)FindResource("TextColor"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 130,
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = "PlainTextQuestion"
        };
        if (question.PlainTextCharLimit > 0)
            textBox.MaxLength = question.PlainTextCharLimit;
        if (TryFindResource("EmbeddedFieldTextBoxStyle") is Style embeddedStyle)
            textBox.Style = embeddedStyle;

        if (_userAnswers.TryGetValue(_currentQuestionIndex, out var previousAnswer)
            && TryGetPlainTextAnswer(previousAnswer, out var restoredAnswer))
        {
            textBox.Text = restoredAnswer;
        }

        var placeholder = new TextBlock
        {
            Text = LocalizationService.GetString("QuizPlainTextPracticePlaceholder"),
            FontSize = TypeScale.Px(TypeScale.SectionTitle, 15),
            Foreground = (Brush)FindResource("TextColorSecondary"),
            Opacity = 0.72,
            IsHitTestVisible = false,
            Margin = new Thickness(0, 1, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrEmpty(textBox.Text) ? Visibility.Visible : Visibility.Collapsed
        };

        void UpdatePlainTextState()
        {
            charCountLabel.Text = question.PlainTextCharLimit > 0
                ? string.Format(LocalizationService.GetString("QuizCharactersWithLimitFormat"), textBox.Text.Length, question.PlainTextCharLimit)
                : string.Format(LocalizationService.GetString("QuizCharactersFormat"), textBox.Text.Length);
            placeholder.Visibility = string.IsNullOrEmpty(textBox.Text) && !textBox.IsKeyboardFocusWithin
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        textBox.TextChanged += (s, e) =>
        {
            UpdatePlainTextState();
            SavePlainTextAnswer(textBox.Text);
        };
        textBox.GotKeyboardFocus += (s, e) => UpdatePlainTextState();
        textBox.LostKeyboardFocus += (s, e) => UpdatePlainTextState();

        inputGrid.Children.Add(textBox);
        inputGrid.Children.Add(placeholder);
        inputBorder.Child = inputGrid;
        content.Children.Add(inputBorder);
        answerBorder.Child = content;
        UpdatePlainTextState();
        return answerBorder;
    }

    private void SavePlainTextAnswer(string answer)
    {
        if (!_userAnswers.ContainsKey(_currentQuestionIndex))
            _userAnswers[_currentQuestionIndex] = new List<QuizOption>();

        // Store plain text answer as a special marker option
        _userAnswers[_currentQuestionIndex].Clear();
        _userAnswers[_currentQuestionIndex].Add(new QuizOption 
        { 
            Text = $"{PlainTextAnswerMarker}{answer}",
            IsCorrect = false 
        });
        RefreshQuestionNavigator();
    }

    private void UpdateNavigationButtons()
    {
        if (FindName("PreviousButton") is Button prevButton)
            prevButton.IsEnabled = _currentQuestionIndex > 0 && !_quiz.ExamMode;

        var showSubmit = _currentQuestionIndex == _quiz.Questions.Count - 1;
        CrossFadeFooterButtons(showSubmit);

        if (FindName("DoneButton") is Button doneButton)
            SetFooterButtonVisible(doneButton, false);
    }

    private void CrossFadeFooterButtons(bool showSubmit)
    {
        if (FindName("NextButton") is not Button nextButton
            || FindName("SubmitButton") is not Button submitButton)
            return;

        // Instant swap, never gated behind a fade's completion callback. Next and Submit share
        // the same grid cell, so whichever one is under the cursor right after a click also picks
        // up hover/press animations that steal its RenderTransitionClock - that silently canceled
        // the old crossfade mid-flight and left the swap (which only ran in the completion
        // callback) stuck, so quiz-takers could reach the last question but never see Submit.
        nextButton.Visibility = showSubmit ? Visibility.Collapsed : Visibility.Visible;
        nextButton.Opacity = 1;
        submitButton.Visibility = showSubmit ? Visibility.Visible : Visibility.Collapsed;
        submitButton.Opacity = 1;
    }

    private static void SetFooterButtonVisible(Button button, bool visible)
    {
        // Same reasoning as CrossFadeFooterButtons above - Visibility must never wait on an
        // animation callback that a colliding hover/press effect on the same button can cancel.
        button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        button.Opacity = 1;
    }

    private void QuizModeWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isResultsView)
        {
            if (e.Key == Key.Enter && FindName("DoneButton") is Button doneButton && doneButton.Visibility == Visibility.Visible)
            {
                DoneButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }

            return;
        }

        // Check if we're in a PlainText TextBox (Ctrl+Enter to submit from TextBox)
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (FindName("OptionsPanel") is StackPanel optionsPanel)
            {
                var question = _quiz.Questions[_currentQuestionIndex];
                if (question.Type == QuizQuestionType.PlainText)
                {
                    // Ctrl+Enter in PlainText box moves to next question
                    if (FindName("NextButton") is Button nextButton && nextButton.Visibility == Visibility.Visible && nextButton.IsEnabled)
                    {
                        NextButton_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        return;
                    }
                    else if (FindName("SubmitButton") is Button submitButton && submitButton.Visibility == Visibility.Visible && submitButton.IsEnabled)
                    {
                        SubmitButton_Click(this, new RoutedEventArgs());
                        e.Handled = true;
                        return;
                    }
                }
            }
        }

        if (e.Key == Key.Enter)
        {
            if (FindName("NextButton") is Button nextButton && nextButton.Visibility == Visibility.Visible && nextButton.IsEnabled)
            {
                NextButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (FindName("SubmitButton") is Button submitButton && submitButton.Visibility == Visibility.Visible && submitButton.IsEnabled)
            {
                SubmitButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }

            return;
        }

        if ((e.Key >= Key.D1 && e.Key <= Key.D9) || (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9))
        {
            int optionIndex = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
            if (TrySelectOption(optionIndex))
                e.Handled = true;
        }
    }

    private bool TrySelectOption(int optionIndex)
    {
        if (FindName("OptionsPanel") is not StackPanel optionsPanel)
            return false;

        if (optionIndex < 0 || optionIndex >= optionsPanel.Children.Count)
            return false;

        if (optionsPanel.Children[optionIndex] is not Button optionButton)
            return false;

        OptionButton_Click(optionButton, new RoutedEventArgs());
        return true;
    }

    private void OptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button clickedButton || clickedButton.Tag is not QuizOption clickedOption)
            return;

        var question = _quiz.Questions[_currentQuestionIndex];
        if (question == null) return;

        bool isMultipleChoice = question.Type == QuizQuestionType.MultipleChoice;

        if (!_userAnswers.ContainsKey(_currentQuestionIndex))
            _userAnswers[_currentQuestionIndex] = new List<QuizOption>();

        var selectedOptions = _userAnswers[_currentQuestionIndex];

        if (isMultipleChoice)
        {
            if (selectedOptions.Contains(clickedOption))
                selectedOptions.Remove(clickedOption);
            else
                selectedOptions.Add(clickedOption);
        }
        else
        {
            selectedOptions.Clear();
            selectedOptions.Add(clickedOption);
        }

        UpdateOptionVisuals(selectedOptions);
        RefreshQuestionNavigator();
    }

    private void UpdateOptionVisuals(List<QuizOption> selectedOptions, bool animate = true)
    {
        if (FindName("OptionsPanel") is not StackPanel optionsPanel)
            return;

        foreach (var child in optionsPanel.Children)
        {
            if (child is not Button optButton || optButton.Tag is not QuizOption opt)
                continue;

            var isSelected = selectedOptions.Contains(opt);
            ApplyOptionSelectionVisual(optButton, isSelected);

            if (animate)
                AnimateOptionSelection(optButton, isSelected);
            else
                optButton.BorderThickness = isSelected ? new Thickness(2) : new Thickness(0);
        }
    }

    private void ApplyOptionSelectionVisual(Button button, bool isSelected)
    {
        button.Background = isSelected
            ? (Brush)FindResource("SelectionBackground")
            : (Brush)FindResource("WindowBackground");
        button.BorderBrush = isSelected
            ? (Brush)FindResource("AccentPrimary")
            : Brushes.Transparent;
        button.FontWeight = FontWeights.Normal;

        if (TryGetOptionLetterChip(button) is not Border chip)
            return;

        chip.Background = isSelected
            ? (Brush)FindResource("SelectionBackground")
            : (Brush)FindResource("InputSurface");
        chip.BorderBrush = isSelected
            ? (Brush)FindResource("AccentPrimary")
            : Brushes.Transparent;
        chip.BorderThickness = isSelected ? new Thickness(1.5) : new Thickness(0);
    }

    private static Border? TryGetOptionLetterChip(Button button)
        => button.Content is Grid grid && grid.Children.Count > 0
            ? grid.Children[0] as Border
            : null;

    private static void AnimateOptionSelection(Button button, bool isSelected)
    {
        var targetThickness = isSelected ? 2.0 : 0.0;
        var currentThickness = button.BorderThickness.Left;

        if (Math.Abs(currentThickness - targetThickness) < 0.01)
            return;

        if (UiMotion.PrefersReducedMotion)
        {
            button.BorderThickness = new Thickness(targetThickness);
            return;
        }

        // Border rim only — no scale pop (wide option rows make even 1% scale feel like a jump).
        UiMotion.Run(
            button,
            new[]
            {
                UiMotion.Track(
                    UiMotion.QuickMs,
                    UiMotion.OutCubic,
                    currentThickness,
                    targetThickness,
                    value => button.BorderThickness = new Thickness(value))
            });
    }
    private void LoadHintSection()
    {
        var question = _quiz.Questions[_currentQuestionIndex];
        var hasHint = !string.IsNullOrWhiteSpace(question.Hint);

        if (FindName("HintSection") is StackPanel hintSection)
            hintSection.Visibility = hasHint ? Visibility.Visible : Visibility.Collapsed;

        if (FindName("HintTextBorder") is Border hintBorder)
            hintBorder.Visibility = Visibility.Collapsed;

        if (FindName("ShowHintButton") is Button showHintBtn)
            showHintBtn.Visibility = Visibility.Visible;

        if (FindName("HintTextBlock") is TextBlock hintText)
            hintText.Text = question.Hint ?? string.Empty;
    }

    private void ShowHintButton_Click(object sender, RoutedEventArgs e)
    {
        _hintUsedQuestions.Add(_currentQuestionIndex);

        if (FindName("HintTextBorder") is Border hintBorder)
        {
            hintBorder.Visibility = Visibility.Visible;
            hintBorder.Opacity = 0;
            UiMotion.Fade(hintBorder, 1, UiMotion.OpenMs, UiMotion.OutQuad);
        }

        if (FindName("ShowHintButton") is Button showHintBtn)
            showHintBtn.Visibility = Visibility.Collapsed;
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isResultsView)
        {
            AnimateReturnFromResults();
            return;
        }

        if (_currentQuestionIndex > 0)
        {
            _currentQuestionIndex--;
            LoadQuestionAnimated(-1);
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentQuestionIndex < _quiz.Questions.Count - 1)
        {
            _currentQuestionIndex++;
            LoadQuestionAnimated(1);
        }
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        var result = ModernDialog.Show(
            this,
            LocalizationService.GetString("QuizSubmitDialogTitle"),
            LocalizationService.GetString("QuizSubmitDialogMessage"),
            ModernDialogTone.Question,
            LocalizationService.GetString("QuizSubmitDialogConfirm"),
            LocalizationService.GetString("QuizSubmitDialogCancel"),
            defaultChoice: ModernDialogResult.Primary);

        if (result == ModernDialogResult.Primary)
            ShowResults();
    }

    private void DoneButton_Click(object sender, RoutedEventArgs e) => Close();

    private bool IsAnswerCorrect(QuizQuestion question, List<QuizOption> userSelected)
    {
        if (question.Type == QuizQuestionType.PlainText)
        {
            if (!TryGetPlainTextAnswer(userSelected, out var userText))
                return false;

            var normalizedUserText = NormalizePlainTextAnswer(userText, question.PlainTextIgnorePunctuation);
            var normalizedExpectedText = NormalizePlainTextAnswer(question.PlainTextAnswer, question.PlainTextIgnorePunctuation);
            var comparison = question.PlainTextMatchCase
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            return !string.IsNullOrEmpty(normalizedUserText)
                && !string.IsNullOrEmpty(normalizedExpectedText)
                && string.Equals(normalizedUserText, normalizedExpectedText, comparison);
        }
        else
        {
            // For multiple choice questions, check if selected options match correct options
            var correctOptions = question.Options?.Where(o => o.IsCorrect).ToList() ?? new List<QuizOption>();
            return userSelected.Count == correctOptions.Count &&
                   userSelected.All(opt => correctOptions.Contains(opt));
        }
    }

    // Moodle-style multi-response scoring: reward true positives, penalize false positives, floor
    // at zero - picking every option can never beat picking only the correct ones. Only applies
    // when the quiz owner opted into PartialCreditMultipleChoice; every other question type stays
    // strictly binary via IsAnswerCorrect.
    private double GetEarnedPoints(QuizQuestion question, List<QuizOption> userSelected)
    {
        if (question.Type == QuizQuestionType.MultipleChoice && _quiz.PartialCreditMultipleChoice)
        {
            var correctOptions = question.Options?.Where(o => o.IsCorrect).ToList() ?? new List<QuizOption>();
            if (correctOptions.Count == 0)
                return IsAnswerCorrect(question, userSelected) ? 1.0 : 0.0;

            var correctlySelected = userSelected.Count(o => o.IsCorrect);
            var incorrectlySelected = userSelected.Count(o => !o.IsCorrect);
            var score = (correctlySelected - incorrectlySelected) / (double)correctOptions.Count;
            return Math.Clamp(score, 0.0, 1.0);
        }

        return IsAnswerCorrect(question, userSelected) ? 1.0 : 0.0;
    }

    private static bool TryGetPlainTextAnswer(List<QuizOption> userSelected, out string answer)
    {
        answer = string.Empty;
        if (userSelected.Count == 0)
            return false;

        var storedAnswer = userSelected[0].Text;
        if (string.IsNullOrEmpty(storedAnswer) || !storedAnswer.StartsWith(PlainTextAnswerMarker, StringComparison.Ordinal))
            return false;

        answer = storedAnswer[PlainTextAnswerMarker.Length..];
        return true;
    }

    private static string NormalizePlainTextAnswer(string? value, bool ignorePunctuation)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = ignorePunctuation
            ? new string(value.Select(ch => char.IsPunctuation(ch) || char.IsSymbol(ch) ? ' ' : ch).ToArray())
            : value;

        return string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private void ShowResults()
    {
        _timer?.Stop();
        _isResultsView = true;

        var correctCount = 0;
        var earnedPoints = 0.0;
        var questions = _quiz.Questions ?? new List<QuizQuestion>();
        var totalQuestions = questions.Count;

        _wrongQuestionIndices.Clear();
        for (int i = 0; i < totalQuestions; i++)
        {
            var question = questions[i];
            if (!_userAnswers.TryGetValue(i, out var userSelected))
            {
                _wrongQuestionIndices.Add(i);
                question.LastAttemptCorrect = false;
                continue;
            }

            bool isCorrect = IsAnswerCorrect(question, userSelected);
            if (isCorrect) correctCount++;
            else _wrongQuestionIndices.Add(i);
            earnedPoints += GetEarnedPoints(question, userSelected);
            question.LastAttemptCorrect = isCorrect;
        }

        var displayTime2 = _isCountdown ? TimeSpan.FromSeconds(_timeLimitSeconds) - _remainingTime : _elapsedTime;

        var passingScore = Math.Clamp(_quiz.PassingScorePercent, 0, 100);

        LastAttempt = new QuizAttempt
        {
            Date = DateTime.Now,
            CorrectCount = correctCount,
            TotalQuestions = totalQuestions,
            EarnedPoints = earnedPoints,
            TimeTaken = displayTime2,
            QuizTitle = _quiz.Title ?? string.Empty,
            PassingScorePercent = passingScore,
        };

        // Percentage folds in partial credit when present (see QuizAttempt.Percentage) - reuse it
        // here so the live results screen and the saved attempt-history entry always agree.
        var percentage = LastAttempt.Percentage;
        var passed = percentage >= passingScore;

        if (!_attemptSubmittedFired)
        {
            _attemptSubmittedFired = true;
            AttemptSubmitted?.Invoke(LastAttempt);
        }


        var displayTime = _isCountdown ? TimeSpan.FromSeconds(_timeLimitSeconds) - _remainingTime : _elapsedTime;
        AnimateResultsTransition(
            () => ApplyResultsVisibility(
                correctCount,
                totalQuestions,
                percentage,
                passed,
                passingScore,
                displayTime,
                questions),
            passed);
    }

    private void ApplyResultsVisibility(
        int correctCount,
        int totalQuestions,
        double percentage,
        bool passed,
        int passingScore,
        TimeSpan displayTime,
        List<QuizQuestion> questions)
    {
        if (FindName("QuestionView") is Border qv) qv.Visibility = Visibility.Collapsed;
        if (FindName("ResultsView") is Border rv) rv.Visibility = Visibility.Visible;
        RefreshQuestionNavigator();
        if (FindName("ProgressText") is TextBlock pt) pt.Text = string.Format(LocalizationService.GetString("QuizResultsLabelFormat"), correctCount, totalQuestions);
        if (FindName("TimerText") is TextBlock tt) tt.Text = displayTime.ToString(@"hh\:mm\:ss");

        var prepareScoreReveal = !UiMotion.PrefersReducedMotion;
        if (FindName("ResultsTitleText") is TextBlock rt)
        {
            rt.Text = LocalizationService.GetString(passed ? "QuizPassedResult" : "QuizFailedResult");
            if (prepareScoreReveal)
                rt.Opacity = 0;
        }

        if (FindName("ResultsScoreText") is TextBlock rs)
        {
            rs.Text = string.Format(LocalizationService.GetString("QuizScoreWithPassingFormat"), correctCount, totalQuestions, percentage, passingScore);
            if (prepareScoreReveal)
                rs.Opacity = 0;
        }

        if (FindName("ResultsTimeText") is TextBlock rtime)
        {
            rtime.Text = string.Format(LocalizationService.GetString("QuizTimeFormat"), displayTime.ToString(@"hh\:mm\:ss"));
            if (prepareScoreReveal)
                rtime.Opacity = 0;
        }
        if (FindName("PreviousButton") is Button pb) SetFooterButtonVisible(pb, false);
        if (FindName("NextButton") is Button nb) SetFooterButtonVisible(nb, false);
        if (FindName("SubmitButton") is Button sb) SetFooterButtonVisible(sb, false);
        if (FindName("DoneButton") is Button db)
            SetFooterButtonVisible(db, true);

        // Retrying within the results screen would defeat exam mode's "one attempt" promise.
        if (FindName("RetryAllButton") is Button rab)
            UiMotion.AnimateInlineReveal(rab, !_quiz.ExamMode);
        if (FindName("RetryWrongButton") is Button rwb)
            UiMotion.AnimateInlineReveal(rwb, !_quiz.ExamMode && _wrongQuestionIndices.Count > 0);
        if (FindName("ResultsQuestionsPanel") is StackPanel rp)
        {
            rp.Children.Clear();
            for (int i = 0; i < questions.Count; i++)
            {
                var q = questions[i];
                var userSel = _userAnswers.TryGetValue(i, out var ans) ? ans : new List<QuizOption>();
                var isCorrect = IsAnswerCorrect(q, userSel);

                var card = new Border
                {
                    Background = isCorrect
                        ? (Brush)FindResource("AccentSuccessSubtle")
                        : (Brush)FindResource("AccentDangerSubtle"),
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(16),
                    Margin = new Thickness(0, 0, 0, 12)
                };

                var content = new StackPanel();

                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var qText = new TextBlock
                {
                    Text = string.Format(LocalizationService.GetString("QuizQuestionPrefixFormat"), i + 1, q.Question),
                    FontSize = TypeScale.Px(TypeScale.SectionTitle, 15),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = isCorrect
                        ? (Brush)FindResource("AccentSuccessSubtleForeground")
                        : (Brush)FindResource("AccentDangerSubtleForeground"),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(qText, 0);
                header.Children.Add(qText);

                var statusIcon = new AppIcon
                {
                    Kind = isCorrect ? AppIconKind.Checkmark : AppIconKind.Close,
                    Width = 18,
                    Height = 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                    Foreground = isCorrect
                        ? (Brush)FindResource("AccentSuccessSubtleForeground")
                        : (Brush)FindResource("AccentDangerSubtleForeground")
                };
                Grid.SetColumn(statusIcon, 1);
                header.Children.Add(statusIcon);

                content.Children.Add(header);

                // Handle display based on question type
                if (q.Type == QuizQuestionType.PlainText)
                {
                    // Extract the plain text answer
                    var userAnswer = TryGetPlainTextAnswer(userSel, out var plainTextAnswer) && !string.IsNullOrWhiteSpace(plainTextAnswer)
                        ? plainTextAnswer
                        : LocalizationService.GetString("QuizNoAnswer");

                    var userAnsBlock = new TextBlock
                    {
                        Text = string.Format(
                            LocalizationService.GetString("QuizYourAnswerFormat"),
                            userAnswer),
                        FontSize = TypeScale.Px(TypeScale.Meta, 13),
                        Foreground = (Brush)FindResource("TextColor"),
                        Margin = new Thickness(0, 8, 0, 4),
                        TextWrapping = TextWrapping.Wrap
                    };
                    content.Children.Add(userAnsBlock);

                    // Show correct answer if incorrect
                    if (!isCorrect && !string.IsNullOrEmpty(q.PlainTextAnswer))
                    {
                        var correctAnsBlock = new TextBlock
                        {
                            Text = string.Format(
                                LocalizationService.GetString("QuizCorrectAnswerFormat"),
                                q.PlainTextAnswer),
                            FontSize = TypeScale.Px(TypeScale.Meta, 13),
                            Foreground = (Brush)FindResource("AccentSuccessSubtleForeground"),
                            Margin = new Thickness(0, 0, 0, 4),
                            TextWrapping = TextWrapping.Wrap
                        };
                        content.Children.Add(correctAnsBlock);
                    }
                }
                else
                {
                    // Multiple choice answer display with A/B/C prefixes matching take UI
                    var options = q.Options ?? new List<QuizOption>();
                    var correctOpts = options.Where(o => o.IsCorrect).ToList();

                    var userAnswerText = userSel.Count > 0
                        ? string.Join(", ", userSel.Select(o =>
                        {
                            var index = options.IndexOf(o);
                            return index >= 0
                                ? QuizOptionMarker.FormatWithLetter(index, o.Text)
                                : o.Text;
                        }))
                        : LocalizationService.GetString("QuizNoAnswer");

                    var userAns = new TextBlock
                    {
                        Text = string.Format(
                            LocalizationService.GetString("QuizYourAnswerFormat"),
                            userAnswerText),
                        FontSize = TypeScale.Px(TypeScale.Meta, 13),
                        Foreground = (Brush)FindResource("TextColor"),
                        Margin = new Thickness(0, 8, 0, 4),
                        TextWrapping = TextWrapping.Wrap
                    };
                    content.Children.Add(userAns);

                    if (!isCorrect && correctOpts.Count > 0)
                    {
                        var correctAnswerText = string.Join(", ", correctOpts.Select(o =>
                        {
                            var index = options.IndexOf(o);
                            return index >= 0
                                ? QuizOptionMarker.FormatWithLetter(index, o.Text)
                                : o.Text;
                        }));

                        var correctAns = new TextBlock
                        {
                            Text = string.Format(LocalizationService.GetString("QuizCorrectAnswerFormat"), correctAnswerText),
                            FontSize = TypeScale.Px(TypeScale.Meta, 13),
                            Foreground = (Brush)FindResource("AccentSuccessSubtleForeground"),
                            Margin = new Thickness(0, 0, 0, 4),
                            TextWrapping = TextWrapping.Wrap
                        };
                        content.Children.Add(correctAns);
                    }
                }

                if (!string.IsNullOrWhiteSpace(q.Explanation))
                {
                    var expl = new TextBlock
                    {
                        Text = string.Format(LocalizationService.GetString("QuizExplanationFormat"), q.Explanation),
                        FontSize = TypeScale.Px(TypeScale.Caption, 12),
                        Foreground = (Brush)FindResource("TextColorSecondary"),
                        Margin = new Thickness(0, 8, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        FontStyle = FontStyles.Italic
                    };
                    content.Children.Add(expl);
                }

                card.Child = content;
                rp.Children.Add(card);
                AnimateResultsReviewCard(card, i);
            }
        }
    }

    private static void AnimateResultsReviewCard(FrameworkElement card, int index)
    {
        if (UiMotion.PrefersReducedMotion)
            return;

        card.Opacity = 0;
        var translate = UiMotion.EnsureTranslate(card);
        translate.Y = UiMotion.ContentLiftY;
        var delay = Math.Min(index * 35, 280);

        UiMotion.RunComposited(
            card,
            new[]
            {
                UiMotion.Track(UiMotion.QuickMs, UiMotion.OutCubic, 0, 1, value => card.Opacity = value, startDelayMs: delay),
                UiMotion.Track(UiMotion.QuickMs, UiMotion.OutCubic, UiMotion.ContentLiftY, 0, value => translate.Y = value, startDelayMs: delay)
            });
    }

    private void LoadQuestionAnimated(int direction)
    {
        if (direction == 0 || UiMotion.PrefersReducedMotion || FindName("QuestionView") is not FrameworkElement questionView)
        {
            LoadQuestion();
            return;
        }

        var translate = UiMotion.EnsureTranslate(questionView);
        var exitOffset = direction > 0 ? -UiMotion.NavShiftX : UiMotion.NavShiftX;
        var opacityStart = questionView.Opacity;

        UiMotion.RunComposited(
            questionView,
            new[]
            {
                UiMotion.Track(UiMotion.QuickMs, UiMotion.InCubic, opacityStart, 0, value => questionView.Opacity = value),
                UiMotion.Track(UiMotion.QuickMs, UiMotion.InCubic, 0, exitOffset, value => translate.X = value)
            },
            () =>
            {
                LoadQuestion();
                translate.X = -exitOffset;
                questionView.Opacity = 0;
                UiMotion.RunComposited(
                    questionView,
                    new[]
                    {
                        UiMotion.Track(UiMotion.HoverInMs, UiMotion.OutCubic, 0, 1, value => questionView.Opacity = value),
                        UiMotion.Track(UiMotion.HoverInMs, UiMotion.OutCubic, -exitOffset, 0, value => translate.X = value)
                    });
            });
    }

    private void AnimateReturnFromResults()
    {
        if (FindName("ResultsView") is not FrameworkElement resultsView
            || FindName("QuestionView") is not FrameworkElement questionView
            || UiMotion.PrefersReducedMotion)
        {
            ApplyReturnFromResultsState();
            LoadQuestion();
            return;
        }

        UiMotion.RunComposited(
            resultsView,
            new[]
            {
                UiMotion.Track(UiMotion.CloseMs, UiMotion.InCubic, resultsView.Opacity, 0, value => resultsView.Opacity = value)
            },
            () =>
            {
                ApplyReturnFromResultsState();
                LoadQuestion();
                questionView.Opacity = 0;
                var translate = UiMotion.EnsureTranslate(questionView);
                translate.Y = UiMotion.PanelLiftY;
                UiMotion.RunComposited(
                    questionView,
                    new[]
                    {
                        UiMotion.Track(UiMotion.OpenMs, UiMotion.OutCubic, 0, 1, value => questionView.Opacity = value),
                        UiMotion.Track(UiMotion.OpenMs, UiMotion.OutCubic, UiMotion.PanelLiftY, 0, value => translate.Y = value)
                    });
            });
    }

    private void ApplyReturnFromResultsState()
    {
        _isResultsView = false;
        _timer?.Start();
        if (FindName("ResultsView") is Border rv)
        {
            rv.Visibility = Visibility.Collapsed;
            rv.Opacity = 1;
        }

        if (FindName("QuestionView") is Border qv)
            qv.Visibility = Visibility.Visible;
        if (FindName("DoneButton") is Button db)
            SetFooterButtonVisible(db, false);
        if (FindName("RetryAllButton") is Button rab)
            UiMotion.AnimateInlineReveal(rab, false);
        if (FindName("RetryWrongButton") is Button rwb)
            UiMotion.AnimateInlineReveal(rwb, false);

        RefreshQuestionNavigator();
    }

    private void AnimateResultsTransition(Action applyResults, bool passed)
    {
        void PlayCelebration()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                if (passed && CelebrationOverlay != null)
                    StudyCelebration.PlayConfetti(CelebrationOverlay);

                StudyCelebration.RevealScore(
                    FindName("ResultsTitleText") as FrameworkElement,
                    FindName("ResultsScoreText") as FrameworkElement,
                    FindName("ResultsTimeText") as FrameworkElement);
            });
        }

        if (FindName("QuestionView") is not FrameworkElement questionView
            || FindName("ResultsView") is not FrameworkElement resultsView
            || UiMotion.PrefersReducedMotion)
        {
            applyResults();
            PlayCelebration();
            return;
        }

        UiMotion.RunComposited(
            questionView,
            new[]
            {
                UiMotion.Track(UiMotion.CloseMs, UiMotion.InCubic, questionView.Opacity, 0, value => questionView.Opacity = value)
            },
            () =>
            {
                applyResults();
                resultsView.Visibility = Visibility.Visible;
                resultsView.Opacity = 0;
                var scale = UiMotion.EnsureScale(resultsView, 0.97);
                scale.ScaleX = 0.97;
                scale.ScaleY = 0.97;
                UiMotion.RunComposited(
                    resultsView,
                    new[]
                    {
                        UiMotion.Track(UiMotion.OpenMs, UiMotion.OutCubic, 0, 1, value => resultsView.Opacity = value),
                        UiMotion.Track(UiMotion.OpenMs, UiMotion.OutCubic, 0.97, 1, value =>
                        {
                            scale.ScaleX = value;
                            scale.ScaleY = value;
                        })
                    },
                    PlayCelebration);
            });
    }

    private void RetryAllButton_Click(object sender, RoutedEventArgs e)
    {
        RestartSession(_quiz);
    }

    private void RetryWrongButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wrongQuestionIndices.Count == 0)
            return;

        var wrongQuestions = _wrongQuestionIndices
            .Select(i => _quiz.Questions[i])
            .ToList();

        var wrongOnlyQuiz = new QuizDocument
        {
            Id = _quiz.Id,
            Title = _quiz.Title + " (Wrong Only)",
            Questions = wrongQuestions,
            PassingScorePercent = _quiz.PassingScorePercent,
            AiModelDisplayName = _quiz.AiModelDisplayName,
            Tags = _quiz.Tags,
            ShuffleQuestions = _quiz.ShuffleQuestions,
            ShuffleOptions = _quiz.ShuffleOptions,
            PartialCreditMultipleChoice = _quiz.PartialCreditMultipleChoice
        };

        RestartSession(wrongOnlyQuiz);
    }

    private void RestartSession(QuizDocument quiz)
    {
        _timer?.Stop();
        if (CelebrationOverlay != null)
            StudyCelebration.Stop(CelebrationOverlay);

        _quiz = quiz ?? throw new ArgumentNullException(nameof(quiz));
        ApplyShufflePreferences(_quiz);
        _currentQuestionIndex = 0;
        _userAnswers.Clear();
        _flaggedQuestions.Clear();
        _hintUsedQuestions.Clear();
        _wrongQuestionIndices.Clear();
        LastAttempt = null;
        // Each in-session retry (Retry All / Retry Wrong Only) is its own attempt, and its
        // per-question results still need to reach QuizPreviewWindow via AttemptSubmitted.
        _attemptSubmittedFired = false;

        ResetTimerForNewSession();
        ApplyReturnFromResultsState();

        if (FindName("ResultsQuestionsPanel") is StackPanel resultsPanel)
            resultsPanel.Children.Clear();

        RebuildQuestionNavigator();
        LoadQuestion();

        if (FindName("QuestionView") is FrameworkElement questionView)
            UiMotion.AnimateContentEnter(questionView, durationMs: UiMotion.QuickMs, offsetY: UiMotion.SubtleLiftY);
    }

    private void ResetTimerForNewSession()
    {
        _isCountdown = _timeLimitSeconds > 0;
        _elapsedTime = TimeSpan.Zero;
        _remainingTime = _isCountdown ? TimeSpan.FromSeconds(_timeLimitSeconds) : TimeSpan.Zero;
        _timerWarningActive = false;

        if (FindName("TimerText") is TextBlock timerText)
        {
            UiMotion.Stop(timerText);
            timerText.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
        }

        UpdateTimerDisplay(_isCountdown ? _remainingTime : _elapsedTime);
    }

}
