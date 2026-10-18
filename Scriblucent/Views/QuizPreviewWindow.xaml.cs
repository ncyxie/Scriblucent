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

public partial class QuizPreviewWindow : Window, INotifyPropertyChanged, Scriblucent.Services.IHostedDocumentEditor
{
    private ScrollViewer? QuestionsScrollViewer
    {
        get
        {
            if (QuestionsItemsControl is null)
                return null;

            QuestionsItemsControl.ApplyTemplate();
            return QuestionsItemsControl.Template?.FindName("QuestionsScrollViewer", QuestionsItemsControl)
                as ScrollViewer;
        }
    }

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

    private readonly Scriblucent.ViewModels.BulkObservableCollection<QuizPreviewQuestion> _questions;
    private readonly QuizDocument _sourceDocument;
    private readonly ObservableCollection<QuizNoteLinkOption> _noteLinkOptions = new();
    private Action<Guid>? _openNoteAction;
    private readonly Action? _requestImmediateSave;
    private string _modelDisplayName = string.Empty;
    private bool _isSubmitted;
    private QuizNoteLinkOption? _selectedLinkedNote;
    private string _lastSavedSnapshot = string.Empty;
    // Cheap stand-in for a full questions/options snapshot - bumped on every tracked question/option
    // change instead of re-joining every field of every question and option on every keystroke.
    private long _questionsChangeVersion;
    private bool _isInitializing = true;
    private bool _allowCloseWithoutPrompt;
    private bool _isTagsPanelOpen;
    private bool _lastSavedTagsPanelOpen;
    private bool _aiReviewNoticeDismissed;

    public QuizPreviewWindow(
        QuizDocument document,
        IEnumerable<QuizNoteLinkOption>? noteOptions = null,
        string? modelDisplayName = null,
        string? title = null,
        Action<Guid>? openNoteAction = null,
        Action? requestImmediateSave = null)
        : this(document, noteOptions, modelDisplayName, title, openNoteAction, requestImmediateSave, initializeOnly: false)
    {
    }

    public QuizPreviewWindow(
        QuizDocument document,
        string? modelDisplayName = null,
        string? title = null)
        : this(document, null, modelDisplayName, title, null, null, initializeOnly: false)
    {
    }

    private QuizPreviewWindow(
        QuizDocument document,
        IEnumerable<QuizNoteLinkOption>? noteOptions,
        string? modelDisplayName,
        string? title,
        Action<Guid>? openNoteAction,
        Action? requestImmediateSave,
        bool initializeOnly)
    {
        _sourceDocument = document;
        _requestImmediateSave = requestImmediateSave;
        _questions = new Scriblucent.ViewModels.BulkObservableCollection<QuizPreviewQuestion>(
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
        TagsTextBox.Text = document.Tags is null
            ? string.Empty
            : string.Join(", ", document.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()));
        SetTagsPanelOpen(document.IsEditorTagsPanelOpen ?? false, animate: false);
        _lastSavedTagsPanelOpen = _isTagsPanelOpen;
        _aiReviewNoticeDismissed = document.AiReviewNoticeDismissed;
        QuestionsItemsControl.ItemsSource = _questions;
        ConfigureAiGeneratedIndicator(modelDisplayName ?? document.AiModelDisplayName);
        AiFeatureGate.Changed += QuizPreviewWindow_AiFeatureGateChanged;
        Closed += (_, _) => AiFeatureGate.Changed -= QuizPreviewWindow_AiFeatureGateChanged;
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
        InitializeBulkSelection();
        WindowContentEnterHelper.AnimateOnLoaded(this);
    }
    public string EditorTitle => TitleTextBox.Text.Trim();

    public IReadOnlyList<string> Tags => ParseTags(TagsTextBox.Text);

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
            var retained = _questions.ToHashSet();
            foreach (var removed in _trackedQuestions.Where(question => !retained.Contains(question)).ToArray())
                DetachQuestionChangeTracking(removed);
            foreach (var question in _questions)
                AttachQuestionChangeTracking(question);
        }

        if (_bulkQuestionEdit) return;
        BulkSelection.SetItems(_questions.Select(question => question.Id));
        _questionsChangeVersion++;
        UpdateEditedIndicator();
    }

    private void AttachQuestionChangeTracking(QuizPreviewQuestion question)
    {
        if (!_trackedQuestions.Add(question)) return;
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
        _trackedQuestions.Remove(question);
        question.PropertyChanged -= QuizQuestion_PropertyChanged;
        question.Options.CollectionChanged -= QuizOptions_CollectionChanged;

        foreach (var option in question.Options)
            option.PropertyChanged -= QuizOption_PropertyChanged;
    }

    private void QuizQuestion_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_bulkQuestionEdit) return;
        if (e.PropertyName == nameof(QuizPreviewQuestion.PlainTextUserAnswer)
            || e.PropertyName == nameof(QuizPreviewQuestion.HasSelection))
        {
            UpdateSummary();
        }

        if (IsSavedQuestionProperty(e.PropertyName))
        {
            _questionsChangeVersion++;
            UpdateEditedIndicator();
        }
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

        if (_bulkQuestionEdit) return;
        _questionsChangeVersion++;
        UpdateEditedIndicator();
    }

    private void QuizOption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_bulkQuestionEdit) return;
        if (IsSavedOptionProperty(e.PropertyName))
        {
            _questionsChangeVersion++;
            UpdateEditedIndicator();
        }
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
        _lastSavedTagsPanelOpen = _isTagsPanelOpen;
        UpdateEditedIndicator();
    }

    // Excluded from GetEditorSnapshot/HasUnsavedChanges (toggling it shouldn't prompt a save on
    // close), but still needs to reach disk eventually - piggybacking on the next natural autosave
    // tick instead of triggering an immediate full-collection rewrite (and its visible "saved"
    // flash) on every single toggle click.
    public bool HasPendingAutoSaveChanges() => HasUnsavedChanges() || _isTagsPanelOpen != _lastSavedTagsPanelOpen;

    public void MarkCurrentStateAutoSaved()
    {
        _lastSavedSnapshot = GetEditorSnapshot();
        _lastSavedTagsPanelOpen = _isTagsPanelOpen;
        ShowAutoSaveIndicator();
    }

    private void ShowAutoSaveIndicator()
    {
        EditedIndicatorText?.ShowSaved(UpdateEditedIndicator);
    }

    public void ReportWorkspaceSaveFailure() => EditedIndicatorText?.ShowFailure();

    private bool HasUnsavedChanges()
    {
        if (_isInitializing)
            return false;

        return !string.Equals(GetEditorSnapshot(), _lastSavedSnapshot, StringComparison.Ordinal);
    }

    private void UpdateEditedIndicator()
    {
        if (_bulkQuestionEdit) return;
        WorkspaceStateChanged?.Invoke();
        if (_isInitializing || EditedIndicatorText is null)
            return;

        EditedIndicatorText.SetEdited(HasUnsavedChanges());
    }

    private string GetEditorSnapshot()
    {
        // Question/option text and structure are represented by _questionsChangeVersion instead of
        // re-joining every field of every question and option here - that rebuild used to run on every
        // keystroke while editing a question, explanation, or answer option.
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
            string.Join('\u001E', Tags),
            (_sourceDocument.GroupId?.ToString() ?? string.Empty),
            _questionsChangeVersion.ToString(CultureInfo.InvariantCulture),
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
            WorkspaceOwner,
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
        if (_workspaceSave is not null) { _workspaceSave(); return; }
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
            CommitTextBoxBindingsInVisualTree(ContentRoot);
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
            Tags = Tags.ToList(),
            IsEditorTagsPanelOpen = _isTagsPanelOpen,
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
            ShuffleQuestions = _sourceDocument.ShuffleQuestions,
            ShuffleOptions = _sourceDocument.ShuffleOptions,
            PartialCreditMultipleChoice = _sourceDocument.PartialCreditMultipleChoice,
            ExamMode = _sourceDocument.ExamMode,
            Attempts = _sourceDocument.Attempts?.ToList() ?? existingDocument?.Attempts?.ToList() ?? new List<QuizAttempt>(),
            ProgressSession = _sourceDocument.ProgressSession,
            AiReviewNoticeDismissed = _aiReviewNoticeDismissed || existingDocument?.AiReviewNoticeDismissed == true || _sourceDocument.AiReviewNoticeDismissed
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

        var questionsScrollViewer = QuestionsScrollViewer;
        SubmitAnswers();
        if (UiMotion.PrefersReducedMotion || questionsScrollViewer is null)
        {
            return;
        }

        questionsScrollViewer.Opacity = .75;
        UiMotion.Fade(
            questionsScrollViewer,
            1,
            120,
            UiMotion.OutCubic);
    }

    private void ResetAnswersButton_Click(object sender, RoutedEventArgs e)
    {
        void ResetAnswers()
        {
            _isSubmitted = false;
            foreach (var question in _questions)
                question.Reset();

            UpdateSummary(animateScore: false);
        }

        // The accepted reset is a model operation, independent of visual completion.
        ResetAnswers();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_workspaceClose is not null) { _workspaceClose(); return; }
        switch (GetCloseDecision())
        {
            case UnsavedCloseDecision.Cancel:
                return;
            case UnsavedCloseDecision.SaveAndClose:
                if (_workspaceSave is not null) { if (_workspaceSave()) _workspaceClose?.Invoke(); return; }
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

        // A saved ProgressSession means a previous attempt was closed mid-way - ask before
        // dropping back in, rather than silently resuming or silently discarding it. Exam mode
        // never offers a resume (closing mid-attempt forfeits it), so skip straight past this -
        // any leftover session is just stale state from before exam mode was turned on.
        var resumeFrom = _sourceDocument.ProgressSession;
        if (resumeFrom is not null && _sourceDocument.ExamMode)
        {
            resumeFrom = null;
            _sourceDocument.ProgressSession = null;
        }
        else if (resumeFrom is not null)
        {
            var choice = ModernDialog.Show(
                WorkspaceOwner,
                LocalizationService.GetString("QuizResumePromptTitle"),
                LocalizationService.GetString("QuizResumePromptMessage"),
                ModernDialogTone.Question,
                LocalizationService.GetString("QuizResumePromptResume"),
                LocalizationService.GetString("Cancel"),
                LocalizationService.GetString("QuizResumePromptStartOver"),
                primaryStyle: ModernDialogButtonStyle.Primary,
                secondaryStyle: ModernDialogButtonStyle.Secondary,
                defaultChoice: ModernDialogResult.Primary);

            switch (choice)
            {
                case ModernDialogResult.Primary:
                    break;
                case ModernDialogResult.Secondary:
                    resumeFrom = null;
                    _sourceDocument.ProgressSession = null;
                    UpdateStartQuizButtonLabel();
                    _requestImmediateSave?.Invoke();
                    break;
                default:
                    return;
            }
        }

        try
        {
            // Preferences (time limit, passing score, shuffle, partial credit, exam mode) are
            // configured ahead of time via the standalone Options button - starting a quiz
            // launches straight in with whatever is already saved on the document, instead of
            // re-asking every time.
            var quizModeWindow = new QuizModeWindow(
                quizDocument,
                quizDocument.TimeLimitSeconds ?? 0,
                resumeFrom)
            {
                Owner = WorkspaceOwner
            };
            // The actual status of the quiz - not which button closed the window - decides
            // whether an attempt gets recorded. This fires the instant a score is computed
            // (Submit confirmed, or the timer running out), so it's recorded immediately and
            // survives no matter how the window is closed afterward (Done, system X, ...).
            quizModeWindow.AttemptSubmitted += attempt => RecordCompletedAttempt(quizDocument, attempt);
            // Not submitted - capture wherever the attempt was left off so Start Quiz resumes
            // there next time, instead of silently discarding an in-progress attempt. Fires on
            // Closing so this runs no matter how the window closed (Done, X, Alt+F4, or an owner
            // window cascading its own close into this one).
            quizModeWindow.ProgressAbandoned += session =>
            {
                if (_sourceDocument.ExamMode)
                    return;

                _sourceDocument.ProgressSession = session;
                SaveQuizProgress();
                RefreshProgressPanel();
                _requestImmediateSave?.Invoke();
            };
            quizModeWindow.ShowDialog();
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

    // QuizModeWindow.LastAttempt/AttemptSubmitted track correctness on attemptedDocument.Questions,
    // which ToDocument() always builds as brand-new QuizQuestion objects (see ToModel()) - not the
    // same references as _sourceDocument.Questions. Match by the now-stable Id to fold
    // LastAttemptCorrect back into the real saved questions so "Practice Missed Questions" can
    // find them later.
    private void RecordCompletedAttempt(QuizDocument attemptedDocument, QuizAttempt attempt)
    {
        var attemptedById = attemptedDocument.Questions.ToDictionary(q => q.Id);
        foreach (var sourceQuestion in _sourceDocument.Questions ?? new List<QuizQuestion>())
        {
            if (attemptedById.TryGetValue(sourceQuestion.Id, out var attempted))
                sourceQuestion.LastAttemptCorrect = attempted.LastAttemptCorrect;
        }

        _sourceDocument.ProgressSession = null;
        _sourceDocument.Attempts ??= new List<QuizAttempt>();
        _sourceDocument.Attempts.Add(attempt);
        SaveQuizProgress();
        RefreshProgressPanel();
        // A finished attempt (new attempt-history entry, updated LastAttemptCorrect) is exactly
        // the kind of change that shouldn't wait on the ~30s autosave timer - save it immediately,
        // same as Flashcards effectively does since study progress rides along on every edit.
        _requestImmediateSave?.Invoke();
    }

    // The only place quiz preferences (time limit, passing score, shuffle, partial credit, exam
    // mode) get configured - Start Quiz and Practice Missed both launch straight in with whatever's
    // saved here instead of asking again.
    private void QuizOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        var quizDocument = ToDocument(_sourceDocument);
        var optionsDialog = new QuizStartOptionsDialog(
            quizDocument.TimeLimitSeconds,
            quizDocument.PassingScorePercent,
            quizDocument.ShuffleQuestions,
            quizDocument.ShuffleOptions,
            quizDocument.PartialCreditMultipleChoice,
            quizDocument.ExamMode)
        {
            Owner = WorkspaceOwner
        };

        if (optionsDialog.ShowDialog() != true)
            return;

        var timeLimitSeconds = optionsDialog.TimeLimitSeconds;
        _sourceDocument.TimeLimitSeconds = timeLimitSeconds == 0 ? null : timeLimitSeconds;
        _sourceDocument.PassingScorePercent = optionsDialog.PassingScorePercent;
        _sourceDocument.ShuffleQuestions = optionsDialog.ShuffleQuestions;
        _sourceDocument.ShuffleOptions = optionsDialog.ShuffleOptions;
        _sourceDocument.PartialCreditMultipleChoice = optionsDialog.PartialCreditMultipleChoice;
        _sourceDocument.ExamMode = optionsDialog.ExamMode;
        if (optionsDialog.ExamMode && _sourceDocument.ProgressSession is not null)
        {
            _sourceDocument.ProgressSession = null;
            UpdateStartQuizButtonLabel();
        }

        UpdateEditedIndicator();
        _requestImmediateSave?.Invoke();
    }

    // Launches an ephemeral, filtered QuizModeWindow containing only the questions the last
    // attempt got wrong (per-question LastAttemptCorrect, not the in-session-only wrong list
    // QuizModeWindow's own "Retry Wrong Only" uses) - the persisted equivalent of that button,
    // reachable after reopening the quiz in a new session. Never offers/captures resume - a
    // filtered missed-only set would hijack the main Start Quiz button into resuming the wrong
    // question set.
    private void PracticeMissedButton_Click(object sender, RoutedEventArgs e)
    {
        var quizDocument = ToDocument(_sourceDocument);
        var missedQuestions = quizDocument.Questions.Where(q => q.LastAttemptCorrect == false).ToList();
        if (missedQuestions.Count == 0)
        {
            ModernMessageBox.Show(
                LocalizationService.GetString("QuizNoMissedQuestions"),
                LocalizationService.GetString("QuizMode"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            UpdatePracticeMissedVisibility();
            return;
        }

        var practiceDocument = new QuizDocument
        {
            Id = quizDocument.Id,
            // Matches QuizModeWindow.RetryWrongButton_Click's own "(Wrong Only)" convention, so a
            // partial-question attempt is never confused with a full-quiz one in history.
            Title = quizDocument.Title + " (Missed Only)",
            Tags = quizDocument.Tags,
            Questions = missedQuestions,
            CreatedAt = quizDocument.CreatedAt,
            AiModelDisplayName = quizDocument.AiModelDisplayName,
            SourceNoteId = quizDocument.SourceNoteId,
            GroupId = quizDocument.GroupId,
            PassingScorePercent = quizDocument.PassingScorePercent,
            ShuffleQuestions = quizDocument.ShuffleQuestions,
            ShuffleOptions = quizDocument.ShuffleOptions,
            PartialCreditMultipleChoice = quizDocument.PartialCreditMultipleChoice,
            ExamMode = quizDocument.ExamMode
        };

        try
        {
            // No time limit for missed-question practice by default (the main quiz's limit was
            // calibrated for the full question set) - same "launch straight in" rule as Start
            // Quiz applies here too, using whatever's already configured via the Options button.
            var quizModeWindow = new QuizModeWindow(practiceDocument, practiceDocument.TimeLimitSeconds ?? 0)
            {
                Owner = WorkspaceOwner
            };
            quizModeWindow.AttemptSubmitted += attempt => RecordCompletedAttempt(practiceDocument, attempt);
            quizModeWindow.ShowDialog();
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

    private void UpdatePracticeMissedVisibility()
    {
        if (FindName("PracticeMissedButton") is not System.Windows.Controls.Button button)
            return;

        var hasMissed = _sourceDocument.Questions?.Any(q => q.LastAttemptCorrect == false) == true;
        button.Visibility = hasMissed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStartQuizButtonLabel()
    {
        if (FindName("StartQuizButton") is not System.Windows.Controls.Button button)
            return;

        button.Content = LocalizationService.GetString(
            _sourceDocument.ProgressSession is not null ? "QuizResumeButton" : "QuizStartButton");
    }

    private void RefreshProgressPanel()
    {
        UpdatePracticeMissedVisibility();
        UpdateStartQuizButtonLabel();
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
            Owner = WorkspaceOwner
        };

        window.ShowDialog();
        RefreshProgressPanel();
        UpdateEditedIndicator();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveAndClose();
    }

    private void TagsPanelToggleButton_Click(object sender, RoutedEventArgs e)
        => SetTagsPanelOpen(!_isTagsPanelOpen, animate: true);

    private void SetTagsPanelOpen(bool isOpen, bool animate)
    {
        _isTagsPanelOpen = isOpen;
        UiMotion.AnimateLayoutReveal(TagsPanel, isOpen, animate: animate);
    }

    private void QuizPreviewWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_workspaceDisposed) return;
        if (!WindowCloseCascade.TryCloseOwnedWindows(this))
        {
            e.Cancel = true;
            return;
        }

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

    // GIFT/CSV only - both are just question lists, unlike .scribquiz/.json which carry a whole
    // document's metadata. To bring in a .scribquiz as a whole, use the dashboard's Import button
    // (creates a new quiz) and "Combine quizzes" to fold it into this one afterward.
    private void ImportQuestionsButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new OpenFileDialog
        {
            Filter = LocalizationService.GetString("QuizImportQuestionsDialogFilter"),
            Title = LocalizationService.GetString("QuizImportQuestionsDialogTitle")
        };

        if (openFileDialog.ShowDialog(WorkspaceOwner) != true)
            return;

        try
        {
            List<QuizQuestion> imported;
            IReadOnlyList<string> warnings;

            var extension = Path.GetExtension(openFileDialog.FileName).ToLowerInvariant();
            var text = File.ReadAllText(openFileDialog.FileName, Encoding.UTF8);

            switch (extension)
            {
                case ".gift":
                {
                    var result = GiftQuizFormat.Parse(text, GetExportFileName());
                    imported = result.Document.Questions;
                    warnings = result.Warnings;
                    break;
                }
                case ".csv":
                {
                    var result = QuizCsvFormat.ParseWithWarnings(text, GetExportFileName());
                    imported = result.Document.Questions;
                    warnings = result.Warnings;
                    break;
                }
                default:
                    throw new InvalidOperationException(LocalizationService.GetString("QuizImportUnsupportedFormat"));
            }

            if (imported.Count == 0)
            {
                ModernMessageBox.Show(
                    LocalizationService.GetString("QuizImportNoQuestionsFound"),
                    LocalizationService.GetString("QuizImportQuestions"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!IsEditMode)
                IsEditMode = true;

            InsertQuestionsAtStart(imported.Select(question => new QuizPreviewQuestion(1, question)));

            RenumberQuestions();
            UpdateSummary();
            UpdateEditedIndicator();

            var summary = string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("QuizImportQuestionsAddedFormat"), imported.Count);
            if (warnings.Count > 0)
                summary += "\n\n" + string.Join("\n", warnings);

            ModernMessageBox.Show(
                summary,
                LocalizationService.GetString("QuizImportQuestions"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ModernMessageBox.Show(
                string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("QuizImportFailedFormat"), ex.Message),
                LocalizationService.GetString("ImportError"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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

        if (saveDialog.ShowDialog(WorkspaceOwner) != true)
            return;

        try
        {
            var extension = Path.GetExtension(saveDialog.FileName).ToLowerInvariant();
            IReadOnlyList<string> warnings = Array.Empty<string>();
            switch (extension)
            {
                case ".scribquiz":
                    QuizFileService.SaveScribQuizFile(saveDialog.FileName, ToDocument());
                    break;
                case ".gift":
                    GiftQuizFormat.SaveGiftFile(saveDialog.FileName, ToDocument());
                    break;
                case ".csv":
                    warnings = QuizCsvFormat.SaveCsvFile(saveDialog.FileName, ToDocument());
                    break;
                case ".xps":
                    // No real PDF conversion exists here (.NET has no built-in XPS->PDF writer) -
                    // .xps is the only printable/answer-key export, deliberately not offered as
                    // .pdf (a prior version of this code did, mislabeling XPS content as PDF).
                    ExportPrintableDocument(saveDialog.FileName);
                    break;
                default:
                    throw new InvalidOperationException(LocalizationService.GetString("QuizExportUnsupportedFormat"));
            }

            var message = LocalizationService.GetString("QuizExportComplete");
            if (warnings.Count > 0)
                message += "\n\n" + string.Join("\n", warnings);

            ShowExportDialog(
                LocalizationService.GetString("Success"),
                message);
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
        ModernDialog.ShowInfo(WorkspaceOwner, title, message);
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
        InsertQuestionsAtStart([question]);
        SetQuestionFocus(0);
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

    private void AnimateQuestionOptionEnter(QuizPreviewQuestion question, QuizPreviewOption option)
    {
        if (!TryGetQuestionOptionsList(question, out var optionsList))
            return;

        VerticalListMotion.AnimateItemEnter(this, optionsList, option);
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

    private void LinkedNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLinkedNote is null)
            return;

        _openNoteAction?.Invoke(SelectedLinkedNote.Id);
    }

    private static IReadOnlyList<string> ParseTags(string? rawTags)
    {
        if (string.IsNullOrWhiteSpace(rawTags))
            return Array.Empty<string>();

        return rawTags
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(tag => tag.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ToggleFullscreen()
    {
        if (ShellWindowChrome.IsFullscreenSuspended(WorkspaceOwner))
            ShellWindowChrome.ResumeFromFullscreen(WorkspaceOwner);
        else
            ShellWindowChrome.SuspendForFullscreen(WorkspaceOwner);
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
        if (!_isInitializing) SyncBulkSelectionChrome();
        if (isEditMode && _isSubmitted)
        {
            _isSubmitted = false;
            foreach (var question in _questions)
                question.Reset();
        }

        var questionsScrollViewer = QuestionsScrollViewer;
        if (_isInitializing || questionsScrollViewer is null || UiMotion.PrefersReducedMotion)
        {
            UpdateModeChrome();
            UpdateSummary();
            return;
        }

        UiMotion.Fade(
            questionsScrollViewer,
            0,
            UiMotion.NavFadeOutMs,
            UiMotion.InCubic,
            () =>
            {
                UpdateModeChrome();
                UpdateSummary();
                questionsScrollViewer.Opacity = 0;
                UiMotion.Fade(questionsScrollViewer, 1, UiMotion.QuickMs, UiMotion.OutCubic);
            });
    }

    private void UpdateModeChrome()
    {
        if (TitleTextBox is null)
            return;

        TitleTextBox.IsReadOnly = !IsEditMode;
        ModeToggleButton.ToolTip = LocalizationService.GetString(IsEditMode ? "ViewQuiz" : "EditQuiz");
        CheckAnswersButton.Visibility = IsEditMode ? Visibility.Collapsed : Visibility.Visible;
        ResetAnswersButton.Visibility = IsEditMode ? Visibility.Collapsed : Visibility.Visible;
        AddQuestionButton.Visibility = IsEditMode ? Visibility.Visible : Visibility.Collapsed;
        OnPropertyChanged(nameof(LinkedNoteVisibility));
        OnPropertyChanged(nameof(LinkedNoteEditorVisibility));
        OnPropertyChanged(nameof(LinkedNoteButtonText));
        OnPropertyChanged(nameof(LinkedNoteButtonToolTip));
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

    private void ExportPrintableDocument(string path)
    {
        var document = BuildPrintableDocument();
        using var xpsDocument = new XpsDocument(path, FileAccess.Write);
        var writer = XpsDocument.CreateXpsDocumentWriter(xpsDocument);
        writer.Write(((IDocumentPaginatorSource)document).DocumentPaginator);
    }

    private FlowDocument BuildPrintableDocument() => QuizPrintableExport.Build(ToDocument(),
        TypeScale.Px(TypeScale.Caption, 12), TypeScale.Px(TypeScale.Display, 24), TypeScale.Px(TypeScale.BlockTitle, 16));

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
        if (BulkSelectionBar.HandleOperationKey(e)) return;
        if (Scriblucent.Controls.EditorSelectionInput.HandleKey(e, BulkSelection, ConfirmDeleteSelectedQuestions)) return;
        if (e.Key == Key.Escape)
        {
            if (ShellWindowChrome.IsFullscreenSuspended(WorkspaceOwner))
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

        if (string.IsNullOrWhiteSpace(_modelDisplayName) || !AiFeatureGate.IsEnabled)
        {
            AiGeneratedInfoBadge.ToolTip = null;
            if (_isInitializing)
            {
                UiMotion.AnimateLayoutReveal(AiGeneratedInfoBadge, false, axis: Orientation.Horizontal, animate: false);
                UiMotion.AnimateLayoutReveal(AiReviewNotice, false, animate: false);
                return;
            }

            UiMotion.AnimateLayoutReveal(AiGeneratedInfoBadge, false, axis: Orientation.Horizontal);
            UiMotion.AnimateLayoutReveal(AiReviewNotice, false);
            return;
        }

        AiGeneratedInfoBadge.ToolTip = LocalizationService.FormatAiGeneratedTooltip(
            "QuizGeneratedWithModel",
            _modelDisplayName,
            includeReviewDisclaimer: !_aiReviewNoticeDismissed);

        if (_isInitializing)
        {
            UiMotion.AnimateLayoutReveal(AiGeneratedInfoBadge, true, axis: Orientation.Horizontal, animate: false);
            UiMotion.AnimateLayoutReveal(AiReviewNotice, !_aiReviewNoticeDismissed, animate: false);
            return;
        }

        UiMotion.AnimateLayoutReveal(AiGeneratedInfoBadge, true, axis: Orientation.Horizontal);
        UiMotion.AnimateLayoutReveal(AiReviewNotice, !_aiReviewNoticeDismissed);
    }

    public event EventHandler? AiReviewNoticeDismissedPersistRequested;

    private void AiReviewNotice_Dismissed(object sender, RoutedEventArgs e)
    {
        if (_aiReviewNoticeDismissed)
            return;

        _aiReviewNoticeDismissed = true;
        _sourceDocument.AiReviewNoticeDismissed = true;
        ConfigureAiGeneratedIndicator(_modelDisplayName);
        AiReviewNoticeDismissedPersistRequested?.Invoke(this, EventArgs.Empty);
    }

    private void QuizPreviewWindow_AiFeatureGateChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() => ConfigureAiGeneratedIndicator(_modelDisplayName));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void UpdateSummary(bool animateScore = false)
    {
        if (_bulkQuestionEdit) return;
        var questionCount = _questions.Count;
        var answeredCount = _questions.Count(question => question.HasSelection);
        QuestionCountTextBlock.Text = string.Format(LocalizationService.GetString("QuizQuestionCountFormat"), questionCount);
        AnsweredCountTextBlock.Text = string.Format(LocalizationService.GetString("QuizAnsweredCountFormat"), answeredCount, questionCount);

        CheckAnswersButton.IsEnabled = !_isSubmitted && questionCount > 0;
        ResetAnswersButton.IsEnabled = _isSubmitted || answeredCount > 0;
        if (ShuffleQuestionsButton is not null)
            ShuffleQuestionsButton.IsEnabled = !BulkSelection.IsActive && questionCount >= 2;

        if (!_isSubmitted)
        {
            if (ScoreBadge.Visibility == Visibility.Visible && !animateScore)
            {
                UiMotion.AnimateLayoutReveal(ScoreBadge, false, axis: Orientation.Horizontal, animate: false);
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
            UiMotion.AnimateLayoutReveal(ScoreBadge, true, axis: Orientation.Horizontal);
        else
            UiMotion.AnimateLayoutReveal(ScoreBadge, true, axis: Orientation.Horizontal, animate: false);
    }

    private void SetQuestionFocus(int index)
    {
        if (index < 0 || index >= _questions.Count) return;

        for (int i = 0; i < _questions.Count; i++)
        {
            _questions[i].IsFocused = (i == index);
        }

        var container = QuestionsItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        if (ContentRoot.IsLoaded && container is null
            && FindVisualChild<VirtualizingStackPanel>(QuestionsItemsControl) is { } panel)
        {
            panel.BringIndexIntoViewPublic(index);
            QuestionsItemsControl.UpdateLayout();
            container = QuestionsItemsControl.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
        }

        container?.BringIntoView();
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            if (FindVisualChild<T>(child) is { } descendant)
                return descendant;
        }

        return null;
    }

    private int GetFocusedQuestionIndex()
    {
        for (int i = 0; i < _questions.Count; i++)
        {
            if (_questions[i].IsFocused) return i;
        }
        return -1;
    }

    internal sealed class QuizPreviewQuestion : INotifyPropertyChanged
    {
        public Guid Id => _source.Id;
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
                    // Carried through untouched from the source - the editor UI has no field for
                    // either, they're just identity/progress data that must survive a save.
                    Id = _source.Id,
                    LastAttemptCorrect = _source.LastAttemptCorrect,
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
                Id = _source.Id,
                LastAttemptCorrect = _source.LastAttemptCorrect,
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

    internal sealed class QuizPreviewOption : INotifyPropertyChanged
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

        public string MarkerLetter => QuizOptionMarker.LetterFromIndex(Parent.Options.IndexOf(this));

        public bool ShowSubmitCorrectIcon => Parent.IsSubmitted && IsCorrect;

        public bool ShowSubmitIncorrectIcon => Parent.IsSubmitted && IsSelected && !IsCorrect;

        public bool ShowMarkerLetter => !ShowSubmitCorrectIcon && !ShowSubmitIncorrectIcon;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void RefreshState()
        {
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(IsCorrectAfterSubmit));
            OnPropertyChanged(nameof(IsIncorrectSelectedAfterSubmit));
            OnPropertyChanged(nameof(MarkerLetter));
            OnPropertyChanged(nameof(ShowSubmitCorrectIcon));
            OnPropertyChanged(nameof(ShowSubmitIncorrectIcon));
            OnPropertyChanged(nameof(ShowMarkerLetter));
        }

        public void SetCorrectSilently(bool value)
        {
            if (_isCorrect == value)
                return;

            _isCorrect = value;
            OnPropertyChanged(nameof(IsCorrect));
            RefreshState();
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

}

