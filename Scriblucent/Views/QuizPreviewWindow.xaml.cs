using Scriblucent.Animations;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using System.Collections.Specialized;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;

namespace Scriblucent.Views;

public partial class QuizPreviewWindow : Window, INotifyPropertyChanged
{
    private enum UnsavedCloseDecision
    {
        Cancel,
        LeaveWithoutSaving,
        SaveAndClose
    }

    public static readonly DependencyProperty IsEditModeProperty = DependencyProperty.Register(
        nameof(IsEditMode),
        typeof(bool),
        typeof(QuizPreviewWindow),
        new PropertyMetadata(false, OnIsEditModeChanged));

    private static Brush ResolveThemeBrush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush
           ?? new SolidColorBrush(fallback);

    private static Brush CorrectBrush => ResolveThemeBrush("AccentSuccess", Color.FromRgb(16, 185, 129));
    private static Brush IncorrectBrush => ResolveThemeBrush("AccentDanger", Color.FromRgb(220, 38, 38));
    private static Brush UnansweredBrush => ResolveThemeBrush("TextColorSecondary", Color.FromRgb(107, 114, 128));

    public sealed class QuizNoteLinkOption
    {
        public QuizNoteLinkOption(Guid id, string title)
        {
            Id = id;
            Title = string.IsNullOrWhiteSpace(title) ? LocalizationService.GetString("QuizUntitled") : title.Trim();
        }

        public Guid Id { get; }
        public string Title { get; }
    }

    private readonly ObservableCollection<QuizPreviewQuestion> _questions;
    private readonly QuizDocument _sourceDocument;
    private readonly ObservableCollection<QuizNoteLinkOption> _noteLinkOptions = new();
    private Action<Guid>? _openNoteAction;
    private string _modelDisplayName = string.Empty;
    private bool _isSubmitted;
    private QuizNoteLinkOption? _selectedLinkedNote;
    private string _lastSavedSnapshot = string.Empty;
    private bool _isInitializing = true;
    private bool _allowCloseWithoutPrompt;

    public QuizPreviewWindow(
        QuizDocument document,
        IEnumerable<QuizNoteLinkOption>? noteOptions = null,
        string? modelDisplayName = null,
        string? title = null,
        Action<Guid>? openNoteAction = null)
        : this(document, noteOptions, modelDisplayName, title, openNoteAction, initializeOnly: false)
    {
    }

    public QuizPreviewWindow(
        QuizDocument document,
        string? modelDisplayName = null,
        string? title = null)
        : this(document, null, modelDisplayName, title, null, initializeOnly: false)
    {
    }

    private QuizPreviewWindow(
        QuizDocument document,
        IEnumerable<QuizNoteLinkOption>? noteOptions,
        string? modelDisplayName,
        string? title,
        Action<Guid>? openNoteAction,
        bool initializeOnly)
    {
        _sourceDocument = document;
        _questions = new ObservableCollection<QuizPreviewQuestion>(
            (document.Questions ?? [])
            .Select((question, index) => new QuizPreviewQuestion(index + 1, question)));
        if (_questions.Count == 0)
            _questions.Add(new QuizPreviewQuestion(1, CreateDefaultQuestion()));

        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        RefreshProgressPanel();

        _openNoteAction = openNoteAction;
        if (noteOptions != null)
        {
            foreach (var option in noteOptions)
                _noteLinkOptions.Add(option);
        }

        _selectedLinkedNote = _noteLinkOptions.FirstOrDefault(option => option.Id == document.SourceNoteId);

        var displayTitle = string.IsNullOrWhiteSpace(title)
            ? document.Title
            : title;
        TitleTextBox.Text = string.IsNullOrWhiteSpace(displayTitle)
            ? LocalizationService.GetString("QuizUntitled")
            : displayTitle.Trim();
        QuestionsItemsControl.ItemsSource = _questions;
        ConfigureAiGeneratedIndicator(modelDisplayName ?? document.AiModelDisplayName);
        UpdateModeChrome();
        UpdateSummary();

        if (_questions.Any())
        {
            SetQuestionFocus(0);
        }

        OnPropertyChanged(nameof(LinkedNoteVisibility));
        OnPropertyChanged(nameof(LinkedNoteEditorVisibility));
        OnPropertyChanged(nameof(LinkedNoteButtonText));
        OnPropertyChanged(nameof(LinkedNoteButtonToolTip));

        PreviewKeyDown += QuizPreviewWindow_PreviewKeyDown;
        TrackQuestionChanges();
        _isInitializing = false;
        MarkCurrentStateSaved();
        WindowContentEnterHelper.AnimateOnLoaded(this);
    }
    public string EditorTitle => TitleTextBox.Text.Trim();

    public string AiModelDisplayName => _modelDisplayName;

    public string LinkedNoteDisplayText => _selectedLinkedNote?.Title ?? string.Empty;

    public string LinkedNoteButtonText => string.IsNullOrWhiteSpace(LinkedNoteDisplayText)
        ? LocalizationService.GetString("QuizLinkedNoteNone")
        : LinkedNoteDisplayText;

    public string LinkedNoteButtonToolTip => string.IsNullOrWhiteSpace(LinkedNoteDisplayText)
        ? LocalizationService.GetString("QuizLinkedNoteNoneTooltip")
        : string.Format(LocalizationService.GetString("QuizLinkedNoteOpenTooltip"), LinkedNoteDisplayText);

    public Visibility LinkedNoteVisibility => HasLinkedNote() ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LinkedNoteEditorVisibility => IsEditMode ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<QuizNoteLinkOption> NoteLinkOptions => _noteLinkOptions;

    public QuizNoteLinkOption? SelectedLinkedNote
    {
        get => _selectedLinkedNote;
        set
        {
            if (ReferenceEquals(_selectedLinkedNote, value))
                return;

            _selectedLinkedNote = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkedNoteDisplayText));
            OnPropertyChanged(nameof(LinkedNoteButtonText));
            OnPropertyChanged(nameof(LinkedNoteButtonToolTip));
            OnPropertyChanged(nameof(LinkedNoteVisibility));
            UpdateEditedIndicator();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEditMode
    {
        get => (bool)GetValue(IsEditModeProperty);
        set => SetValue(IsEditModeProperty, value);
    }

    private void EditorField_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateEditedIndicator();
    }

    private void TrackQuestionChanges()
    {
        _questions.CollectionChanged += Questions_CollectionChanged;
        foreach (var question in _questions)
            AttachQuestionChangeTracking(question);
    }

    private void Questions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (QuizPreviewQuestion question in e.OldItems)
                DetachQuestionChangeTracking(question);
        }

        if (e.NewItems is not null)
        {
            foreach (QuizPreviewQuestion question in e.NewItems)
                AttachQuestionChangeTracking(question);
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var question in _questions)
                AttachQuestionChangeTracking(question);
        }

        UpdateEditedIndicator();
    }

    private void AttachQuestionChangeTracking(QuizPreviewQuestion question)
    {
        question.PropertyChanged -= QuizQuestion_PropertyChanged;
        question.PropertyChanged += QuizQuestion_PropertyChanged;
        question.Options.CollectionChanged -= QuizOptions_CollectionChanged;
        question.Options.CollectionChanged += QuizOptions_CollectionChanged;

        foreach (var option in question.Options)
        {
            option.PropertyChanged -= QuizOption_PropertyChanged;
            option.PropertyChanged += QuizOption_PropertyChanged;
        }
    }

    private void DetachQuestionChangeTracking(QuizPreviewQuestion question)
    {
        question.PropertyChanged -= QuizQuestion_PropertyChanged;
        question.Options.CollectionChanged -= QuizOptions_CollectionChanged;

        foreach (var option in question.Options)
            option.PropertyChanged -= QuizOption_PropertyChanged;
    }

    private void QuizQuestion_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuizPreviewQuestion.PlainTextUserAnswer)
            || e.PropertyName == nameof(QuizPreviewQuestion.HasSelection))
        {
            UpdateSummary();
        }

        if (IsSavedQuestionProperty(e.PropertyName))
            UpdateEditedIndicator();
    }

    private void QuizOptions_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (QuizPreviewOption option in e.OldItems)
                option.PropertyChanged -= QuizOption_PropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (QuizPreviewOption option in e.NewItems)
            {
                option.PropertyChanged -= QuizOption_PropertyChanged;
                option.PropertyChanged += QuizOption_PropertyChanged;
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset && sender is ObservableCollection<QuizPreviewOption> options)
        {
            foreach (var option in options)
            {
                option.PropertyChanged -= QuizOption_PropertyChanged;
                option.PropertyChanged += QuizOption_PropertyChanged;
            }
        }

        UpdateEditedIndicator();
    }

    private void QuizOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsSavedOptionProperty(e.PropertyName))
            UpdateEditedIndicator();
    }

    private static bool IsSavedQuestionProperty(string? propertyName)
    {
        return propertyName is null
            || propertyName == nameof(QuizPreviewQuestion.Number)
            || propertyName == nameof(QuizPreviewQuestion.Type)
            || propertyName == nameof(QuizPreviewQuestion.Question)
            || propertyName == nameof(QuizPreviewQuestion.Explanation)
            || propertyName == nameof(QuizPreviewQuestion.PlainTextAnswer)      
            || propertyName == nameof(QuizPreviewQuestion.PlainTextCharLimit)
            || propertyName == nameof(QuizPreviewQuestion.PlainTextMatchCase)
            || propertyName == nameof(QuizPreviewQuestion.PlainTextIgnorePunctuation);
    }

    private static bool IsSavedOptionProperty(string? propertyName)
    {
        return propertyName is null
            || propertyName == nameof(QuizPreviewOption.Text)
            || propertyName == nameof(QuizPreviewOption.IsCorrect);
    }

    private void MarkCurrentStateSaved()
    {
        _lastSavedSnapshot = GetEditorSnapshot();
        UpdateEditedIndicator();
    }

    public bool HasPendingAutoSaveChanges() => HasUnsavedChanges();

    public void MarkCurrentStateAutoSaved() => MarkCurrentStateSaved();

    private bool HasUnsavedChanges()
    {
        if (_isInitializing)
            return false;

        return !string.Equals(GetEditorSnapshot(), _lastSavedSnapshot, StringComparison.Ordinal);
    }

    private void UpdateEditedIndicator()
    {
        if (_isInitializing || EditedIndicatorText is null)
            return;

        UiMotion.AnimateInlineReveal(EditedIndicatorText, HasUnsavedChanges());
    }

    private string GetEditorSnapshot()
    {
        var questionsSnapshot = string.Join('\u001E', _questions.Select(question =>
        {
            var optionsSnapshot = string.Join('\u001C', question.Options.Select(option =>
                string.Join('\u001B', option.Text, option.IsCorrect.ToString())));

            return string.Join(
                '\u001D',
                question.Number.ToString(CultureInfo.InvariantCulture),
                question.Type.ToString(),
                question.Question,
                question.Explanation,
                question.Hint ?? string.Empty,
                question.PlainTextAnswer,
                question.PlainTextCharLimit.ToString(CultureInfo.InvariantCulture),
                question.PlainTextMatchCase.ToString(CultureInfo.InvariantCulture),
                question.PlainTextIgnorePunctuation.ToString(CultureInfo.InvariantCulture),
                optionsSnapshot);
        }));

        var attemptsSnapshot = string.Join('\u001E', (_sourceDocument.Attempts ?? []).Select(attempt =>
            string.Join(
                '\u001D',
                attempt.Date.ToString("O", CultureInfo.InvariantCulture),
                attempt.CorrectCount.ToString(CultureInfo.InvariantCulture),
                attempt.TotalQuestions.ToString(CultureInfo.InvariantCulture),
                attempt.TimeTaken.Ticks.ToString(CultureInfo.InvariantCulture),
                attempt.QuizTitle,
                attempt.PassingScorePercent.ToString(CultureInfo.InvariantCulture))));

        return string.Join(
            '\u001F',
            TitleTextBox.Text,
            GetSourceNoteIdForSnapshot(),
            string.Join('\u001E', _sourceDocument.Tags ?? []),
            (_sourceDocument.GroupId?.ToString() ?? string.Empty),
            questionsSnapshot,
            attemptsSnapshot);
    }

    private string GetSourceNoteIdForSnapshot()
        => (SelectedLinkedNote?.Id ?? _sourceDocument.SourceNoteId)?.ToString() ?? string.Empty;

    private UnsavedCloseDecision GetCloseDecision()
    {
        if (!HasUnsavedChanges())
            return UnsavedCloseDecision.LeaveWithoutSaving;

        var documentTitle = ResolveEditorTitleForPrompt();
        var result = ModernDialog.Show(
            this,
            LocalizationService.GetString("UnsavedChanges"),
            string.Format(LocalizationService.GetString("UnsavedChangesConfirmationFormat"), documentTitle),
            ModernDialogTone.Warning,
            LocalizationService.GetString("LeaveWithoutSaving"),
            LocalizationService.GetString("Cancel"),
            LocalizationService.GetString("SaveAndExit"),
            primaryStyle: ModernDialogButtonStyle.Danger,
            secondaryStyle: ModernDialogButtonStyle.Primary);

        return result switch
        {
            ModernDialogResult.Primary => UnsavedCloseDecision.LeaveWithoutSaving,
            ModernDialogResult.Secondary => UnsavedCloseDecision.SaveAndClose,
            _ => UnsavedCloseDecision.Cancel
        };
    }

    private string ResolveEditorTitleForPrompt()
    {
        var title = EditorTitle;
        return string.IsNullOrWhiteSpace(title)
            ? LocalizationService.GetString("QuizUntitled")
            : title;
    }

    private void SaveAndClose()
    {
        // Ensure any in-progress edits (focused TextBox) update their binding source
        CommitFocusedTextBoxBinding();

        MarkCurrentStateSaved();
        _allowCloseWithoutPrompt = true;
        WindowContentEnterHelper.CloseWithDialogResult(this, true);
    }

    private void CommitFocusedTextBoxBinding()
    {
        try
        {
            // Commit all TextBox bindings in the window to ensure all pending edits are saved
            CommitTextBoxBindingsInVisualTree(this);
        }
        catch
        {
            // Swallow any exceptions - this is a best-effort commit to avoid losing edits when closing
        }
    }

    private void CommitTextBoxBindingsInVisualTree(DependencyObject obj)
    {
        // Recursively walk the visual tree and update all TextBox bindings
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = VisualTreeHelper.GetChild(obj, i);
            if (child is System.Windows.Controls.TextBox tb)
            {
                var be = tb.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
                be?.UpdateSource();
            }

            CommitTextBoxBindingsInVisualTree(child);
        }
    }

    public QuizDocument ToDocument(QuizDocument? existingDocument = null)
    {
        return new QuizDocument
        {
            Id = existingDocument?.Id ?? _sourceDocument.Id,
            Title = string.IsNullOrWhiteSpace(EditorTitle)
                ? LocalizationService.GetString("QuizUntitled")
                : EditorTitle,
            Tags = existingDocument?.Tags?.ToList() ?? _sourceDocument.Tags?.ToList() ?? new List<string>(),
            Questions = _questions.Select(question => question.ToModel()).ToList(),
            CreatedAt = existingDocument?.CreatedAt ?? _sourceDocument.CreatedAt,
            LastModified = DateTime.Now,
            AiModelDisplayName = string.IsNullOrWhiteSpace(_modelDisplayName)
                ? existingDocument?.AiModelDisplayName ?? _sourceDocument.AiModelDisplayName
                : _modelDisplayName,
            SourceNoteId = SelectedLinkedNote?.Id ?? existingDocument?.SourceNoteId ?? _sourceDocument.SourceNoteId,
            GroupId = existingDocument?.GroupId ?? _sourceDocument.GroupId,
            Schedules = existingDocument?.Schedules?.ToList() ?? _sourceDocument.Schedules?.ToList() ?? new List<NoteScheduleEntry>(),
            IsPinned = existingDocument?.IsPinned ?? _sourceDocument.IsPinned,
            TimeLimitSeconds = existingDocument?.TimeLimitSeconds ?? _sourceDocument.TimeLimitSeconds,
            PassingScorePercent = _sourceDocument.PassingScorePercent,
            Attempts = _sourceDocument.Attempts?.ToList() ?? existingDocument?.Attempts?.ToList() ?? new List<QuizAttempt>()
        };
    }

    private void OptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSubmitted || IsEditMode)
            return;

        if ((sender as FrameworkElement)?.DataContext is not QuizPreviewOption option)
            return;

        option.Parent.SelectOption(option);
        UpdateSummary();
    }

    private void CheckAnswersButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsEditMode)
            return;

        CommitFocusedTextBoxBinding();

        void SubmitAnswers()
        {
            _isSubmitted = true;
            foreach (var question in _questions)
                question.Submit();

            UpdateSummary(animateScore: true);
        }

        if (UiMotion.PrefersReducedMotion || QuestionsScrollViewer is null)
        {
            SubmitAnswers();
            return;
        }

        UiMotion.Fade(
            QuestionsScrollViewer,
            0.5,
            UiMotion.NavFadeOutMs,
            UiMotion.InCubic,
            () =>
            {
                SubmitAnswers();
                UiMotion.Fade(QuestionsScrollViewer, 1, UiMotion.QuickMs, UiMotion.OutCubic);
            });
    }

    private void ResetAnswersButton_Click(object sender, RoutedEventArgs e)
    {
        void ResetAnswers()
        {
            _isSubmitted = false;
            foreach (var question in _questions)
                question.Reset();

            UpdateSummary(animateScore: true);
        }

        if (UiMotion.PrefersReducedMotion || ScoreBadge.Visibility != Visibility.Visible)
        {
            ResetAnswers();
            return;
        }

        UiMotion.SlideFadePanel(
            ScoreBadge,
            false,
            UiMotion.NavFadeOutMs,
            UiMotion.SubtleLiftY,
            ResetAnswers);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        switch (GetCloseDecision())
        {
            case UnsavedCloseDecision.Cancel:
                return;
            case UnsavedCloseDecision.SaveAndClose:
                SaveAndClose();
                return;
            case UnsavedCloseDecision.LeaveWithoutSaving:
                _allowCloseWithoutPrompt = true;
                Close();
                return;
        }
    }

    private void StartQuizButton_Click(object sender, RoutedEventArgs e)
    {
        // Use the live editor snapshot — _sourceDocument.Questions is not updated until Save.
        var quizDocument = ToDocument(_sourceDocument);
        if (quizDocument.Questions == null || quizDocument.Questions.Count == 0)
        {
            ModernMessageBox.Show(
                LocalizationService.GetString("QuizNoQuestions"),
                LocalizationService.GetString("QuizMode"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var optionsDialog = new QuizStartOptionsDialog(quizDocument.TimeLimitSeconds, quizDocument.PassingScorePercent)
            {
                Owner = this
            };

            if (optionsDialog.ShowDialog() != true)
                return;

            var timeLimitSeconds = optionsDialog.TimeLimitSeconds;
            quizDocument.TimeLimitSeconds = timeLimitSeconds == 0 ? null : timeLimitSeconds;
            quizDocument.PassingScorePercent = optionsDialog.PassingScorePercent;
            _sourceDocument.TimeLimitSeconds = quizDocument.TimeLimitSeconds;
            _sourceDocument.PassingScorePercent = quizDocument.PassingScorePercent;

            var quizModeWindow = new QuizModeWindow(quizDocument, timeLimitSeconds)
            {
                Owner = this
            };
            quizModeWindow.ShowDialog();
            if (quizModeWindow.LastAttempt != null)
            {
                _sourceDocument.Attempts ??= new List<QuizAttempt>();
                _sourceDocument.Attempts.Add(quizModeWindow.LastAttempt);
                SaveQuizProgress();
                RefreshProgressPanel();
            }
        }
        catch (Exception ex)
        {
            ModernMessageBox.Show(
                $"Error starting quiz: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
    private void SaveQuizProgress()
    {
        // Keep the quiz editor open after a test run. Attempts are part of the editor
        // snapshot, so leaving the document dirty lets autosave persist them.
        UpdateEditedIndicator();
    }

    private void RefreshProgressPanel()
    {
        if (_sourceDocument.Attempts.Count == 0) return;

        var avg = _sourceDocument.Attempts.Average(a => a.Percentage);
        var count = _sourceDocument.Attempts.Count;
        var best = _sourceDocument.Attempts.Max(a => a.Percentage);

        if (FindName("StatsAvgText") is System.Windows.Controls.TextBlock avgTb)
            avgTb.Text = $"{avg:F1}%";
        if (FindName("StatsTotalText") is System.Windows.Controls.TextBlock totalTb)
            totalTb.Text = count.ToString();
        if (FindName("StatsBestText") is System.Windows.Controls.TextBlock bestTb)
            bestTb.Text = $"{best:F1}%";

        if (FindName("AttemptsListPanel") is System.Windows.Controls.StackPanel panel)
        {
            panel.Children.Clear();
            foreach (var attempt in _sourceDocument.Attempts.OrderByDescending(a => a.Date))
            {
                var row = new System.Windows.Controls.TextBlock
                {
                    Text = $"{attempt.Date:yyyy-MM-dd HH:mm}  —  {attempt.CorrectCount}/{attempt.TotalQuestions} ({attempt.Percentage:F1}%)  ⏱ {attempt.TimeTaken:hh\\:mm\\:ss}",
                    Margin = new System.Windows.Thickness(0, 4, 0, 4),
                    Foreground = (System.Windows.Media.Brush)FindResource("TextColor")
                };
                panel.Children.Add(row);
            }
        }
    }

    private void ResetProgressButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var result = ModernMessageBox.Show(
            "Ar tikrai norite ištrinti visą šio kvizo progreso istoriją?",
            "Resetuoti progresą",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            _sourceDocument.Attempts.Clear();
            RefreshProgressPanel();
        }
    }

    private void QuizHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new QuizAttemptHistoryWindow(_sourceDocument)
        {
            Owner = this
        };

        window.ShowDialog();
        RefreshProgressPanel();
        UpdateEditedIndicator();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveAndClose();
    }

    private void QuizPreviewWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowCloseWithoutPrompt)
            return;

        switch (GetCloseDecision())
        {
            case UnsavedCloseDecision.Cancel:
                e.Cancel = true;
                return;
            case UnsavedCloseDecision.SaveAndClose:
                _allowCloseWithoutPrompt = true;
                MarkCurrentStateSaved();
                e.Cancel = true;
                WindowContentEnterHelper.CloseWithDialogResult(this, true);
                return;
            case UnsavedCloseDecision.LeaveWithoutSaving:
                _allowCloseWithoutPrompt = true;
                return;
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_questions.Count == 0)
            return;

        var saveDialog = new SaveFileDialog
        {
            Title = LocalizationService.GetString("QuizExportDialogTitle"),
            Filter = LocalizationService.GetString("QuizExportDialogFilter"),
            FileName = GetExportFileName(),
            OverwritePrompt = true
        };

        if (saveDialog.ShowDialog(this) != true)
            return;

        try
        {
            var extension = Path.GetExtension(saveDialog.FileName).ToLowerInvariant();
            switch (extension)
            {
                case ".json":
                    ExportToJson(saveDialog.FileName);
                    break;
                case ".csv":
                    ExportToCsv(saveDialog.FileName);
                    break;
                case ".xps":
                case ".pdf":
                    ExportPrintableDocument(saveDialog.FileName);
                    break;
                default:
                    throw new InvalidOperationException(LocalizationService.GetString("QuizExportUnsupportedFormat"));
            }

            ShowExportDialog(
                LocalizationService.GetString("Success"),
                LocalizationService.GetString("QuizExportComplete"));
        }
        catch (Exception ex)
        {
            ModernMessageBox.Show(
                string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("QuizExportFailedFormat"), ex.Message),
                LocalizationService.GetString("ExportError"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowExportDialog(string title, string message)
    {
        ModernDialog.ShowInfo(this, title, message);
    }

    private void ModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        IsEditMode = !IsEditMode;
    }

    private void AddQuestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditMode)
            IsEditMode = true;

        _isSubmitted = false;
        var question = new QuizPreviewQuestion(_questions.Count + 1, CreateDefaultQuestion());
        _questions.Add(question);
        RenumberQuestions();
        SetQuestionFocus(_questions.Count - 1);
        VerticalListMotion.AnimateItemEnter(this, QuestionsItemsControl, question, offsetY: UiMotion.ContentLiftY);
        UpdateSummary();
    }

    private void ShuffleQuestionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_questions.Count < 2) return;

        var rng = new Random();
        var shuffled = _questions.OrderBy(_ => rng.Next()).ToList();

        _questions.Clear();
        foreach (var q in shuffled)
            _questions.Add(q);

        RenumberQuestions();
        UpdateEditedIndicator();
    }

    private void AddOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditMode)
            return;

        if ((sender as FrameworkElement)?.DataContext is not QuizPreviewQuestion question)
            return;

        var option = question.AddOption(LocalizationService.GetString("NewQuizWrongAnswer"), isCorrect: false);
        if (option is not null)
            AnimateQuestionOptionEnter(question, option);

        UpdateSummary();
    }

    private void QuizTypeComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.DataContext is not QuizPreviewQuestion question)
            return;

        SelectQuizTypeComboBoxItem(comboBox, question.Type);
    }

    private void QuizTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox
            || comboBox.DataContext is not QuizPreviewQuestion question
            || comboBox.SelectedItem is not ComboBoxItem item
            || item.Tag is null)
            return;

        if (!Enum.TryParse(item.Tag.ToString(), out QuizQuestionType type))
            return;

        var oldType = question.Type;
        question.Type = type;

        if (type == QuizQuestionType.PlainText)
        {
            // Clear options for plain text questions
            question.Options.Clear();
            if (string.IsNullOrWhiteSpace(question.PlainTextAnswer))
                question.PlainTextAnswer = string.Empty;
        }
        else if (question.Options.Count == 0)
        {
            // Add default options when switching away from plain text
            question.ResetOptions(CreateDefaultQuestion().Options);
        }

        UpdateSummary();
    }

    private static void SelectQuizTypeComboBoxItem(ComboBox comboBox, QuizQuestionType type)
    {
        var targetTag = type.ToString();
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), targetTag, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private void MoveQuestionUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditMode)
            return;

        if ((sender as FrameworkElement)?.DataContext is not QuizPreviewQuestion question)
            return;

        var index = _questions.IndexOf(question);
        if (index <= 0)
            return;

        var swapPartner = _questions[index - 1];
        VerticalListMotion.AnimatePairSwap(
            this,
            QuestionsItemsControl,
            question,
            swapPartner,
            () =>
            {
                _questions.Move(index, index - 1);
                RenumberQuestions();
            },
            () => SetQuestionFocus(index - 1));
    }

    private void MoveQuestionDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditMode)
            return;

        if ((sender as FrameworkElement)?.DataContext is not QuizPreviewQuestion question)
            return;

        var index = _questions.IndexOf(question);
        if (index < 0 || index >= _questions.Count - 1)
            return;

        var swapPartner = _questions[index + 1];
        VerticalListMotion.AnimatePairSwap(
            this,
            QuestionsItemsControl,
            question,
            swapPartner,
            () =>
            {
                _questions.Move(index, index + 1);
                RenumberQuestions();
            },
            () => SetQuestionFocus(index + 1));
    }

    private void MoveOptionUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditMode)
            return;

        if ((sender as FrameworkElement)?.DataContext is not QuizPreviewOption option)
            return;

        var question = option.Parent;
        var options = question.Options;
        var index = options.IndexOf(option);
        if (index <= 0)
            return;

        var swapPartner = options[index - 1];
        AnimateQuestionOptionSwap(
            question,
            option,
            swapPartner,
            () => options.Move(index, index - 1),
            () => question.RefreshOptionOrderState());
    }

    private void MoveOptionDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditMode)
            return;

        if ((sender as FrameworkElement)?.DataContext is not QuizPreviewOption option)
            return;

        var question = option.Parent;
        var options = question.Options;
        var index = options.IndexOf(option);
        if (index < 0 || index >= options.Count - 1)
            return;

        var swapPartner = options[index + 1];
        AnimateQuestionOptionSwap(
            question,
            option,
            swapPartner,
            () => options.Move(index, index + 1),
            () => question.RefreshOptionOrderState());
    }

    private void AnimateQuestionOptionEnter(QuizPreviewQuestion question, QuizPreviewOption option)
    {
        if (!TryGetQuestionOptionsList(question, out var optionsList))
            return;

        VerticalListMotion.AnimateItemEnter(this, optionsList, option);
    }

    private void AnimateQuestionOptionSwap(
        QuizPreviewQuestion question,
        QuizPreviewOption primaryOption,
        QuizPreviewOption secondaryOption,
        Action applyMove,
        Action? onComplete = null)
    {
        if (!TryGetQuestionOptionsList(question, out var optionsList))
        {
            applyMove();
            onComplete?.Invoke();
            return;
        }

        VerticalListMotion.AnimatePairSwap(
            this,
            optionsList,
            primaryOption,
            secondaryOption,
            applyMove,
            onComplete);
    }

    private bool TryGetQuestionOptionsList(QuizPreviewQuestion question, out ItemsControl optionsList)
    {
        optionsList = null!;
        if (QuestionsItemsControl.ItemContainerGenerator.ContainerFromItem(question) is not FrameworkElement questionContainer)
            return false;

        if (VerticalListMotion.FindNestedItemsControl(questionContainer) is not ItemsControl nestedList)
            return false;

        optionsList = nestedList;
        return true;
    }

    private void RemoveOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditMode)
            return;

        if ((sender as FrameworkElement)?.DataContext is not QuizPreviewOption option)
            return;

        option.Parent.RemoveOption(option);
        UpdateSummary();
        e.Handled = true;
    }

    private void DeleteQuestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsEditMode)
            return;

        if ((sender as FrameworkElement)?.DataContext is not QuizPreviewQuestion question)
            return;

        if (_questions.Count <= 1)
        {
            question.Question = LocalizationService.GetString("NewQuizQuestion");
            question.Explanation = string.Empty;
            question.ResetOptions(CreateDefaultQuestion().Options);
            UpdateSummary();
            return;
        }

        var index = _questions.IndexOf(question);
        _questions.Remove(question);
        RenumberQuestions();
        SetQuestionFocus(Math.Min(index, _questions.Count - 1));
        UpdateSummary();
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void CategoriesAndSetsButton_Click(object sender, RoutedEventArgs e)
    {
        EditQuizTags();
    }

    private void LinkedNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLinkedNote is null)
            return;

        _openNoteAction?.Invoke(SelectedLinkedNote.Id);
    }

    private void ToggleFullscreen()
    {
        if (ShellWindowChrome.IsFullscreenSuspended(this))
            ShellWindowChrome.ResumeFromFullscreen(this);
        else
            ShellWindowChrome.SuspendForFullscreen(this);
    }

    private void RenumberQuestions()
    {
        for (var i = 0; i < _questions.Count; i++)
            _questions[i].Number = i + 1;
    }

    private static void OnIsEditModeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is QuizPreviewWindow window)
            window.HandleEditModeChanged((bool)e.NewValue);
    }

    private void HandleEditModeChanged(bool isEditMode)
    {
        if (isEditMode && _isSubmitted)
        {
            _isSubmitted = false;
            foreach (var question in _questions)
                question.Reset();
        }

        if (_isInitializing || QuestionsScrollViewer is null || UiMotion.PrefersReducedMotion)
        {
            UpdateModeChrome();
            UpdateSummary();
            return;
        }

        UiMotion.Fade(
            QuestionsScrollViewer,
            0,
            UiMotion.NavFadeOutMs,
            UiMotion.InCubic,
            () =>
            {
                UpdateModeChrome();
                UpdateSummary();
                QuestionsScrollViewer.Opacity = 0;
                UiMotion.Fade(QuestionsScrollViewer, 1, UiMotion.QuickMs, UiMotion.OutCubic);
            });
    }

    private void UpdateModeChrome()
    {
        if (TitleTextBox is null)
            return;

        TitleTextBox.IsReadOnly = !IsEditMode;
        ModeToggleButton.Content = LocalizationService.GetString(IsEditMode ? "ViewQuiz" : "EditQuiz");
        CheckAnswersButton.Visibility = IsEditMode ? Visibility.Collapsed : Visibility.Visible;
        ResetAnswersButton.Visibility = IsEditMode ? Visibility.Collapsed : Visibility.Visible;
        AddQuestionButton.Visibility = IsEditMode ? Visibility.Visible : Visibility.Collapsed;
        OnPropertyChanged(nameof(LinkedNoteVisibility));
        OnPropertyChanged(nameof(LinkedNoteEditorVisibility));
        OnPropertyChanged(nameof(LinkedNoteButtonText));
        OnPropertyChanged(nameof(LinkedNoteButtonToolTip));
    }

    private void EditQuizTags()
    {
        var currentTags = string.Join(", ", _sourceDocument.Tags ?? []);
        var tagsDialog = new SimpleInputDialog(
            "Quiz categories",
            "Enter quiz categories/tags separated by commas, semicolons, or pipes:",
            currentTags)
        {
            Owner = this
        };

        if (tagsDialog.ShowDialog() != true)
            return;

        _sourceDocument.Tags = tagsDialog.InputText
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        UpdateEditedIndicator();
    }

    private bool HasLinkedNote()
    {
        return SelectedLinkedNote is not null;
    }

    private string GetExportFileName()
    {
        var baseName = string.IsNullOrWhiteSpace(EditorTitle)
            ? LocalizationService.GetString("QuizUntitled")
            : EditorTitle;

        foreach (var invalid in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');

        return baseName;
    }

    private void ExportToJson(string path)
    {
        var exportDocument = ToDocument();
        var json = JsonSerializer.Serialize(exportDocument, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private void ExportToCsv(string path)
    {
        var exportDocument = ToDocument();
        var sb = new StringBuilder();
        sb.AppendLine("QuestionNumber,Type,Question,CorrectAnswers,Explanation");

        foreach (var question in exportDocument.Questions)
        {
            var correctAnswers = question.Type == QuizQuestionType.PlainText
                ? question.PlainTextAnswer
                : string.Join(" | ", question.Options.Where(option => option.IsCorrect).Select(option => option.Text));
            sb.AppendLine(string.Join(",",
                EscapeCsv(question.SetIndex.ToString(CultureInfo.InvariantCulture)),
                EscapeCsv(question.Type.ToString()),
                EscapeCsv(question.Question),
                EscapeCsv(correctAnswers),
                EscapeCsv(question.Explanation)));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private void ExportPrintableDocument(string path)
    {
        var document = BuildPrintableDocument();
        using var xpsDocument = new XpsDocument(path, FileAccess.Write);
        var writer = XpsDocument.CreateXpsDocumentWriter(xpsDocument);
        writer.Write(((IDocumentPaginatorSource)document).DocumentPaginator);
    }

    private FlowDocument BuildPrintableDocument()
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = TypeScale.Px(TypeScale.Caption, 12),
            PagePadding = new Thickness(48),
            ColumnWidth = 700,
            Background = Brushes.White,
            Foreground = Brushes.Black
        };

        document.Blocks.Add(new Paragraph(new Run(EditorTitle))
        {
            FontSize = TypeScale.Px(TypeScale.Display, 24),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 16)
        });

        foreach (var question in _questions)
        {
            document.Blocks.Add(new Paragraph(new Run(string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("QuizQuestionHeaderFormat"), question.Number, question.Type switch
            {
                QuizQuestionType.TrueFalse => LocalizationService.GetString("QuizTypeTrueFalse"),
                QuizQuestionType.MultipleChoice => LocalizationService.GetString("QuizTypeMultipleChoice"),
                QuizQuestionType.PlainText => LocalizationService.GetString("QuizTypePlainText"),
                _ => LocalizationService.GetString("QuizTypeSingleChoice")
            })))
            {
                FontSize = TypeScale.Px(TypeScale.BlockTitle, 16),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 6)
            });

            document.Blocks.Add(new Paragraph(new Run(question.Question))
            {
                Margin = new Thickness(0, 0, 0, 6)
            });

            if (question.IsPlainText)
            {
                document.Blocks.Add(new Paragraph(new Run(string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("QuizCorrectAnswerFormat"), question.PlainTextAnswer)))
                {
                    Margin = new Thickness(12, 0, 0, 2)
                });
            }

            foreach (var option in question.Options.Where(option => !string.IsNullOrWhiteSpace(option.Text)))
            {
                var marker = option.IsCorrect ? "✓" : "•";
                document.Blocks.Add(new Paragraph(new Run($"{marker} {option.Text}"))
                {
                    Margin = new Thickness(12, 0, 0, 2)
                });
            }

            if (!string.IsNullOrWhiteSpace(question.Explanation))
            {
                document.Blocks.Add(new Paragraph(new Run(string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("QuizExplanationFormat"), question.Explanation)))
                {
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }
        }

        return document;
    }

    private static string EscapeCsv(string value)
    {
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static QuizQuestion CreateDefaultQuestion()
    {
        return new QuizQuestion
        {
            Type = QuizQuestionType.SingleChoice,
            Question = LocalizationService.GetString("NewQuizQuestion"),
            Options = new List<QuizOption>
            {
            new() { Text = LocalizationService.GetString("NewQuizCorrectAnswer"), IsCorrect = true },
            new() { Text = LocalizationService.GetString("NewQuizWrongAnswer"), IsCorrect = false },
            new() { Text = LocalizationService.GetString("NewQuizWrongAnswer"), IsCorrect = false }
            },
            PlainTextAnswer = string.Empty,
            PlainTextCharLimit = 500,
            PlainTextMatchCase = false,
            PlainTextIgnorePunctuation = false
        };
    }

    private void QuizPreviewWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ShellWindowChrome.IsFullscreenSuspended(this))
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else
            {
                Close();
            }
        }
        else if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (IsEditMode)
        {
            return;
        }
        else if (e.Key == Key.Enter)
        {
            if (CheckAnswersButton.IsEnabled && CheckAnswersButton.Visibility == Visibility.Visible)
            {
                CheckAnswersButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (ResetAnswersButton.IsEnabled && ResetAnswersButton.Visibility == Visibility.Visible)
            {
                ResetAnswersButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Up)
        {
            int current = GetFocusedQuestionIndex();
            if (current > 0)
            {
                SetQuestionFocus(current - 1);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Down)
        {
            int current = GetFocusedQuestionIndex();
            if (current >= 0 && current < _questions.Count - 1)
            {
                SetQuestionFocus(current + 1);
                e.Handled = true;
            }
        }
        else if ((e.Key >= Key.D1 && e.Key <= Key.D9) || (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9))
        {
            int number = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
            int current = GetFocusedQuestionIndex();
            if (current >= 0 && current < _questions.Count)
            {
                var question = _questions[current];
                if (number < question.Options.Count)
                {
                    question.SelectOption(question.Options[number]);
                    UpdateSummary();
                    e.Handled = true;
                }
            }
        }
    }

    private void ConfigureAiGeneratedIndicator(string? modelDisplayName)
    {
        _modelDisplayName = modelDisplayName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_modelDisplayName))
        {
            AiGeneratedInfoBadge.ToolTip = null;
            if (_isInitializing)
            {
                AiGeneratedInfoBadge.Visibility = Visibility.Collapsed;
                return;
            }

            UiMotion.AnimateInlineReveal(AiGeneratedInfoBadge, false);
            return;
        }

        AiGeneratedInfoBadge.ToolTip = string.Format(
            LocalizationService.GetString("QuizGeneratedWithModel"),
            _modelDisplayName);

        if (_isInitializing)
        {
            AiGeneratedInfoBadge.Visibility = Visibility.Visible;
            return;
        }

        UiMotion.AnimateInlineReveal(AiGeneratedInfoBadge, true);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void UpdateSummary(bool animateScore = false)
    {
        var questionCount = _questions.Count;
        var answeredCount = _questions.Count(question => question.HasSelection);
        QuestionCountTextBlock.Text = string.Format(LocalizationService.GetString("QuizQuestionCountFormat"), questionCount);
        AnsweredCountTextBlock.Text = string.Format(LocalizationService.GetString("QuizAnsweredCountFormat"), answeredCount, questionCount);

        CheckAnswersButton.IsEnabled = !_isSubmitted && questionCount > 0;
        ResetAnswersButton.IsEnabled = _isSubmitted || answeredCount > 0;

        if (!_isSubmitted)
        {
            if (ScoreBadge.Visibility == Visibility.Visible && !animateScore)
            {
                ScoreBadge.Visibility = Visibility.Collapsed;
                ScoreTextBlock.Text = string.Empty;
            }
            else if (!animateScore)
            {
                ScoreTextBlock.Text = string.Empty;
            }

            return;
        }

        var correctCount = _questions.Count(question => question.IsCorrect == true);
        var percent = questionCount == 0
            ? 0
            : (int)Math.Round((double)correctCount / questionCount * 100);

        ScoreTextBlock.Text = string.Format(LocalizationService.GetString("QuizScoreFormat"), correctCount, questionCount, percent);

        if (animateScore && !UiMotion.PrefersReducedMotion)
            UiMotion.AnimateInlineReveal(ScoreBadge, true);
        else
            ScoreBadge.Visibility = Visibility.Visible;
    }

    private void SetQuestionFocus(int index)
    {
        if (index < 0 || index >= _questions.Count) return;

        for (int i = 0; i < _questions.Count; i++)
        {
            _questions[i].IsFocused = (i == index);
        }

        var container = QuestionsItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        container?.BringIntoView();
    }

    private int GetFocusedQuestionIndex()
    {
        for (int i = 0; i < _questions.Count; i++)
        {
            if (_questions[i].IsFocused) return i;
        }
        return -1;
    }

    private sealed class QuizPreviewQuestion : INotifyPropertyChanged
    {
        private readonly QuizQuestion _source;
        private bool _isSubmitted;
        private bool _isFocused;
        private int _number;
        private QuizQuestionType _type;
        private string _question = string.Empty;
        private string _explanation = string.Empty;
        private string _hint = string.Empty;
        private string _plainTextAnswer = string.Empty;
        private string _plainTextUserAnswer = string.Empty;
        private int _plainTextCharLimit = 500;  // Default 500 characters
        private bool _plainTextMatchCase;
        private bool _plainTextIgnorePunctuation;
        public QuizPreviewQuestion(int number, QuizQuestion source)
        {
            _source = source;
            Number = number;
            _type = source.Type;
            Question = source.Question;
            Explanation = source.Explanation;
            Hint = source.Hint;

            _plainTextAnswer = source.PlainTextAnswer ?? string.Empty;
            _plainTextCharLimit = source.PlainTextCharLimit;
            _plainTextMatchCase = source.PlainTextMatchCase;
            _plainTextIgnorePunctuation = source.PlainTextIgnorePunctuation;

            foreach (var option in source.Options)
                Options.Add(new QuizPreviewOption(this, option.Text, option.IsCorrect));
            EnsureValidOptions();
        }

        public string PlainTextAnswer
        {
            get => _plainTextAnswer;
            set
            {
                if (_plainTextAnswer == (value ?? string.Empty))
                    return;

                _plainTextAnswer = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlainTextAnswerCharCountText));
            }
        }

        public string PlainTextUserAnswer
        {
            get => _plainTextUserAnswer;
            set
            {
                if (_isSubmitted)
                    return;

                var nextValue = value ?? string.Empty;
                if (_plainTextUserAnswer == nextValue)
                    return;

                _plainTextUserAnswer = nextValue;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlainTextPracticeCharCountText));
                OnPropertyChanged(nameof(HasSelection));
            }
        }

        public int PlainTextCharLimit
        {
            get => _plainTextCharLimit;
            set
            {
                if (_plainTextCharLimit == value)
                    return;

                _plainTextCharLimit = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(PlainTextAnswerCharCountText));
                OnPropertyChanged(nameof(PlainTextPracticeCharCountText));
            }
        }

        public string PlainTextAnswerCharCountText =>
            string.Format(LocalizationService.GetString("QuizCharactersFormat"), PlainTextAnswer?.Length ?? 0);

        public string PlainTextPracticeCharCountText =>
            string.Format(LocalizationService.GetString("QuizCharactersFormat"), PlainTextUserAnswer?.Length ?? 0);

        public bool PlainTextMatchCase
        {
            get => _plainTextMatchCase;
            set
            {
                if (_plainTextMatchCase == value)
                    return;

                _plainTextMatchCase = value;
                OnPropertyChanged();
            }
        }

        public bool PlainTextIgnorePunctuation
        {
            get => _plainTextIgnorePunctuation;
            set
            {
                if (_plainTextIgnorePunctuation == value)
                    return;

                _plainTextIgnorePunctuation = value;
                OnPropertyChanged();
            }
        }
        public bool IsPlainText => Type == QuizQuestionType.PlainText;

        public bool CanAddOption => Type != QuizQuestionType.TrueFalse && Type != QuizQuestionType.PlainText;

        public bool IsFocused
        {
            get => _isFocused;
            set
            {
                if (_isFocused != value)
                {
                    _isFocused = value;
                    OnPropertyChanged();
                }
            }
        }

        public int Number
        {
            get => _number;
            set
            {
                if (_number == value)
                    return;

                _number = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HeaderText));
            }
        }

        public QuizQuestionType Type
        {
            get => _type;
            set
            {
                if (_type == value)
                    return;

                _type = value;
                NormalizeOptionsForType();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HeaderText));
                OnPropertyChanged(nameof(CanAddOption));
                OnPropertyChanged(nameof(CanReorderOptions));
                OnPropertyChanged(nameof(IsPlainText));
                foreach (var option in Options)
                    option.RefreshState();
            }
        }

        public string Question
        {
            get => _question;
            set
            {
                if (_question == (value ?? string.Empty))
                    return;

                _question = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string Explanation
        {
            get => _explanation;
            set
            {
                if (_explanation == (value ?? string.Empty))
                    return;

                _explanation = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasVisibleExplanation));
                OnPropertyChanged(nameof(ExplanationText));
            }
        }
        public string Hint
        {
            get => _hint;
            set
            {
                if (_hint == (value ?? string.Empty)) return;
                _hint = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<QuizPreviewOption> Options { get; } = new();

        public bool CanReorderOptions => Type != QuizQuestionType.TrueFalse && Options.Count > 1;

        public bool CanRemoveOption => Type != QuizQuestionType.TrueFalse && Options.Count > 2;

        public bool HasSelection => IsPlainText
            ? !string.IsNullOrWhiteSpace(PlainTextUserAnswer)
            : Options.Any(option => option.IsSelected);

        public bool IsSubmitted => _isSubmitted;

        public bool? IsCorrect { get; private set; }

        public string HeaderText => string.Format(
            LocalizationService.GetString("QuizQuestionHeaderFormat"),
            Number,
            Type switch
            {
                QuizQuestionType.TrueFalse => LocalizationService.GetString("QuizTypeTrueFalse"),
                QuizQuestionType.MultipleChoice => LocalizationService.GetString("QuizTypeMultipleChoice"),
                QuizQuestionType.PlainText => LocalizationService.GetString("QuizTypePlainText"),
                _ => LocalizationService.GetString("QuizTypeSingleChoice")
            });

        public bool HasResult => _isSubmitted;

        public string ResultText
        {
            get
            {
                if (!_isSubmitted)
                    return string.Empty;

                if (!HasSelection)
                    return LocalizationService.GetString("QuizUnanswered");

                return IsCorrect == true
                    ? LocalizationService.GetString("QuizCorrect")
                    : LocalizationService.GetString("QuizIncorrect");
            }
        }

        public Brush ResultBackground
        {
            get
            {
                if (!_isSubmitted || !HasSelection)
                    return UnansweredBrush;

                return IsCorrect == true ? CorrectBrush : IncorrectBrush;
            }
        }

        public bool HasVisibleExplanation => _isSubmitted && !string.IsNullOrWhiteSpace(Explanation);

        public string ExplanationText => string.Format(LocalizationService.GetString("QuizExplanationFormat"), Explanation);

        public bool ShowPlainTextCorrectAnswer => IsPlainText && _isSubmitted && IsCorrect != true;

        public string CorrectPlainTextAnswerDisplay
        {
            get
            {
                var correctAnswer = string.IsNullOrWhiteSpace(PlainTextAnswer)
                    ? LocalizationService.GetString("QuizNoAnswer")
                    : PlainTextAnswer.Trim();

                return string.Format(LocalizationService.GetString("QuizCorrectAnswerFormat"), correctAnswer);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SelectOption(QuizPreviewOption option)
        {
            if (_isSubmitted || IsPlainText)
                return;

            if (Type == QuizQuestionType.MultipleChoice)
            {
                option.IsSelected = !option.IsSelected;
                option.RefreshState();
            }
            else
            {
                foreach (var item in Options)
                {
                    item.IsSelected = ReferenceEquals(item, option);
                    item.RefreshState();
                }
            }

            OnPropertyChanged(nameof(HasSelection));
        }

        public QuizPreviewOption? AddOption(string text, bool isCorrect)
        {
            if (!CanAddOption)
                return null;

            var option = new QuizPreviewOption(this, text, isCorrect);
            Options.Add(option);
            OnPropertyChanged(nameof(CanReorderOptions));
            OnPropertyChanged(nameof(CanRemoveOption));
            RefreshState();
            return option;
        }

        public void RemoveOption(QuizPreviewOption option)
        {
            if (!CanRemoveOption || !Options.Contains(option))
                return;

            var removedWasCorrect = option.IsCorrect;
            Options.Remove(option);

            if (removedWasCorrect && Options.All(item => !item.IsCorrect))
                Options[0].SetCorrectSilently(true);

            if (Options.Count > 0 && Options.All(item => string.IsNullOrWhiteSpace(item.Text)))
                Options[0].Text = LocalizationService.GetString("NewQuizCorrectAnswer");

            OnPropertyChanged(nameof(CanReorderOptions));
            OnPropertyChanged(nameof(CanRemoveOption));
            RefreshState();
        }

        public void RefreshOptionOrderState()
        {
            OnPropertyChanged(nameof(CanReorderOptions));
            OnPropertyChanged(nameof(CanRemoveOption));
            foreach (var option in Options)
                option.RefreshState();
        }

        public void ResetOptions(IEnumerable<QuizOption> options)
        {
            Options.Clear();
            foreach (var option in options)
                Options.Add(new QuizPreviewOption(this, option.Text, option.IsCorrect));

            _isSubmitted = false;
            IsCorrect = null;
            EnsureValidOptions();
            OnPropertyChanged(nameof(CanReorderOptions));
            OnPropertyChanged(nameof(CanRemoveOption));
            RefreshState();
        }

        public void Submit()
        {
            if (IsPlainText)
            {
                _isSubmitted = true;
                var userAnswer = NormalizePlainTextAnswer(PlainTextUserAnswer, PlainTextIgnorePunctuation);
                var correctAnswer = NormalizePlainTextAnswer(PlainTextAnswer, PlainTextIgnorePunctuation);
                var comparison = PlainTextMatchCase
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;
                IsCorrect = !string.IsNullOrEmpty(userAnswer)
                    && !string.IsNullOrEmpty(correctAnswer)
                    && string.Equals(userAnswer, correctAnswer, comparison);
                RefreshState();
                return;
            }

            _isSubmitted = true;
            var selectedOptions = Options.Where(option => option.IsSelected).ToList();
            var correctCount = Options.Count(option => option.IsCorrect);
            IsCorrect = selectedOptions.Count > 0
                && selectedOptions.Count == correctCount
                && selectedOptions.All(option => option.IsCorrect);
            RefreshState();
        }

        public void Reset()
        {
            if (IsPlainText)
            {
                _isSubmitted = false;
                IsCorrect = null;
                _plainTextUserAnswer = string.Empty;
                OnPropertyChanged(nameof(PlainTextUserAnswer));
                OnPropertyChanged(nameof(PlainTextPracticeCharCountText));
                OnPropertyChanged(nameof(HasSelection));
                RefreshState();
                return;
            }

            _isSubmitted = false;
            IsCorrect = null;

            foreach (var option in Options)
            {
                option.IsSelected = false;
                option.RefreshState();
            }

            RefreshState();
        }

        public QuizQuestion ToModel()
        {
            if (IsPlainText)
            {
                return new QuizQuestion
                {
                    Type = QuizQuestionType.PlainText,
                    Question = Question.Trim(),
                    Explanation = Explanation.Trim(),
                    Hint = Hint?.Trim() ?? string.Empty,
                    PlainTextAnswer = PlainTextAnswer.Trim(),
                    PlainTextCharLimit = PlainTextCharLimit,
                    PlainTextMatchCase = PlainTextMatchCase,
                    PlainTextIgnorePunctuation = PlainTextIgnorePunctuation,
                    SetIndex = Math.Max(1, _source.SetIndex),
                    Options = new List<QuizOption>()
                };
            }

            var options = Options
                .Where(option => !string.IsNullOrWhiteSpace(option.Text))
                .Select(option => new QuizOption
                {
                    Text = option.Text.Trim(),
                    IsCorrect = option.IsCorrect
                })
                .ToList();
            NormalizeModelOptions(Type, options);
            if (options.Count > 0 && options.All(option => !option.IsCorrect))
                options[0].IsCorrect = true;

            return new QuizQuestion
            {
                Type = Type,
                Question = Question.Trim(),
                Explanation = Explanation.Trim(),
                Hint = Hint.Trim(),
                SetIndex = Math.Max(1, _source.SetIndex),
                Options = options,
                PlainTextAnswer = string.Empty,
                PlainTextCharLimit = 500,
                PlainTextMatchCase = false,
                PlainTextIgnorePunctuation = false
            };
        }

        private void EnsureValidOptions()
        {
            if (Options.Count == 0)
            {
                if (Type == QuizQuestionType.TrueFalse)
                    ResetToTrueFalseOptions();
                else
                {
                    Options.Add(new QuizPreviewOption(this, LocalizationService.GetString("NewQuizCorrectAnswer"), true));
                    Options.Add(new QuizPreviewOption(this, LocalizationService.GetString("NewQuizWrongAnswer"), false));
                    Options.Add(new QuizPreviewOption(this, LocalizationService.GetString("NewQuizWrongAnswer"), false));
                }
            }

            NormalizeOptionsForType();
        }

        private void NormalizeOptionsForType()
        {
            if (Type == QuizQuestionType.PlainText)
            {
                Options.Clear();
                return;
            }

            if (Type == QuizQuestionType.TrueFalse)
            {
                ResetToTrueFalseOptions();
                return;
            }

            if (Type == QuizQuestionType.SingleChoice)
            {
                var firstCorrect = Options.FirstOrDefault(option => option.IsCorrect);
                foreach (var option in Options)
                    option.SetCorrectSilently(ReferenceEquals(option, firstCorrect));
            }

            if (Options.Count > 0 && Options.All(option => !option.IsCorrect))
                Options[0].SetCorrectSilently(true);

            OnPropertyChanged(nameof(CanReorderOptions));
            OnPropertyChanged(nameof(CanRemoveOption));
        }

        private void ResetToTrueFalseOptions()
        {
            var falseIsCorrect = Options.Any(option =>
                option.IsCorrect
                && ((option.Text ?? string.Empty).Contains("false", StringComparison.OrdinalIgnoreCase)
                    || (option.Text ?? string.Empty).Contains("klaid", StringComparison.OrdinalIgnoreCase)));

            Options.Clear();
            Options.Add(new QuizPreviewOption(this, LocalizationService.GetString("QuizOptionTrue"), !falseIsCorrect));
            Options.Add(new QuizPreviewOption(this, LocalizationService.GetString("QuizOptionFalse"), falseIsCorrect));
            OnPropertyChanged(nameof(CanReorderOptions));
            OnPropertyChanged(nameof(CanRemoveOption));
        }

        private static void NormalizeModelOptions(QuizQuestionType type, List<QuizOption> options)
        {
            if (type == QuizQuestionType.TrueFalse)
            {
                var falseIsCorrect = options.Any(option =>
                    option.IsCorrect
                    && ((option.Text ?? string.Empty).Contains("false", StringComparison.OrdinalIgnoreCase)
                        || (option.Text ?? string.Empty).Contains("klaid", StringComparison.OrdinalIgnoreCase)));
                options.Clear();
                options.Add(new QuizOption { Text = LocalizationService.GetString("QuizOptionTrue"), IsCorrect = !falseIsCorrect });
                options.Add(new QuizOption { Text = LocalizationService.GetString("QuizOptionFalse"), IsCorrect = falseIsCorrect });
                return;
            }

            if (type != QuizQuestionType.SingleChoice)
                return;

            var foundCorrect = false;
            foreach (var option in options)
            {
                if (!option.IsCorrect)
                    continue;

                if (!foundCorrect)
                    foundCorrect = true;
                else
                    option.IsCorrect = false;
            }
        }

        private void RefreshState()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(IsSubmitted));
            OnPropertyChanged(nameof(IsCorrect));
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(ResultText));
            OnPropertyChanged(nameof(ResultBackground));
            OnPropertyChanged(nameof(HasVisibleExplanation));
            OnPropertyChanged(nameof(ExplanationText));
            OnPropertyChanged(nameof(IsPlainText));
            OnPropertyChanged(nameof(PlainTextAnswerCharCountText));
            OnPropertyChanged(nameof(PlainTextPracticeCharCountText));
            OnPropertyChanged(nameof(ShowPlainTextCorrectAnswer));
            OnPropertyChanged(nameof(CorrectPlainTextAnswerDisplay));
            OnPropertyChanged(nameof(PlainTextMatchCase));
            OnPropertyChanged(nameof(PlainTextIgnorePunctuation));

            foreach (var option in Options)
                option.RefreshState();
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

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class QuizPreviewOption : INotifyPropertyChanged
    {
        private string _text = string.Empty;
        private bool _isCorrect;
        private bool _isSelected;

        public QuizPreviewOption(QuizPreviewQuestion parent, string text, bool isCorrect)
        {
            Parent = parent;
            Text = text;
            IsCorrect = isCorrect;
        }

        public QuizPreviewQuestion Parent { get; }

        public string Text
        {
            get => _text;
            set
            {
                if (_text == (value ?? string.Empty))
                    return;

                _text = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public bool IsCorrect
        {
            get => _isCorrect;
            set
            {
                if (_isCorrect == value)
                    return;

                _isCorrect = value;
                if (value && Parent.Type != QuizQuestionType.MultipleChoice)
                {
                    foreach (var option in Parent.Options.Where(option => !ReferenceEquals(option, this)))
                        option.SetCorrectSilently(false);
                }

                OnPropertyChanged();
                RefreshState();
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                RefreshState();
            }
        }

        public bool IsCorrectAfterSubmit => Parent.IsSubmitted && IsCorrect;

        public bool IsIncorrectSelectedAfterSubmit => Parent.IsSubmitted && IsSelected && !IsCorrect;

        public string MarkerText
        {
            get
            {
                if (Parent.IsSubmitted && IsCorrect)
                    return "✓";

                if (Parent.IsSubmitted && IsSelected && !IsCorrect)
                    return "×";

                if (Parent.Type == QuizQuestionType.MultipleChoice)
                    return IsSelected ? "☑" : "☐";

                return IsSelected ? "●" : "○";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void RefreshState()
        {
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(IsCorrect));
            OnPropertyChanged(nameof(IsCorrectAfterSubmit));
            OnPropertyChanged(nameof(IsIncorrectSelectedAfterSubmit));
            OnPropertyChanged(nameof(MarkerText));
        }

        public void SetCorrectSilently(bool value)
        {
            if (_isCorrect == value)
                return;

            _isCorrect = value;
            RefreshState();
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

}
