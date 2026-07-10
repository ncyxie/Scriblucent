using NoteCards.Animations;
using NoteCards.Behaviors;
using NoteCards.Localization;
using NoteCards;
using NoteCards.Models;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace NoteCards.Views;

public partial class QuizModeWindow : Window
{
    private static readonly Color CorrectAnswerBackgroundColor = Color.FromRgb(240, 253, 244);
    private static readonly Color CorrectAnswerBorderColor = Color.FromRgb(16, 185, 129);
    private static readonly Color IncorrectAnswerBackgroundColor = Color.FromRgb(254, 242, 242);
    private static readonly Color IncorrectAnswerBorderColor = Color.FromRgb(220, 38, 38);
    private const string PlainTextAnswerMarker = "__PLAINTEXT__:";

    private readonly QuizDocument _quiz;
    private int _currentQuestionIndex;
    private readonly Dictionary<int, List<QuizOption>> _userAnswers;
    private DispatcherTimer? _timer;
    private TimeSpan _elapsedTime;
    private bool _isResultsView;
    private readonly int _timeLimitSeconds; // 0 = be limito
    private bool _isCountdown;
    private TimeSpan _remainingTime;
    private readonly HashSet<int> _hintUsedQuestions = new();
    public QuizAttempt? LastAttempt { get; private set; }
    private List<int> _wrongQuestionIndices = new();
    private bool _timerWarningActive;

    public QuizModeWindow(QuizDocument quiz, int timeLimitSeconds = 0)
    {
        InitializeComponent();
        NoteCards.Services.WindowThemeService.Register(this);

        _quiz = quiz ?? throw new System.ArgumentNullException(nameof(quiz));
        _timeLimitSeconds = timeLimitSeconds;
        _currentQuestionIndex = 0;
        _userAnswers = new Dictionary<int, List<QuizOption>>();
        _elapsedTime = TimeSpan.Zero;
        _isResultsView = false;

        Owner = Application.Current.MainWindow;
        Title = LocalizationService.GetString("QuizModeTitle");

        StartTimer();
        LoadQuestion();
        WindowContentEnterHelper.AnimateOnLoaded(this);
    }

    private void StartTimer()
    {
        _isCountdown = _timeLimitSeconds > 0;
        if (_isCountdown)
            _remainingTime = TimeSpan.FromSeconds(_timeLimitSeconds);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (s, e) =>
        {
            if (_isCountdown)
            {
                _remainingTime -= TimeSpan.FromSeconds(1);
                UpdateTimerDisplay(_remainingTime);

                // ⚠️ Įspėjimas kai lieka mažai laiko (pvz. 30 sek)
                if (_remainingTime.TotalSeconds <= 30 && !_timerWarningActive)
                    HighlightTimerWarning();

                // ⏱️ Kai laikas baigiasi — automatiškai pateikti
                if (_remainingTime <= TimeSpan.Zero)
                {
                    _timer.Stop();
                    ShowResults(); // arba pereiti prie kito klausimo
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

        // ✅ Show question type indicator for multiple choice
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
                foreach (var option in question.Options)
                {
                    if (option == null || string.IsNullOrWhiteSpace(option.Text)) continue;

                    var button = new Button
                    {
                        Content = option.Text,
                        Margin = new Thickness(0, 0, 0, 10),
                        Padding = new Thickness(16, 14, 16, 14),
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Background = (Brush)FindResource("CardBackground"),
                        BorderBrush = (Brush)FindResource("BorderColor"),
                        Foreground = (Brush)FindResource("TextColor"),
                        BorderThickness = new Thickness(1),
                        Cursor = Cursors.Hand,
                        Tag = option,
                        FontSize = 14,
                        FontWeight = FontWeights.Normal,
                        MinHeight = 44,
                        Focusable = false,
                        OverridesDefaultStyle = true,
                        Template = CreateSimpleButtonTemplate()
                    };

                    ButtonPressMotionBehavior.SetIsEnabled(button, true);
                    ButtonPressMotionBehavior.SetMotionHostName(button, "ButtonBorder");
                    button.Click += OptionButton_Click;
                    optionsPanel.Children.Add(button);
                }
            }
        }

        // Restore previously selected answer(s)
        RestoreSelectedAnswer();
        LoadHintSection();
        UpdateNavigationButtons();
    }

    private ControlTemplate CreateSimpleButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(FrameworkElement.NameProperty, "ButtonBorder");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));

        // ✅ Bind Background/Border properties from Button to Border using RelativeSource.TemplatedParent
        border.SetBinding(Border.BackgroundProperty,
            new Binding { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent), Path = new PropertyPath(Button.BackgroundProperty) });
        border.SetBinding(Border.BorderBrushProperty,
            new Binding { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent), Path = new PropertyPath(Button.BorderBrushProperty) });
        border.SetBinding(Border.BorderThicknessProperty,
            new Binding { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent), Path = new PropertyPath(Button.BorderThicknessProperty) });

        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetValue(ContentPresenter.MarginProperty, new Thickness(16, 14, 16, 14));
        contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        contentPresenter.SetBinding(ContentPresenter.ContentProperty,
            new Binding { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent), Path = new PropertyPath(Button.ContentProperty) });

        border.AppendChild(contentPresenter);
        template.VisualTree = border;
        return template;
    }

    private void RestoreSelectedAnswer()
    {
        if (FindName("OptionsPanel") is StackPanel optionsPanel &&
            _userAnswers.TryGetValue(_currentQuestionIndex, out var selectedOptions))
        {
            foreach (var child in optionsPanel.Children)
            {
                if (child is Button btn && btn.Tag is QuizOption opt)
                {
                    bool isSelected = selectedOptions.Contains(opt);
                    btn.Background = isSelected
                        ? (Brush)FindResource("NoteCardSelection")
                        : (Brush)FindResource("CardBackground");
                    btn.BorderBrush = isSelected
                        ? (Brush)FindResource("NoteCardSelectionBorder")
                        : (Brush)FindResource("BorderColor");
                    btn.BorderThickness = isSelected ? new Thickness(2) : new Thickness(1);
                    btn.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
                    btn.IsEnabled = true;
                }
            }
        }
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
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextColorSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(label);

        var charCountLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = (Brush)FindResource("TextColorSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(charCountLabel, 1);
        header.Children.Add(charCountLabel);
        content.Children.Add(header);

        var inputBorder = new Border
        {
            Background = (Brush)FindResource("WindowBackground"),
            BorderBrush = (Brush)FindResource("BorderColor"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8)
        };

        var inputGrid = new Grid();
        var textBox = new TextBox
        {
            Name = "PlainTextAnswerBox",
            Padding = new Thickness(0),
            FontSize = 15,
            Foreground = (Brush)FindResource("TextColor"),
            CaretBrush = (Brush)FindResource("TextColor"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 130,
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Tag = "PlainTextQuestion"
        };

        if (_userAnswers.TryGetValue(_currentQuestionIndex, out var previousAnswer)
            && TryGetPlainTextAnswer(previousAnswer, out var restoredAnswer))
        {
            textBox.Text = restoredAnswer;
        }

        var placeholder = new TextBlock
        {
            Text = LocalizationService.GetString("QuizPlainTextPracticePlaceholder"),
            FontSize = 15,
            Foreground = (Brush)FindResource("TextColorSecondary"),
            Opacity = 0.72,
            IsHitTestVisible = false,
            Margin = new Thickness(0, 1, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrEmpty(textBox.Text) ? Visibility.Visible : Visibility.Collapsed
        };

        void UpdatePlainTextState()
        {
            charCountLabel.Text = string.Format(LocalizationService.GetString("QuizCharactersFormat"), textBox.Text.Length);
            placeholder.Visibility = string.IsNullOrEmpty(textBox.Text) && !textBox.IsKeyboardFocusWithin
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        textBox.TextChanged += (s, e) =>
        {
            UpdatePlainTextState();
            SavePlainTextAnswer(textBox.Text);
        };
        textBox.GotKeyboardFocus += (s, e) =>
        {
            inputBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(63, 111, 232));
            UpdatePlainTextState();
        };
        textBox.LostKeyboardFocus += (s, e) =>
        {
            inputBorder.BorderBrush = (Brush)FindResource("BorderColor");
            UpdatePlainTextState();
        };

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
    }

    private void UpdateNavigationButtons()
    {
        if (FindName("PreviousButton") is Button prevButton)
            prevButton.IsEnabled = _currentQuestionIndex > 0;

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

        var outgoing = showSubmit ? nextButton : submitButton;
        var incoming = showSubmit ? submitButton : nextButton;
        var outgoingVisible = outgoing.Visibility == Visibility.Visible;

        if (UiMotion.PrefersReducedMotion || !outgoingVisible)
        {
            nextButton.Visibility = showSubmit ? Visibility.Collapsed : Visibility.Visible;
            submitButton.Visibility = showSubmit ? Visibility.Visible : Visibility.Collapsed;
            nextButton.Opacity = 1;
            submitButton.Opacity = 1;
            return;
        }

        UiMotion.Stop(outgoing);
        UiMotion.Stop(incoming);
        UiMotion.Fade(
            outgoing,
            0,
            UiMotion.NavFadeOutMs,
            UiMotion.InCubic,
            () =>
            {
                outgoing.Visibility = Visibility.Collapsed;
                outgoing.Opacity = 1;
                incoming.Visibility = Visibility.Visible;
                incoming.Opacity = 0;
                UiMotion.Fade(incoming, 1, UiMotion.QuickMs, UiMotion.OutCubic);
            });
    }

    private static void SetFooterButtonVisible(Button button, bool visible)
    {
        if (visible)
        {
            button.Visibility = Visibility.Visible;
            button.Opacity = 1;
            return;
        }

        if (button.Visibility != Visibility.Visible)
            return;

        if (UiMotion.PrefersReducedMotion)
        {
            button.Visibility = Visibility.Collapsed;
            return;
        }

        UiMotion.Stop(button);
        UiMotion.Fade(
            button,
            0,
            UiMotion.NavFadeOutMs,
            UiMotion.InCubic,
            () =>
            {
                button.Visibility = Visibility.Collapsed;
                button.Opacity = 1;
            });
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

        // ✅ Update visuals - now works immediately since we removed interfering triggers
        UpdateOptionVisuals(selectedOptions);
    }

    private void UpdateOptionVisuals(List<QuizOption> selectedOptions)
    {
        if (FindName("OptionsPanel") is not StackPanel optionsPanel)
            return;

        foreach (var child in optionsPanel.Children)
        {
            if (child is not Button optButton || optButton.Tag is not QuizOption opt)
                continue;

            var isSelected = selectedOptions.Contains(opt);
            optButton.Background = isSelected
                ? (Brush)FindResource("NoteCardSelection")
                : (Brush)FindResource("CardBackground");
            optButton.BorderBrush = isSelected
                ? (Brush)FindResource("NoteCardSelectionBorder")
                : (Brush)FindResource("BorderColor");
            optButton.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;

            AnimateOptionSelection(optButton, isSelected);
        }
    }

    private static void AnimateOptionSelection(Button button, bool isSelected)
    {
        var targetThickness = isSelected ? 2.0 : 1.0;
        var currentThickness = button.BorderThickness.Left;

        if (Math.Abs(currentThickness - targetThickness) < 0.01)
            return;

        if (UiMotion.PrefersReducedMotion)
        {
            button.BorderThickness = new Thickness(targetThickness);
            return;
        }

        UiMotion.Stop(button);
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = UiMotion.EnsureScale(button);

        UiMotion.RunComposited(
            button,
            new[]
            {
                UiMotion.Track(
                    UiMotion.QuickMs,
                    UiMotion.OutCubic,
                    currentThickness,
                    targetThickness,
                    value => button.BorderThickness = new Thickness(value)),
                UiMotion.Track(
                    UiMotion.QuickMs / 2,
                    UiMotion.OutCubic,
                    1,
                    isSelected ? 1.012 : 1.006,
                    value =>
                    {
                        scale.ScaleX = value;
                        scale.ScaleY = value;
                    }),
                UiMotion.Track(
                    UiMotion.QuickMs / 2,
                    UiMotion.OutCubic,
                    isSelected ? 1.012 : 1.006,
                    1,
                    value =>
                    {
                        scale.ScaleX = value;
                        scale.ScaleY = value;
                    },
                    startDelayMs: UiMotion.QuickMs / 2)
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
        var questions = _quiz.Questions ?? new List<QuizQuestion>();
        var totalQuestions = questions.Count;

        for (int i = 0; i < totalQuestions; i++)
        {
            var question = questions[i];
            if (!_userAnswers.TryGetValue(i, out var userSelected)) continue;

            bool isCorrect = IsAnswerCorrect(question, userSelected);
            if (isCorrect) correctCount++;
        }

        _wrongQuestionIndices.Clear();
        for (int i = 0; i < totalQuestions; i++)
        {
            var question = questions[i];
            if (!_userAnswers.TryGetValue(i, out var userSelected))
            {
                _wrongQuestionIndices.Add(i);
                continue;
            }
            bool isCorrect = IsAnswerCorrect(question, userSelected);
            if (!isCorrect) _wrongQuestionIndices.Add(i);
        }

        var percentage = totalQuestions == 0 ? 0 : (double)correctCount / totalQuestions * 100;

        var displayTime2 = _isCountdown ? TimeSpan.FromSeconds(_timeLimitSeconds) - _remainingTime : _elapsedTime;

        var passingScore = Math.Clamp(_quiz.PassingScorePercent, 0, 100);
        var passed = percentage >= passingScore;

        LastAttempt = new QuizAttempt
        {
            Date = DateTime.Now,
            CorrectCount = correctCount,
            TotalQuestions = totalQuestions,
            TimeTaken = displayTime2,
            QuizTitle = _quiz.Title ?? string.Empty,
            PassingScorePercent = passingScore,
        };


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
        {
            db.Visibility = Visibility.Visible;
            if (UiMotion.PrefersReducedMotion)
            {
                db.Opacity = 1;
            }
            else
            {
                db.Opacity = 0;
                UiMotion.Fade(db, 1, UiMotion.QuickMs, UiMotion.OutCubic);
            }
        }

        if (FindName("RetryAllButton") is Button rab)
            UiMotion.AnimateInlineReveal(rab, true);
        if (FindName("RetryWrongButton") is Button rwb)
            UiMotion.AnimateInlineReveal(rwb, _wrongQuestionIndices.Count > 0);
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
                    Background = (Brush)FindResource("CardBackground"),
                    BorderBrush = isCorrect
                        ? new SolidColorBrush(CorrectAnswerBorderColor)
                        : new SolidColorBrush(IncorrectAnswerBorderColor),
                    BorderThickness = new Thickness(1),
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
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextColor"),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(qText, 0);
                header.Children.Add(qText);

                var status = new TextBlock
                {
                    Text = isCorrect ? "✓" : "✗",
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    Foreground = isCorrect
                        ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                        : new SolidColorBrush(Color.FromRgb(220, 38, 38)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                Grid.SetColumn(status, 1);
                header.Children.Add(status);

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
                        FontSize = 13,
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
                            FontSize = 13,
                            Foreground = new SolidColorBrush(CorrectAnswerBorderColor),
                            Margin = new Thickness(0, 0, 0, 4),
                            TextWrapping = TextWrapping.Wrap
                        };
                        content.Children.Add(correctAnsBlock);
                    }
                }
                else
                {
                    // Multiple choice answer display
                    var correctOpts = q.Options?.Where(o => o.IsCorrect).ToList() ?? new List<QuizOption>();

                    var userAns = new TextBlock
                    {
                        Text = string.Format(
                            LocalizationService.GetString("QuizYourAnswerFormat"),
                            userSel.Count > 0 ? string.Join(", ", userSel.Select(o => o.Text)) : LocalizationService.GetString("QuizNoAnswer")),
                        FontSize = 13,
                        Foreground = (Brush)FindResource("TextColor"),
                        Margin = new Thickness(0, 8, 0, 4),
                        TextWrapping = TextWrapping.Wrap
                    };
                    content.Children.Add(userAns);

                    if (!isCorrect && correctOpts.Count > 0)
                    {
                        var correctAns = new TextBlock
                        {
                            Text = string.Format(LocalizationService.GetString("QuizCorrectAnswerFormat"), string.Join(", ", correctOpts.Select(o => o.Text))),
                            FontSize = 13,
                            Foreground = new SolidColorBrush(CorrectAnswerBorderColor),
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
                        FontSize = 12,
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
        _timer?.Stop();
        var retryWindow = new QuizModeWindow(_quiz, _timeLimitSeconds)
        {
            Owner = this.Owner
        };
        retryWindow.ShowDialog();
        // Propagate the retry attempt back to the preview window.
        if (retryWindow.LastAttempt != null)
            LastAttempt = retryWindow.LastAttempt;
        Close();
    }

    private void RetryWrongButton_Click(object sender, RoutedEventArgs e)
    {
        if (_wrongQuestionIndices.Count == 0) return;
        _timer?.Stop();

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
            Tags = _quiz.Tags
        };

        var retryWindow = new QuizModeWindow(wrongOnlyQuiz, _timeLimitSeconds)
        {
            Owner = this.Owner
        };
        retryWindow.ShowDialog();
        if (retryWindow.LastAttempt != null)
            LastAttempt = retryWindow.LastAttempt;
        Close();
    }

}
