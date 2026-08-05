using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Localization;
using Scriblucent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;

namespace Scriblucent.Views;

public partial class FlashcardsPreviewWindow : Window, INotifyPropertyChanged
{
    private const double StudyProgressBarFallbackWidth = 316;

    private double GetStudyProgressTrackWidth()
    {
        if (StudyModeProgressTrack is null)
            return StudyProgressBarFallbackWidth;

        var width = StudyModeProgressTrack.ActualWidth;
        return width > 1 ? width : StudyProgressBarFallbackWidth;
    }

    private enum UnsavedCloseDecision
    {
        Cancel,
        LeaveWithoutSaving,
        SaveAndClose
    }

    private enum StudyStartDecision
    {
        Cancel,
        Continue,
        Restart
    }

    private const int DefaultSetIndex = 1;
    private readonly List<FlashcardPreviewItem> _allItems;
    private readonly ObservableCollection<FlashcardPreviewItem> _items;
    private readonly ObservableCollection<FlashcardSetOption> _setOptions;
    private readonly ObservableCollection<FlashcardStatusFilterOption> _statusFilterOptions;
    private readonly ObservableCollection<FlashcardCategoryFilterOption> _categoryFilterOptions;
    private readonly ObservableCollection<FlashcardNoteLinkOption> _noteLinkOptions = new();
    private readonly List<FlashcardPreviewItem> _studyHistory = new();
    private readonly Random _random = new();
    private Action<Guid>? _openNoteAction;
    private bool _isStudyMode;
    private bool _isStudyModeCardAnimating;
    private bool _studyCompletionDialogShown;
    private bool _studyCelebrationShown;
    private int _studyModeIndex;
    private int _studyHistoryPosition = -1;
    private int _currentSetIndex = DefaultSetIndex;
    private FlashcardPreviewItem? _selectedItem;
    private CardMaturity? _statusFilterMaturity;
    private string _categoryFilter = string.Empty;
    private string _searchText = string.Empty;
    private bool _starredOnly;
    private string _modelDisplayName = string.Empty;
    private string _lastSavedSnapshot = string.Empty;
    private DispatcherTimer? _editedIndicatorDebounceTimer;
    private DispatcherTimer? _searchFilterDebounceTimer;
    private bool _isInitializing = true;
    private bool _isFlashingTitleStatus;
    private bool _studyModeLayoutVisible;
    private bool _allowCloseWithoutPrompt;
    private bool _isApplyingFlipSpeed;
    private bool _isOptionsPanelOpen;
    private int _optionsPanelAnimationVersion;
    private FlashcardStudySession? _pendingStudySession;
    private FlashcardNoteLinkOption? _selectedLinkedNote;
    private bool _dueOnly = true;
    private readonly Dictionary<FlashcardReviewGrade, int> _sessionGradeCounts = new();
    private int _sessionCardsReviewed;
    private DateTime _sessionStartedAt;

    /// <summary>Cards passed *during the current active session* - deliberately separate from the
    /// persistent IsKnown flag, which only reflects a card's most recent grade ever, not whether
    /// it's been reviewed today. A card that graduated weeks ago and is now due again for its
    /// scheduled check-in still carries IsKnown=true from that old grade; using IsKnown here would
    /// make ApplyStudyModeState/TryFindNextStudyIndex treat it as already done before it was ever
    /// shown this session, reporting "session complete" immediately. Cleared on every fresh Start
    /// Study click, same lifecycle as _sessionGradeCounts/_sessionCardsReviewed.</summary>
    private readonly HashSet<Guid> _sessionKnownCardIds = new();
    private bool _isCramMode;
    private const int CramStepsToMaster = 2;
    private readonly Dictionary<Guid, int> _cramStepsRemaining = new();
    private int _cramLapNumber = 1;
    private int _cramCardsReviewed;
    private AudioClipPlayer? _audioPlayer;
    private FlashcardPreviewItem? _activeAudioFlashcard;
    private bool _activeAudioIsQuestion;

    public sealed class FlashcardNoteLinkOption
    {
        public FlashcardNoteLinkOption(Guid id, string title)
        {
            Id = id;
            Title = string.IsNullOrWhiteSpace(title) ? LocalizationService.GetString("QuizLinkedNoteNone") : title.Trim();
        }

        public Guid Id { get; }
        public string Title { get; }
    }

    public FlashcardsPreviewWindow(
        IEnumerable<FlashcardItem> items,
        IEnumerable<FlashcardNoteLinkOption>? noteOptions = null,
        string? modelDisplayName = null,
        string? title = null,
        IEnumerable<string>? tags = null,
        IReadOnlyDictionary<int, string>? setNames = null,
        FlashcardStudySession? studySession = null,
        Guid? sourceNoteId = null,
        Action<Guid>? openNoteAction = null,
        int flipDelayMilliseconds = 0,
        bool studyDueCardsOnly = true)
    {
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        InitializeFlipSpeedSlider(flipDelayMilliseconds);
        _openNoteAction = openNoteAction;
        _dueOnly = studyDueCardsOnly;
        DueOnlyFilterCheckBox.IsChecked = _dueOnly;
        if (noteOptions != null)
        {
            foreach (var option in noteOptions)
                _noteLinkOptions.Add(option);
        }
        _selectedLinkedNote = _noteLinkOptions.FirstOrDefault(option => option.Id == sourceNoteId);
        _allItems = items
            .Select(i => new FlashcardPreviewItem(
                i.Id,
                i.Question,
                i.Answer,
                Math.Max(DefaultSetIndex, i.SetIndex),
                i.Category,
                i.IsKnown,
                i.IsUnknown,
                i.Hint,
                i.EaseFactor,
                i.IntervalDays,
                i.Repetitions,
                i.DueDate,
                i.LastReviewedAt,
                i.Stability,
                i.Difficulty,
                i.QuestionImageFileName,
                i.QuestionAudioFileName,
                i.AnswerImageFileName,
                i.AnswerAudioFileName,
                i.IsStarred))
            .ToList();
        _items = new ObservableCollection<FlashcardPreviewItem>();
        _setOptions = new ObservableCollection<FlashcardSetOption>();
        _statusFilterOptions = new ObservableCollection<FlashcardStatusFilterOption>();
        _categoryFilterOptions = new ObservableCollection<FlashcardCategoryFilterOption>();

        SetSelectorComboBox.ItemsSource = _setOptions;
        StatusFilterComboBox.ItemsSource = _statusFilterOptions;
        CategoryFilterComboBox.ItemsSource = _categoryFilterOptions;
        FlashcardsItemsControl.ItemsSource = _items;
        TitleTextBox.Text = string.IsNullOrWhiteSpace(title)
            ? LocalizationService.GetString("FlashcardsEditorTitle")
            : title.Trim();
        TagsTextBox.Text = tags is null
            ? string.Empty
            : string.Join(", ", tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()));
        ConfigureAiGeneratedIndicator(modelDisplayName);
        OnPropertyChanged(nameof(NoteLinkOptions));
        OnPropertyChanged(nameof(LinkedNoteButtonText));
        OnPropertyChanged(nameof(LinkedNoteButtonToolTip));

        _pendingStudySession = studySession;
        UpdateShuffleButtonState();
        InitializeStatusFilterOptions();
        InitializeSetOptionsFromItems(setNames);
        if (studySession is not null)
        {
            var targetSetIndex = Math.Max(DefaultSetIndex, studySession.CurrentSetIndex);
            var targetSet = _setOptions.FirstOrDefault(option => option.SetIndex == targetSetIndex);
            if (targetSet is not null)
                SetSelectorComboBox.SelectedItem = targetSet;
        }
        ApplyStudyModeState();
        _studyModeLayoutVisible = _isStudyMode && _items.Count > 0;
        _isInitializing = false;
        MarkCurrentStateSaved();
        PreviewKeyDown += FlashcardsPreviewWindow_PreviewKeyDown;
        Closed += (_, _) => _audioPlayer?.Dispose();
        WindowContentEnterHelper.AnimateOnLoaded(this);
    }

    public string EditorTitle => TitleTextBox.Text.Trim();

    public IReadOnlyList<string> Tags => ParseTags(TagsTextBox.Text);

    public string AiModelDisplayName => _modelDisplayName;

    public ObservableCollection<FlashcardNoteLinkOption> NoteLinkOptions => _noteLinkOptions;

    public string LinkedNoteButtonText => _selectedLinkedNote?.Title ?? LocalizationService.GetString("QuizLinkedNoteNone");

    public string LinkedNoteButtonToolTip => _selectedLinkedNote is null
        ? LocalizationService.GetString("QuizLinkedNoteNoneTooltip")
        : string.Format(LocalizationService.GetString("QuizLinkedNoteOpenTooltip"), _selectedLinkedNote.Title);

    public FlashcardNoteLinkOption? SelectedLinkedNote
    {
        get => _selectedLinkedNote;
        set
        {
            if (ReferenceEquals(_selectedLinkedNote, value))
                return;

            _selectedLinkedNote = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkedNoteButtonText));
            OnPropertyChanged(nameof(LinkedNoteButtonToolTip));
            UpdateEditedIndicator();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FlashcardSetDocument ToDocument(FlashcardSetDocument? existingDocument = null)
    {
        return new FlashcardSetDocument
        {
            Id = existingDocument?.Id ?? Guid.NewGuid(),
            Title = string.IsNullOrWhiteSpace(EditorTitle)
                ? LocalizationService.GetString("FlashcardSetUntitled")
                : EditorTitle,
            Tags = Tags.ToList(),
            SetNames = _setOptions
                .Where(option => !string.IsNullOrWhiteSpace(option.DisplayName))
                .ToDictionary(option => option.SetIndex, option => option.DisplayName.Trim()),
            Cards = GetFlashcardItems().ToList(),
            StudySession = BuildStudySession(),
            CreatedAt = existingDocument?.CreatedAt ?? DateTime.UtcNow,
            LastModified = DateTime.Now,
            AiModelDisplayName = string.IsNullOrWhiteSpace(_modelDisplayName)
                ? existingDocument?.AiModelDisplayName ?? string.Empty
                : _modelDisplayName,
            SourceNoteId = _selectedLinkedNote?.Id ?? existingDocument?.SourceNoteId,
            GroupId = existingDocument?.GroupId,
            Schedules = existingDocument?.Schedules?.ToList() ?? new List<NoteScheduleEntry>(),
            IsPinned = existingDocument?.IsPinned ?? false,
            FlipDelayMilliseconds = GetFlipAnimationDurationMs(),
            StudyDueCardsOnly = _dueOnly
        };
    }

    public IReadOnlyList<FlashcardItem> GetFlashcardItems()
    {
        return _allItems
            .Select(item => new FlashcardItem
            {
                Id = item.Id,
                Question = item.Question,
                Answer = item.Answer,
                Category = item.Category,
                Hint = item.Hint,
                SetIndex = Math.Max(DefaultSetIndex, item.SetIndex),
                IsKnown = item.IsKnown,
                IsUnknown = item.IsUnknown,
                IsStarred = item.IsStarred,
                EaseFactor = item.EaseFactor,
                IntervalDays = item.IntervalDays,
                Repetitions = item.Repetitions,
                DueDate = item.DueDate,
                LastReviewedAt = item.LastReviewedAt,
                Stability = item.Stability,
                Difficulty = item.Difficulty,
                QuestionImageFileName = item.QuestionImageFileName,
                QuestionAudioFileName = item.QuestionAudioFileName,
                AnswerImageFileName = item.AnswerImageFileName,
                AnswerAudioFileName = item.AnswerAudioFileName
            })
            .ToList();
    }

    private FlashcardStudySession BuildStudySession()
    {
        // Cram sessions are throwaway by design - never persist one, even if the editor is closed
        // mid-session, so reopening the set never offers to "resume" a cram lap.
        if (_isCramMode)
            return new FlashcardStudySession();

        return new FlashcardStudySession
        {
            IsStudyMode = _isStudyMode,
            CurrentSetIndex = _currentSetIndex,
            CurrentCardId = GetCurrentStudyCardId(),
            History = _studyHistory.Select(item => item.Id).ToList(),
            HistoryPosition = _studyHistoryPosition
        };
    }

    private Guid? GetCurrentStudyCardId()
    {
        if (!_isStudyMode || _items.Count == 0)
            return null;

        if (_studyModeIndex < 0 || _studyModeIndex >= _items.Count)
            return null;

        return _items[_studyModeIndex].Id;
    }

    private void RestoreStudySession(FlashcardStudySession session)
    {
        _isStudyMode = session.IsStudyMode;
        _currentSetIndex = Math.Max(DefaultSetIndex, session.CurrentSetIndex);

        _studyHistory.Clear();
        _studyHistoryPosition = -1;

        var itemsById = _items.ToDictionary(item => item.Id, item => item);
        foreach (var id in session.History)
        {
            if (itemsById.TryGetValue(id, out var item))
                _studyHistory.Add(item);
        }

        if (_studyHistory.Count > 0)
        {
            _studyHistoryPosition = Math.Clamp(session.HistoryPosition, -1, _studyHistory.Count - 1);
        }

        if (session.CurrentCardId.HasValue && itemsById.TryGetValue(session.CurrentCardId.Value, out var currentItem))
        {
            _studyModeIndex = _items.IndexOf(currentItem);
        }
        else if (_studyHistoryPosition >= 0 && _studyHistoryPosition < _studyHistory.Count)
        {
            _studyModeIndex = _items.IndexOf(_studyHistory[_studyHistoryPosition]);
        }
        else
        {
            _studyModeIndex = Math.Max(0, _items.ToList().FindIndex(item => !item.IsKnown));
        }
    }

    private void EditorField_TextChanged(object sender, TextChangedEventArgs e)
    {
        // GetEditorSnapshot() re-joins every card's Question/Answer/Category even though only
        // Title/Tags changed here, so debounce instead of paying that cost on every keystroke.
        _editedIndicatorDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _editedIndicatorDebounceTimer.Stop();
        _editedIndicatorDebounceTimer.Tick -= EditedIndicatorDebounceTimer_Tick;
        _editedIndicatorDebounceTimer.Tick += EditedIndicatorDebounceTimer_Tick;
        _editedIndicatorDebounceTimer.Start();
    }

    private void EditedIndicatorDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _editedIndicatorDebounceTimer?.Stop();
        UpdateEditedIndicator();
    }

    private void MarkCurrentStateSaved()
    {
        _lastSavedSnapshot = GetEditorSnapshot();
        UpdateEditedIndicator();
    }

    public bool HasPendingAutoSaveChanges() => HasUnsavedChanges();

    public void MarkCurrentStateAutoSaved()
    {
        _lastSavedSnapshot = GetEditorSnapshot();
        ShowAutoSaveIndicator();
    }

    private void ShowAutoSaveIndicator()
    {
        if (EditedIndicatorText is null)
            return;

        _isFlashingTitleStatus = true;
        UiMotion.FlashInlineStatus(
            EditedIndicatorText,
            LocalizationService.GetString("AutoSaving"),
            () =>
            {
                _isFlashingTitleStatus = false;
                UpdateEditedIndicator();
            });
    }

    private bool HasUnsavedChanges()
    {
        if (_isInitializing)
            return false;

        return !string.Equals(GetEditorSnapshot(), _lastSavedSnapshot, StringComparison.Ordinal);
    }

    private void UpdateEditedIndicator()
    {
        if (_isInitializing || EditedIndicatorText is null || _isFlashingTitleStatus)
            return;

        EditedIndicatorText.Text = LocalizationService.GetString("EditedIndicator");
        UiMotion.AnimateInlineReveal(EditedIndicatorText, HasUnsavedChanges());
    }

    private string GetEditorSnapshot()
    {
        var setSnapshot = string.Join(
            '\u001E',
            _setOptions
                .OrderBy(option => option.SetIndex)
                .Select(option => $"{option.SetIndex}\u001D{option.DisplayName.Trim()}"));

        var cardSnapshot = string.Join(
            '\u001E',
            _allItems.Select(item =>
                $"{item.Id}\u001D{item.SetIndex}\u001D{item.Question}\u001D{item.Answer}\u001D{item.Category}\u001D{item.IsKnown}\u001D{item.IsUnknown}\u001D{item.IsStarred}"));

        // Study-mode position (which set tab, which card, nav history) is deliberately
        // excluded from the dirty check - browsing/navigating (Next/Previous, switching set
        // tabs) is not an edit, so it must not flip the Edited indicator or trigger
        // autosave/close-confirm on its own. Grading still counts as a real change because it
        // updates the graded card's IsKnown/IsUnknown above. BuildStudySession() still
        // captures position unconditionally, so it's included whenever a save happens for an
        // actual reason.
        return string.Join(
            '\u001F',
            TitleTextBox.Text,
            _selectedLinkedNote?.Id.ToString() ?? string.Empty,
            string.Join('\u001E', Tags),
            setSnapshot,
            cardSnapshot,
            GetFlipAnimationDurationMs().ToString());
    }

    private void LinkedNoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedLinkedNote is null)
            return;

        _openNoteAction?.Invoke(_selectedLinkedNote.Id);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

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
            ? LocalizationService.GetString("FlashcardSetUntitled")
            : title;
    }

    private void SaveAndClose()
    {
        MarkCurrentStateSaved();
        _allowCloseWithoutPrompt = true;
        WindowContentEnterHelper.CloseWithDialogResult(this, true);
    }

    private void ConfigureAiGeneratedIndicator(string? modelDisplayName)
    {
        _modelDisplayName = modelDisplayName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(modelDisplayName))
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
            LocalizationService.GetString("FlashcardsGeneratedWithModel"),
            modelDisplayName.Trim());

        if (_isInitializing)
        {
            AiGeneratedInfoBadge.Visibility = Visibility.Visible;
            return;
        }

        UiMotion.AnimateInlineReveal(AiGeneratedInfoBadge, true);
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

    private IReadOnlyList<string> GetCategoryOptions()
    {
        return _allItems
            .Select(item => item.Category.Trim())
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void InitializeStatusFilterOptions()
    {
        _statusFilterOptions.Clear();
        _statusFilterOptions.Add(new FlashcardStatusFilterOption
        {
            Maturity = null,
            DisplayName = LocalizationService.GetString("FlashcardStatusAll")
        });
        _statusFilterOptions.Add(new FlashcardStatusFilterOption
        {
            Maturity = CardMaturity.New,
            DisplayName = LocalizationService.GetString("FlashcardStatusNew")
        });
        _statusFilterOptions.Add(new FlashcardStatusFilterOption
        {
            Maturity = CardMaturity.Learning,
            DisplayName = LocalizationService.GetString("FlashcardStatusLearning")
        });
        _statusFilterOptions.Add(new FlashcardStatusFilterOption
        {
            Maturity = CardMaturity.Mature,
            DisplayName = LocalizationService.GetString("FlashcardStatusMature")
        });

        StatusFilterComboBox.SelectedIndex = 0;
    }

    private void InitializeCategoryFilterOptions(bool preserveSelection = true)
    {
        var selectedCategory = preserveSelection
            ? _categoryFilter
            : string.Empty;

        _categoryFilterOptions.Clear();
        _categoryFilterOptions.Add(new FlashcardCategoryFilterOption
        {
            Category = string.Empty,
            DisplayName = LocalizationService.GetString("FlashcardCategoryAll")
        });

        var categories = _allItems
            .Where(item => item.SetIndex == _currentSetIndex)
            .Select(item => item.Category.Trim())
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var category in categories)
        {
            _categoryFilterOptions.Add(new FlashcardCategoryFilterOption
            {
                Category = category,
                DisplayName = category
            });
        }

        var matchedOption = _categoryFilterOptions.FirstOrDefault(option =>
            string.Equals(option.Category, selectedCategory, StringComparison.OrdinalIgnoreCase));

        CategoryFilterComboBox.SelectedItem = matchedOption ?? _categoryFilterOptions.First();
    }

    private void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatusFilterComboBox.SelectedItem is not FlashcardStatusFilterOption option)
            return;

        _statusFilterMaturity = option.Maturity;
        ApplyFilters();
    }

    private void FlashcardsPreviewWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreenMode();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && IsStudyFullscreen)
        {
            ToggleFullscreenMode();
            e.Handled = true;
            return;
        }

        if (!_isStudyMode || _items.Count == 0)
            return;

        if (e.OriginalSource is TextBox)
            return;

        if (e.Key == Key.Right || e.Key == Key.N)
        {
            MoveToNextStudyCardWithAnimation();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left)
        {
            MoveToPreviousStudyCardWithAnimation();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space || e.Key == Key.F)
        {
            FlipCurrentStudyCard();
            e.Handled = true;
            return;
        }

        // Anki-style grading shortcuts - only meaningful once the answer is actually revealed.
        if (StudyModeFlipHost.IsFlipped)
        {
            var grade = e.Key switch
            {
                Key.D1 or Key.NumPad1 => (FlashcardReviewGrade?)FlashcardReviewGrade.Again,
                Key.D2 or Key.NumPad2 => FlashcardReviewGrade.Hard,
                Key.D3 or Key.NumPad3 => FlashcardReviewGrade.Good,
                Key.D4 or Key.NumPad4 => FlashcardReviewGrade.Easy,
                _ => null
            };

            if (grade.HasValue)
            {
                GradeCurrentStudyCard(grade.Value);
                e.Handled = true;
            }
        }
    }

    private void FlipCurrentStudyCard()
    {
        // Cram mode tracks its own completion per-card via _cramStepsRemaining and deliberately
        // never touches IsKnown (see the grading comment near CramStepsToMaster) - checking
        // IsKnown here too would block flipping outright whenever every card in the cram set
        // already happens to be known from real study history, which is exactly the situation
        // that leads someone to cram in the first place. Mirrors the same branch already used by
        // TryFindNextStudyIndex and the study progress-bar calculation.
        var allCardsDoneForThisPass = _isCramMode
            ? _items.All(item => _cramStepsRemaining.GetValueOrDefault(item.Id, CramStepsToMaster) <= 0)
            : _items.All(item => _sessionKnownCardIds.Contains(item.Id));

        if (!_isStudyMode
            || _isStudyModeCardAnimating
            || StudyModeFlipHost.IsAnimating
            || _items.Count == 0
            || allCardsDoneForThisPass
            || _studyModeIndex < 0
            || _studyModeIndex >= _items.Count
            || StudyModeCard.Visibility != Visibility.Visible
            || !StudyModeCard.IsHitTestVisible)
        {
            return;
        }

        AnimateFlashcardFlip(StudyModeFlipHost);
    }

    private void InitializeSetOptionsFromItems(IReadOnlyDictionary<int, string>? setNames)
    {
        _setOptions.Clear();

        var setIndexes = _allItems
            .Select(item => item.SetIndex)
            .Concat(setNames?.Keys ?? Enumerable.Empty<int>())
            .Where(index => index >= DefaultSetIndex)
            .Distinct()
            .OrderBy(index => index)
            .DefaultIfEmpty(DefaultSetIndex)
            .ToList();

        foreach (var setIndex in setIndexes)
        {
            string? displayName = null;
            setNames?.TryGetValue(setIndex, out displayName);
            AddSetOption(setIndex, displayName, selectSet: false);
        }

        SetSelectorComboBox.SelectedItem = _setOptions
            .OrderBy(option => option.SetIndex)
            .FirstOrDefault();
    }

    private void SetSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SetSelectorComboBox.SelectedItem is not FlashcardSetOption set)
            return;

        ApplySetFilter(set.SetIndex);
    }

    private void ApplySetFilter(int setIndex)
    {
        _currentSetIndex = Math.Max(DefaultSetIndex, setIndex);
        InitializeCategoryFilterOptions();
        ApplyFilters();
    }

    /// <summary>Category/status/search filters only - shared by ApplyFilters (which layers the
    /// due-only filter on top) and UpdateEmptyGridState (which needs to check whether due-only is
    /// specifically what's hiding everything, as opposed to these filters).</summary>
    private IEnumerable<FlashcardPreviewItem> GetItemsMatchingNonDueFilters()
    {
        var normalizedQuery = (_searchText ?? string.Empty).Trim();

        var filteredItems = _allItems.Where(item => item.SetIndex == _currentSetIndex);
        if (_statusFilterMaturity.HasValue)
        {
            var maturity = _statusFilterMaturity.Value;
            filteredItems = filteredItems.Where(item => item.Maturity == maturity);
        }

        if (!string.IsNullOrWhiteSpace(_categoryFilter))
        {
            filteredItems = filteredItems.Where(item =>
                string.Equals(item.Category, _categoryFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (_starredOnly)
            filteredItems = filteredItems.Where(item => item.IsStarred);

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            filteredItems = filteredItems.Where(item =>
                item.Question.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || item.Answer.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || item.Category.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase));
        }

        return filteredItems;
    }

    /// <summary>Rebuilds _items from the current filters (search/category/status/due-only). Pass
    /// preserveStudyPosition: true when refreshing after grading rather than after an actual
    /// filter/set change - e.g. exiting study mode should drop cards that are no longer due
    /// without also resetting _studyModeIndex/history, which would break resuming later.</summary>
    private void ApplyFilters(bool preserveStudyPosition = false)
    {
        var filteredItems = GetItemsMatchingNonDueFilters();

        List<FlashcardPreviewItem> orderedItems;
        if (_dueOnly && !_isCramMode)
        {
            var now = DateTime.Now;
            orderedItems = filteredItems
                .Where(item => FlashcardReviewScheduler.IsDue(item.DueDate, now))
                .OrderBy(item => item.DueDate ?? DateTime.MinValue)
                .ToList();
        }
        else
        {
            orderedItems = filteredItems.ToList();
        }

        _items.Clear();
        foreach (var item in orderedItems)
            _items.Add(item);

        UpdateQuestionAnswerToggleButton();
        UpdateEmptyGridState();

        if (_pendingStudySession is not null)
        {
            RestoreStudySession(_pendingStudySession);
            _pendingStudySession = null;
        }
        else if (!preserveStudyPosition)
        {
            _studyModeIndex = 0;
            ResetStudyHistory();
        }

        ApplyStudyModeState();
        UpdateShuffleButtonState();
    }

    /// <summary>The grid view has no built-in "nothing to show" placeholder - when _items is
    /// empty it just renders as blank space, which reads as broken rather than "you're done" or
    /// "nothing created yet". Distinguishes three real cases: the set has literally no cards, the
    /// due-only filter is what's hiding everything (in which case say when the next one arrives,
    /// mirroring FlashcardSetViewModel.DueStatusText's dashboard-card equivalent), or some other
    /// filter/search combination excludes everything.</summary>
    private void UpdateEmptyGridState()
    {
        if (_items.Count > 0)
        {
            FlashcardsGridEmptyStateText.Visibility = Visibility.Collapsed;
            return;
        }

        var hasAnyCardsInSet = _allItems.Any(item => item.SetIndex == _currentSetIndex);
        string message;

        if (!hasAnyCardsInSet)
        {
            message = LocalizationService.GetString("FlashcardGridEmptyBeginCreating");
        }
        else if (_dueOnly && !_isCramMode)
        {
            var upcomingDueDates = GetItemsMatchingNonDueFilters()
                .Where(item => item.DueDate.HasValue)
                .Select(item => item.DueDate!.Value)
                .ToList();

            message = upcomingDueDates.Count > 0
                ? BuildNextReviewMessage(upcomingDueDates.Min())
                : LocalizationService.GetString("FlashcardGridEmptyNoMatches");
        }
        else
        {
            message = LocalizationService.GetString("FlashcardGridEmptyNoMatches");
        }

        FlashcardsGridEmptyStateText.Text = message;
        FlashcardsGridEmptyStateText.Visibility = Visibility.Visible;
    }

    private static string BuildNextReviewMessage(DateTime nextDueDate)
    {
        var days = (nextDueDate.Date - DateTime.Now.Date).Days;
        return days <= 1
            ? LocalizationService.GetString("FlashcardGridEmptyNextReviewTomorrow")
            : string.Format(LocalizationService.GetString("FlashcardGridEmptyNextReviewInDaysFormat"), days);
    }

    private void ResetStudyHistory()
    {
        _studyHistory.Clear();
        _studyHistoryPosition = -1;
    }

    private bool AreAllStudyItemsKnown()
    {
        if (_items.Count == 0)
            return false;

        return _items.All(item => item.IsKnown);
    }

    private bool HasStudyProgress()
    {
        return _items.Any(item => item.IsKnown || item.IsUnknown);
    }

    private void ResetStudyStatusesForCurrentSet()
    {
        var setIndex = _currentSetIndex;
        var hasChanges = false;

        foreach (var item in _allItems.Where(item => item.SetIndex == setIndex))
        {
            if (!item.IsKnown && !item.IsUnknown && item.DueDate is null && item.Repetitions == 0)
                continue;

            ResetItemReviewState(item);
            hasChanges = true;
        }

        if (hasChanges)
        {
            ApplyFilters();
            UpdateEditedIndicator();
        }
    }

    private void ResetStudyStatusesForAllSets()
    {
        foreach (var item in _allItems)
            ResetItemReviewState(item);

        ResetStudyHistory();
        ApplyFilters();
        UpdateEditedIndicator();
    }

    /// <summary>Resets the legacy known/unknown flags and both engines' scheduling state to
    /// "new" - a real restart, not just clearing the display flags while leaving stale due
    /// dates/intervals (or FSRS stability/difficulty) behind.</summary>
    private static void ResetItemReviewState(FlashcardPreviewItem item)
    {
        item.IsKnown = false;
        item.IsUnknown = false;
        item.EaseFactor = 2.5;
        item.IntervalDays = 0;
        item.Repetitions = 0;
        item.DueDate = null;
        item.LastReviewedAt = null;
        item.Stability = 0;
        item.Difficulty = 0;
    }

    private void FlashcardHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new FlashcardStudyHistoryWindow(() => ToDocument(), ResetStudyStatusesForAllSets)
        {
            Owner = this
        };

        window.ShowDialog();
        UpdateEditedIndicator();
    }

    private void FlashcardStatisticsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new FlashcardStatisticsWindow(() => ToDocument())
        {
            Owner = this
        };

        window.ShowDialog();
    }

    // CSV/TXT only - both are just card lists, unlike .scribflash which carries a whole document's
    // metadata and media. To bring in a .scribflash as a whole, use the dashboard's Import button
    // (creates a new set) and "Combine flashcards" to fold it into this one afterward.
    private void ImportCardsButton_Click(object sender, RoutedEventArgs e)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = LocalizationService.GetString("FlashcardImportCardsDialogFilter"),
            Title = LocalizationService.GetString("FlashcardImportCardsDialogTitle")
        };

        if (openFileDialog.ShowDialog(this) != true)
            return;

        try
        {
            var extension = System.IO.Path.GetExtension(openFileDialog.FileName).ToLowerInvariant();
            var delimiter = extension == ".txt" ? '\t' : ',';
            var text = System.IO.File.ReadAllText(openFileDialog.FileName, System.Text.Encoding.UTF8);
            var result = FlashcardDelimitedFormat.ParseWithWarnings(text, delimiter, GetExportFileName());

            if (result.Document.Cards.Count == 0)
            {
                ModernMessageBox.Show(
                    LocalizationService.GetString("FlashcardImportNoCardsFound"),
                    LocalizationService.GetString("FlashcardImportCards"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var targetSetIndex = GetSelectedSetIndex() ?? _currentSetIndex;
            foreach (var card in result.Document.Cards)
            {
                _allItems.Add(new FlashcardPreviewItem(
                    Guid.NewGuid(),
                    card.Question,
                    card.Answer,
                    targetSetIndex,
                    card.Category,
                    isKnown: false,
                    isUnknown: false,
                    hint: card.Hint));
            }

            ApplySetFilter(targetSetIndex);
            UpdateEditedIndicator();

            var summary = string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("FlashcardImportCardsAddedFormat"), result.Document.Cards.Count);
            if (result.Warnings.Count > 0)
                summary += "\n\n" + string.Join("\n", result.Warnings);

            ModernMessageBox.Show(
                summary,
                LocalizationService.GetString("FlashcardImportCards"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ModernMessageBox.Show(
                string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("FlashcardImportCardsFailedFormat"), ex.Message),
                LocalizationService.GetString("ImportError"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_allItems.Count == 0)
            return;

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationService.GetString("FlashcardExportDialogTitle"),
            Filter = LocalizationService.GetString("FlashcardExportDialogFilter"),
            FileName = GetExportFileName(),
            OverwritePrompt = true
        };

        if (saveDialog.ShowDialog(this) != true)
            return;

        try
        {
            var extension = System.IO.Path.GetExtension(saveDialog.FileName).ToLowerInvariant();
            switch (extension)
            {
                case ".scribflash":
                    FlashcardFileService.SaveScribFlashFile(saveDialog.FileName, ToDocument());
                    break;
                case ".csv":
                    FlashcardDelimitedFormat.SaveCsvFile(saveDialog.FileName, ToDocument());
                    break;
                case ".txt":
                    FlashcardDelimitedFormat.SaveTxtFile(saveDialog.FileName, ToDocument());
                    break;
                default:
                    throw new InvalidOperationException(LocalizationService.GetString("FlashcardExportUnsupportedFormat"));
            }

            ModernMessageBox.Show(
                LocalizationService.GetString("FlashcardExportComplete"),
                LocalizationService.GetString("Success"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ModernMessageBox.Show(
                string.Format(CultureInfo.CurrentCulture, LocalizationService.GetString("FlashcardExportFailedFormat"), ex.Message),
                LocalizationService.GetString("ExportError"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private string GetExportFileName()
    {
        var baseName = string.IsNullOrWhiteSpace(EditorTitle)
            ? LocalizationService.GetString("FlashcardSetUntitled")
            : EditorTitle;

        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(invalid, '_');

        return baseName;
    }

    private void PlayQuestionAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveFlashcardFromElement(sender) is { } flashcard)
            TogglePlayPauseAudio(flashcard, isQuestion: true);
    }

    private void PlayAnswerAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveFlashcardFromElement(sender) is { } flashcard)
            TogglePlayPauseAudio(flashcard, isQuestion: false);
    }

    private void RestartQuestionAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveFlashcardFromElement(sender) is { } flashcard)
            RestartAudio(flashcard, isQuestion: true);
    }

    private void RestartAnswerAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveFlashcardFromElement(sender) is { } flashcard)
            RestartAudio(flashcard, isQuestion: false);
    }

    private void QuestionImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ResolveFlashcardFromElement(sender) is { } flashcard)
            ShowImageViewer(flashcard.QuestionImageFileName);
    }

    private void AnswerImage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ResolveFlashcardFromElement(sender) is { } flashcard)
            ShowImageViewer(flashcard.AnswerImageFileName);
    }

    private static FlashcardPreviewItem? ResolveFlashcardFromElement(object sender)
        => sender is FrameworkElement { DataContext: FlashcardPreviewItem flashcard } ? flashcard : null;

    private void TogglePlayPauseAudio(FlashcardPreviewItem flashcard, bool isQuestion)
    {
        var fileName = isQuestion ? flashcard.QuestionAudioFileName : flashcard.AnswerAudioFileName;
        if (!TryResolveExistingAudioPath(fileName, out var path))
            return;

        SetActiveAudio(flashcard, isQuestion);
        _audioPlayer ??= CreateAudioPlayer();
        _audioPlayer.Volume = ReadFlashcardAudioVolume();
        _audioPlayer.TogglePlayPause(path);
    }

    private void RestartAudio(FlashcardPreviewItem flashcard, bool isQuestion)
    {
        var fileName = isQuestion ? flashcard.QuestionAudioFileName : flashcard.AnswerAudioFileName;
        if (!TryResolveExistingAudioPath(fileName, out var path))
            return;

        SetActiveAudio(flashcard, isQuestion);
        _audioPlayer ??= CreateAudioPlayer();
        _audioPlayer.Volume = ReadFlashcardAudioVolume();
        _audioPlayer.Restart(path);
    }

    private static float ReadFlashcardAudioVolume()
        => AppSettingsService.Load().FlashcardAudioVolumePercent / 100f;

    private bool TryResolveExistingAudioPath(string? fileName, out string path)
    {
        path = FlashcardMediaStorageService.ResolvePath(fileName) ?? string.Empty;
        if (path.Length == 0)
            return false;

        if (!System.IO.File.Exists(path))
        {
            ShowAudioPlaybackError("File not found on disk.");
            return false;
        }

        return true;
    }

    /// <summary>Clears the previously-active card/side's playing flag before switching, since
    /// only one clip plays at a time - otherwise a stale "playing" icon would linger on whatever
    /// was active before.</summary>
    private void SetActiveAudio(FlashcardPreviewItem flashcard, bool isQuestion)
    {
        if (_activeAudioFlashcard is not null
            && (!ReferenceEquals(_activeAudioFlashcard, flashcard) || _activeAudioIsQuestion != isQuestion))
        {
            _activeAudioFlashcard.IsQuestionAudioPlaying = false;
            _activeAudioFlashcard.IsAnswerAudioPlaying = false;
        }

        _activeAudioFlashcard = flashcard;
        _activeAudioIsQuestion = isQuestion;
    }

    private void UpdateActiveAudioPlaybackFlags()
    {
        if (_activeAudioFlashcard is null)
            return;

        var isPlaying = _audioPlayer?.IsPlaying ?? false;

        if (_activeAudioIsQuestion)
        {
            _activeAudioFlashcard.IsQuestionAudioPlaying = isPlaying;
            _activeAudioFlashcard.IsAnswerAudioPlaying = false;
        }
        else
        {
            _activeAudioFlashcard.IsAnswerAudioPlaying = isPlaying;
            _activeAudioFlashcard.IsQuestionAudioPlaying = false;
        }
    }

    private AudioClipPlayer CreateAudioPlayer()
    {
        var player = new AudioClipPlayer();
        player.PlaybackFailed += (_, ex) =>
        {
            CrashLog.Append("FlashcardAudioPlayback", ex);
            ShowAudioPlaybackError(ex.Message);
        };
        player.PlaybackStateChanged += (_, _) => UpdateActiveAudioPlaybackFlags();
        return player;
    }

    private void ShowAudioPlaybackError(string reason)
    {
        ModernMessageBox.Show(
            string.Format(LocalizationService.GetString("FlashcardAudioPlaybackError"), reason),
            LocalizationService.GetString("FlashcardPlayAudio"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    /// <summary>Cards render images small and downscaled (see FlashcardPreviewItem's cached,
    /// low-res decode), so this loads the same file fresh at full stored resolution - only when
    /// actually zooming, not for every card on screen.</summary>
    private void ShowImageViewer(string? fileName)
    {
        var fullResolutionImage = FlashcardMediaStorageService.LoadImage(fileName, decodePixelWidth: 0);
        if (fullResolutionImage is null)
            return;

        var viewer = new FlashcardImageViewerWindow(fullResolutionImage)
        {
            Owner = this
        };
        viewer.ShowDialog();
    }

    private void MoveToStudyItem(FlashcardPreviewItem item, bool appendToHistory)
    {
        var index = _items.IndexOf(item);
        if (index < 0)
            return;

        _audioPlayer?.Stop();
        _studyModeIndex = index;

        if (!appendToHistory)
            return;

        if (_studyHistoryPosition < _studyHistory.Count - 1)
            _studyHistory.RemoveRange(_studyHistoryPosition + 1, _studyHistory.Count - _studyHistoryPosition - 1);

        if (_studyHistory.Count == 0 || !ReferenceEquals(_studyHistory[^1], item))
            _studyHistory.Add(item);

        _studyHistoryPosition = _studyHistory.Count - 1;
    }

    private void EnsureCurrentStudyItemInHistory(FlashcardPreviewItem item)
    {
        if (_studyHistoryPosition >= 0
            && _studyHistoryPosition < _studyHistory.Count
            && ReferenceEquals(_studyHistory[_studyHistoryPosition], item))
        {
            return;
        }

        if (_studyHistoryPosition < _studyHistory.Count - 1)
            _studyHistory.RemoveRange(_studyHistoryPosition + 1, _studyHistory.Count - _studyHistoryPosition - 1);

        if (_studyHistory.Count == 0 || !ReferenceEquals(_studyHistory[^1], item))
            _studyHistory.Add(item);

        _studyHistoryPosition = _studyHistory.Count - 1;
    }

    private bool TryFindNextStudyIndex(int currentIndex, out int nextIndex)
    {
        nextIndex = -1;

        if (_items.Count == 0)
            return false;

        var normalizedCurrentIndex = currentIndex >= 0 && currentIndex < _items.Count
            ? currentIndex
            : -1;

        for (var offset = 1; offset <= _items.Count; offset++)
        {
            var candidateIndex = (normalizedCurrentIndex + offset) % _items.Count;
            if (candidateIndex == normalizedCurrentIndex)
                break;

            var candidate = _items[candidateIndex];
            var doneForThisPass = _isCramMode
                ? _cramStepsRemaining.GetValueOrDefault(candidate.Id, CramStepsToMaster) <= 0
                : _sessionKnownCardIds.Contains(candidate.Id);
            if (doneForThisPass)
                continue;

            nextIndex = candidateIndex;
            return true;
        }

        return false;
    }

    private bool CanMoveToNextStudyItem(bool fromHistory)
    {
        if (_items.Count == 0)
            return false;

        if (fromHistory && _studyHistoryPosition < _studyHistory.Count - 1)
            return true;

        return TryFindNextStudyIndex(_studyModeIndex, out _);
    }

    private bool TryMoveToNextStudyItem(bool fromHistory)
    {
        if (_items.Count == 0)
            return false;

        if (fromHistory && _studyHistoryPosition < _studyHistory.Count - 1)
        {
            _studyHistoryPosition++;
            MoveToStudyItem(_studyHistory[_studyHistoryPosition], appendToHistory: false);
            return true;
        }

        if (!TryFindNextStudyIndex(_studyModeIndex, out var nextIndex))
            return false;

        MoveToStudyItem(_items[nextIndex], appendToHistory: true);
        return true;
    }

    private void UpdateShuffleButtonState()
    {
        if (ShuffleModeButton is null)
            return;

        ShuffleModeButton.IsEnabled = _items.Count >= 2;
    }

    private void UpdateStudyModeFullscreenButton()
    {
        if (FullscreenButton is null)
            return;

        FullscreenButton.ToolTip = LocalizationService.GetString(IsStudyFullscreen
            ? "StudyModeExitFullscreen"
            : "StudyModeFullscreenTooltip");
    }

    private bool IsStudyFullscreen => ShellWindowChrome.IsFullscreenSuspended(this);

    private void ToggleFullscreenMode()
    {
        if (IsStudyFullscreen)
            ShellWindowChrome.ResumeFromFullscreen(this);
        else
            ShellWindowChrome.SuspendForFullscreen(this);

        UpdateStudyModeFullscreenButton();
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreenMode();
    }

    private void OptionsPanelToggleButton_Click(object sender, RoutedEventArgs e)
        => SetOptionsPanelOpen(!_isOptionsPanelOpen);

    private void SetOptionsPanelOpen(bool isOpen)
    {
        _isOptionsPanelOpen = isOpen;
        _optionsPanelAnimationVersion++;
        AnimateChromePanel(FlashcardsOptionsPanel, isOpen, _optionsPanelAnimationVersion, () => _isOptionsPanelOpen);
    }

    private void AnimateChromePanel(Border panel, bool show, int animationVersion, Func<bool> isStillOpen)
    {
        if (UiMotion.PrefersReducedMotion)
        {
            UiMotion.Stop(panel);
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            panel.Opacity = show ? 1 : 0;
            EnsureChromePanelTransform(panel).Y = show ? 0 : -6;
            return;
        }

        var transform = EnsureChromePanelTransform(panel);
        var easing = show ? UiMotion.OutQuad : UiMotion.InQuad;
        var opacityFrom = panel.Opacity;
        var yFrom = transform.Y;

        if (show)
            panel.Visibility = Visibility.Visible;

        UiMotion.Stop(panel);
        UiMotion.RunComposited(
            panel,
            new[]
            {
                UiMotion.Track(UiMotion.FastMs, easing, opacityFrom, show ? 1 : 0, value => panel.Opacity = value),
                UiMotion.Track(UiMotion.FastMs, easing, yFrom, show ? 0 : -6, value => transform.Y = value)
            },
            () =>
            {
                if (!show && animationVersion == _optionsPanelAnimationVersion && !isStillOpen())
                    panel.Visibility = Visibility.Collapsed;
            });
    }

    private static TranslateTransform EnsureChromePanelTransform(Border panel)
    {
        if (panel.RenderTransform is TranslateTransform existing)
            return existing;

        var transform = new TranslateTransform(0, -6);
        panel.RenderTransform = transform;
        return transform;
    }

    private void ShuffleModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count <= 1)
            return;

        var shuffled = _items.OrderBy(_ => _random.Next()).ToList();
        _items.Clear();
        foreach (var item in shuffled)
            _items.Add(item);

        _studyModeIndex = 0;
        ResetStudyHistory();
        ApplyStudyModeState();
    }

    private void FlashcardSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = FlashcardSearchTextBox.Text ?? string.Empty;

        _searchFilterDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchFilterDebounceTimer.Stop();
        _searchFilterDebounceTimer.Tick -= SearchFilterDebounceTimer_Tick;
        _searchFilterDebounceTimer.Tick += SearchFilterDebounceTimer_Tick;
        _searchFilterDebounceTimer.Start();
    }

    private void SearchFilterDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchFilterDebounceTimer?.Stop();
        ApplyFilters();
    }

    private void FiltersButton_Click(object sender, RoutedEventArgs e)
    {
        var wasOpen = FiltersPopup.IsOpen;
        UiMotion.TogglePopup(FiltersPopup);
        if (wasOpen)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            FlashcardSearchTextBox.Focus();
            FlashcardSearchTextBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CategoryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryFilterComboBox.SelectedItem is not FlashcardCategoryFilterOption option)
            return;

        _categoryFilter = option.Category?.Trim() ?? string.Empty;
        ApplyFilters();
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        StatusFilterComboBox.SelectedIndex = 0;
        CategoryFilterComboBox.SelectedIndex = 0;
        FlashcardSearchTextBox.Clear();
        _dueOnly = false;
        DueOnlyFilterCheckBox.IsChecked = false;
        _starredOnly = false;
        StarredOnlyFilterCheckBox.IsChecked = false;
        ApplyFilters();
        UpdateEditedIndicator();
        UiMotion.ClosePopup(FiltersPopup);
    }

    private void DueOnlyFilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        SetDueOnlyFilter(DueOnlyFilterCheckBox.IsChecked == true);
    }

    private void StarredOnlyFilterCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _starredOnly = StarredOnlyFilterCheckBox.IsChecked == true;
        ApplyFilters();
    }

    private void ToggleStarButton_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveFlashcardFromElement(sender) is { } flashcard)
        {
            flashcard.IsStarred = !flashcard.IsStarred;
            UpdateEditedIndicator();
        }

        e.Handled = true;
    }

    private void SetDueOnlyFilter(bool value)
    {
        if (_dueOnly == value)
            return;

        _dueOnly = value;
        DueOnlyFilterCheckBox.IsChecked = value;
        ApplyFilters();
        UpdateEditedIndicator();
    }

    private int? GetSelectedSetIndex()
    {
        return SetSelectorComboBox.SelectedItem is FlashcardSetOption set
            ? set.SetIndex
            : null;
    }

    private int GetNextSetIndex()
    {
        return _setOptions.Count == 0
            ? DefaultSetIndex
            : _setOptions.Max(option => option.SetIndex) + 1;
    }

    private void UpdateSetSelectorState()
    {
        SetSelectorComboBox.IsEnabled = _setOptions.Count > 0;
    }

    private void RenameSetButton_Click(object sender, RoutedEventArgs e)
    {
        if (SetSelectorComboBox.SelectedItem is not FlashcardSetOption selectedSet)
            return;

        RenameSet(selectedSet);
    }

    private void RenameSetInListButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FlashcardSetOption selectedSet })
            return;

        SetSelectorComboBox.SelectedItem = selectedSet;
        RenameSet(selectedSet);
        e.Handled = true;
    }

    private void RenameSet(FlashcardSetOption selectedSet)
    {
        var dialog = new SimpleInputDialog(
            LocalizationService.GetString("RenameFlashcardSet"),
            LocalizationService.GetString("RenameFlashcardSetPrompt"),
            selectedSet.DisplayName)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        var newName = dialog.InputText.Trim();
        if (string.IsNullOrWhiteSpace(newName))
            return;

        selectedSet.DisplayName = newName;
        ReorderSetOptions();
        UpdateEditedIndicator();
    }

    private void DeleteSetButton_Click(object sender, RoutedEventArgs e)
    {
        if (SetSelectorComboBox.SelectedItem is not FlashcardSetOption selectedSet)
            return;

        DeleteSet(selectedSet);
    }

    private void DeleteSetInListButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FlashcardSetOption selectedSet })
            return;

        DeleteSet(selectedSet);
        e.Handled = true;
    }

    private void DeleteSet(FlashcardSetOption selectedSet)
    {
        var confirmed = ModernDialog.ConfirmDanger(
            this,
            LocalizationService.GetString("DeleteFlashcardSet"),
            string.Format(LocalizationService.GetString("DeleteFlashcardSetConfirmationFormat"), selectedSet.DisplayName),
            LocalizationService.GetString("Confirm"));

        if (!confirmed)
            return;

        for (var i = _allItems.Count - 1; i >= 0; i--)
        {
            if (_allItems[i].SetIndex == selectedSet.SetIndex)
                _allItems.RemoveAt(i);
        }

        _setOptions.Remove(selectedSet);
        if (_setOptions.Count > 0)
            SetSelectorComboBox.SelectedItem = _setOptions.OrderBy(option => option.SetIndex).First();
        else
            _currentSetIndex = DefaultSetIndex;

        UpdateSetSelectorState();
        InitializeCategoryFilterOptions();
        ApplyFilters();
        UpdateEditedIndicator();
    }

    private FlashcardSetOption AddSetOption(int setIndex, string? displayName = null, bool selectSet = false)
    {
        var normalizedSetIndex = Math.Max(DefaultSetIndex, setIndex);
        var existing = _setOptions.FirstOrDefault(option => option.SetIndex == normalizedSetIndex);
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(displayName))
                existing.DisplayName = displayName;

            if (selectSet)
                SetSelectorComboBox.SelectedItem = existing;

            return existing;
        }

        var option = new FlashcardSetOption
        {
            SetIndex = normalizedSetIndex,
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? string.Format(LocalizationService.GetString("FlashcardSetFormat"), normalizedSetIndex)
                : displayName.Trim()
        };

        _setOptions.Add(option);
        ReorderSetOptions();

        if (selectSet)
            SetSelectorComboBox.SelectedItem = _setOptions.FirstOrDefault(o => o.SetIndex == option.SetIndex);

        UpdateSetSelectorState();
        return option;
    }

    private void ReorderSetOptions()
    {
        var selectedSetIndex = GetSelectedSetIndex();
        var ordered = _setOptions.OrderBy(option => option.SetIndex).ToList();
        _setOptions.Clear();

        foreach (var option in ordered)
            _setOptions.Add(option);

        if (selectedSetIndex.HasValue)
            SetSelectorComboBox.SelectedItem = _setOptions.FirstOrDefault(option => option.SetIndex == selectedSetIndex.Value);

        if (SetSelectorComboBox.SelectedItem is null && _setOptions.Count > 0)
            SetSelectorComboBox.SelectedIndex = 0;
    }

    private bool TryCreateSet(out FlashcardSetOption createdSet, bool selectSet)
    {
        var nextSetIndex = GetNextSetIndex();
        var defaultName = string.Format(LocalizationService.GetString("FlashcardSetFormat"), nextSetIndex);

        var dialog = new SimpleInputDialog(
            LocalizationService.GetString("CreateFlashcardSetTitle"),
            LocalizationService.GetString("CreateFlashcardSetPrompt"),
            defaultName)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            createdSet = null!;
            return false;
        }

        var setName = string.IsNullOrWhiteSpace(dialog.InputText)
            ? defaultName
            : dialog.InputText.Trim();

        createdSet = AddSetOption(nextSetIndex, setName, selectSet);
        UpdateEditedIndicator();
        return true;
    }

    private void CreateSetButton_Click(object sender, RoutedEventArgs e)
    {
        TryCreateSet(out _, selectSet: true);
    }

    private void AddFlashcardButton_Click(object sender, RoutedEventArgs e)
    {
        CreateFlashcardInSet(GetSelectedSetIndex() ?? _currentSetIndex);
    }

    private static FlashcardPreviewItem? ResolveFlashcardFromMenuItem(MenuItem menuItem)
    {
        if (menuItem.DataContext is FlashcardPreviewItem flashcard)
            return flashcard;

        if (menuItem.Parent is MenuItem parentMenuItem)
            return ResolveFlashcardFromMenuItem(parentMenuItem);

        if (menuItem.Parent is ContextMenu { PlacementTarget: FrameworkElement placementTarget }
            && placementTarget.DataContext is FlashcardPreviewItem placementFlashcard)
        {
            return placementFlashcard;
        }

        return null;
    }

    private void MoveFlashcardToSet(FlashcardPreviewItem flashcard, int targetSetIndex)
    {
        var normalizedSetIndex = Math.Max(DefaultSetIndex, targetSetIndex);
        if (flashcard.SetIndex == normalizedSetIndex)
            return;

        var selectedSetIndex = GetSelectedSetIndex();
        flashcard.SetIndex = normalizedSetIndex;

        if (selectedSetIndex.HasValue)
            ApplySetFilter(selectedSetIndex.Value);

        UpdateEditedIndicator();
    }

    private void ShowMoveToSetPicker(FlashcardPreviewItem flashcard)
    {
        var pickerMenu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint
        };

        foreach (var setOption in _setOptions.OrderBy(option => option.SetIndex))
        {
            var targetSetIndex = setOption.SetIndex;
            var setItem = new MenuItem
            {
                Header = setOption.DisplayName,
                IsEnabled = flashcard.SetIndex != targetSetIndex
            };

            setItem.Click += (_, _) => MoveFlashcardToSet(flashcard, targetSetIndex);
            pickerMenu.Items.Add(setItem);
        }

        pickerMenu.Items.Add(new Separator());

        var createAndMoveItem = new MenuItem
        {
            Header = LocalizationService.GetString("CreateFlashcardSetAndMove")
        };
        createAndMoveItem.Click += (_, _) =>
        {
            if (!TryCreateSet(out var createdSet, selectSet: false))
                return;

            MoveFlashcardToSet(flashcard, createdSet.SetIndex);
        };

        pickerMenu.Items.Add(createAndMoveItem);
        pickerMenu.IsOpen = true;
    }

    private void MoveToSetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem moveToSetMenu)
            return;

        var flashcard = ResolveFlashcardFromMenuItem(moveToSetMenu);
        if (flashcard is null)
            return;

        ShowMoveToSetPicker(flashcard);
        e.Handled = true;
    }

    private void AddFlashcardToSetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem addToSetMenu)
            return;

        var pickerMenu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint
        };

        foreach (var setOption in _setOptions.OrderBy(option => option.SetIndex))
        {
            var targetSetIndex = setOption.SetIndex;
            var setItem = new MenuItem
            {
                Header = setOption.DisplayName
            };

            setItem.Click += (_, _) => CreateFlashcardInSet(targetSetIndex);
            pickerMenu.Items.Add(setItem);
        }

        pickerMenu.Items.Add(new Separator());

        var createSetAndAddItem = new MenuItem
        {
            Header = LocalizationService.GetString("CreateFlashcardSetAndAdd")
        };
        createSetAndAddItem.Click += (_, _) =>
        {
            if (!TryCreateSet(out var createdSet, selectSet: false))
                return;

            CreateFlashcardInSet(createdSet.SetIndex);
        };

        pickerMenu.Items.Add(createSetAndAddItem);
        pickerMenu.IsOpen = true;
        e.Handled = true;
    }

    private void CreateFlashcardInSet(int targetSetIndex)
    {
        var normalizedSetIndex = Math.Max(DefaultSetIndex, targetSetIndex);
        AddSetOption(normalizedSetIndex, selectSet: false);

        var dialog = new EditFlashcardDialog(true, GetCategoryOptions())
        {
            Owner = this,
            Question = string.Empty,
            Answer = string.Empty,
            Category = string.Empty,
            Hint = string.Empty
        };

        if (dialog.ShowDialog() != true)
            return;

        var flashcard = new FlashcardPreviewItem(
            Guid.NewGuid(),
            dialog.Question,
            dialog.Answer,
            normalizedSetIndex,
            dialog.Category,
            isKnown: false,
            isUnknown: false,
            hint: dialog.Hint,
            questionImageFileName: dialog.QuestionImageFileName,
            questionAudioFileName: dialog.QuestionAudioFileName,
            answerImageFileName: dialog.AnswerImageFileName,
            answerAudioFileName: dialog.AnswerAudioFileName);
        _allItems.Add(flashcard);

        var selectedSetIndex = GetSelectedSetIndex() ?? _currentSetIndex;
        ApplySetFilter(selectedSetIndex);
        UpdateEditedIndicator();
    }

    private void StartStudyModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCramMode)
            return;

        if (_items.Count == 0 && !_isStudyMode && !TryResolveEmptyStudyQueue())
            return;

        if (_items.Count == 0)
            return;

        if (_isStudyMode)
        {
            if (IsStudyFullscreen)
                ToggleFullscreenMode();

            _isStudyMode = false;
            // ApplyFilters (not just ApplyStudyModeState) - cards graded during this session may
            // no longer be due, and the grid needs to drop them rather than keep showing stale
            // matches until some other filter change happens to trigger a refresh. Preserve study
            // position since this is a pause, not a completion - resuming should still work.
            ApplyFilters(preserveStudyPosition: true);
            return;
        }

        var startDecision = GetStudyStartDecision();
        if (startDecision == StudyStartDecision.Cancel)
            return;

        // Must clear before EnsureContinuableStudyIndex/ResetStudyStatusesForCurrentSet below -
        // both position/reset based on "which cards still need this session's review", which
        // depends on this being empty at the start of a fresh Start Study click.
        _sessionKnownCardIds.Clear();

        if (startDecision == StudyStartDecision.Restart)
        {
            ResetStudyStatusesForCurrentSet();
            _studyModeIndex = 0;
            ResetStudyHistory();
        }
        else
        {
            EnsureContinuableStudyIndex();
        }

        _studyCompletionDialogShown = false;
        _studyCelebrationShown = false;
        _sessionGradeCounts.Clear();
        _sessionCardsReviewed = 0;
        _sessionStartedAt = DateTime.Now;
        _isStudyMode = true;
        ApplyStudyModeState();
    }

    /// <summary>Keeps _sessionKnownCardIds in sync with a grading outcome - mirrors how IsKnown/
    /// IsUnknown are mutually exclusive, so re-failing a card already passed earlier this session
    /// correctly puts it back in the rotation instead of leaving it stuck "done".</summary>
    private void UpdateSessionKnownState(Guid cardId, bool isKnown)
    {
        if (isKnown)
            _sessionKnownCardIds.Add(cardId);
        else
            _sessionKnownCardIds.Remove(cardId);
    }

    /// <summary>
    /// Cram Mode: unlimited practice through everything matching the current filters, regardless
    /// of due date, that never touches a card's real SM-2 state - mirrors Anki's filtered-deck
    /// "reschedule: off" cram behavior. Always starts fresh (no resume prompt); exiting is simple
    /// since nothing real was ever written.
    /// </summary>
    private void CramModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCramMode)
        {
            if (IsStudyFullscreen)
                ToggleFullscreenMode();

            _isStudyMode = false;
            _isCramMode = false;
            _cramStepsRemaining.Clear();
            _cramLapNumber = 1;
            _cramCardsReviewed = 0;
            // ApplyFilters (not just ApplyStudyModeState) - cram populates _items ignoring the
            // due-only filter, so exiting needs to rebuild it from the real filters now that
            // _isCramMode is false again, or the due-only queue stays stuck showing every card.
            ApplyFilters();
            return;
        }

        if (_isStudyMode)
            return;

        _isCramMode = true;
        _cramStepsRemaining.Clear();
        _cramLapNumber = 1;
        _cramCardsReviewed = 0;
        ApplyFilters();

        if (_items.Count == 0)
        {
            _isCramMode = false;
            return;
        }

        _studyCompletionDialogShown = false;
        _studyCelebrationShown = false;
        _isStudyMode = true;
        ApplyStudyModeState();
    }

    /// <summary>
    /// Due-only filtering can legitimately leave nothing to study ("all caught up for now") -
    /// offer to study anyway (turns due-only off for this set) rather than the button silently
    /// doing nothing. Returns true if the study queue now has cards in it.
    /// </summary>
    private bool TryResolveEmptyStudyQueue()
    {
        if (!_dueOnly || !_allItems.Any(item => item.SetIndex == _currentSetIndex))
            return false;

        var result = ModernDialog.Show(
            this,
            LocalizationService.GetString("StudyModeAllCaughtUpTitle"),
            LocalizationService.GetString("StudyModeAllCaughtUpMessage"),
            ModernDialogTone.Question,
            LocalizationService.GetString("StudyModeStudyAnyway"),
            LocalizationService.GetString("Cancel"));

        if (result != ModernDialogResult.Primary)
            return false;

        SetDueOnlyFilter(false);
        return _items.Count > 0;
    }

    private StudyStartDecision GetStudyStartDecision()
    {
        if (!_dueOnly && AreAllStudyItemsKnown())
        {
            var result = ModernDialog.Show(
                this,
                LocalizationService.GetString("StudyModeResumeTitle"),
                LocalizationService.GetString("StudyModeCompletedRestartMessage"),
                ModernDialogTone.Question,
                LocalizationService.GetString("StudyModeRestart"),
                LocalizationService.GetString("Cancel"));

            return result == ModernDialogResult.Primary
                ? StudyStartDecision.Restart
                : StudyStartDecision.Cancel;
        }

        if (!HasStudyProgress())
            return StudyStartDecision.Restart;

        var resumeResult = ModernDialog.Show(
            this,
            LocalizationService.GetString("StudyModeResumeTitle"),
            LocalizationService.GetString("StudyModeResumeMessage"),
            ModernDialogTone.Question,
            LocalizationService.GetString("StudyModeRestart"),
            LocalizationService.GetString("Cancel"),
            LocalizationService.GetString("StudyModeContinue"));

        return resumeResult switch
        {
            ModernDialogResult.Secondary => StudyStartDecision.Continue,
            ModernDialogResult.Primary => StudyStartDecision.Restart,
            _ => StudyStartDecision.Cancel
        };
    }

    private void EnsureContinuableStudyIndex()
    {
        if (_studyModeIndex >= 0
            && _studyModeIndex < _items.Count
            && !_sessionKnownCardIds.Contains(_items[_studyModeIndex].Id))
        {
            return;
        }

        var nextUnstudiedIndex = _items.ToList().FindIndex(item => !_sessionKnownCardIds.Contains(item.Id));
        _studyModeIndex = nextUnstudiedIndex >= 0 ? nextUnstudiedIndex : 0;
    }

    private void StudyModeCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        FlipCurrentStudyCard();
    }

    private void StudyHintPrompt_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FlashcardPreviewItem item })
            item.IsHintRevealed = true;

        e.Handled = true;
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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveAndClose();
    }

    private void FlashcardsPreviewWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (IsStudyFullscreen)
            ToggleFullscreenMode();

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

    private void FlashcardCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FlashcardFlipHost host && host.DataContext is FlashcardPreviewItem item)
        {
            SelectFlashcard(item);
            AnimateFlashcardFlip(host);
        }
    }

    private void SelectFlashcard(FlashcardPreviewItem item)
    {
        if (ReferenceEquals(_selectedItem, item))
            return;

        _audioPlayer?.Stop();

        if (_selectedItem is not null)
            _selectedItem.IsSelected = false;

        _selectedItem = item;
        _selectedItem.IsSelected = true;
    }

    private void MoveFlashcardButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FlashcardPreviewItem flashcard)
            ShowMoveToSetPicker(flashcard);

        e.Handled = true;
    }

    private void MoveStudyModeFlashcardButton_Click(object sender, RoutedEventArgs e)
    {
        if (StudyModeCard.DataContext is FlashcardPreviewItem flashcard)
            ShowMoveToSetPicker(flashcard);

        e.Handled = true;
    }

    private void EditFlashcardButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FlashcardPreviewItem flashcard)
            EditFlashcard(flashcard);

        e.Handled = true;
    }

    private void QuestionAnswerToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var showAnswers = !_items.All(item => item.IsFlipped);
        foreach (var item in _items)
            item.IsFlipped = showAnswers;

        UpdateQuestionAnswerToggleButton();
    }

    private void UpdateQuestionAnswerToggleButton()
    {
        if (QuestionAnswerToggleButton is null)
            return;

        QuestionAnswerToggleButton.IsEnabled = _items.Count > 0;
        QuestionAnswerToggleButton.Content = LocalizationService.GetString(
            _items.Count > 0 && _items.All(item => item.IsFlipped)
                ? "FlashcardsShowQuestions"
                : "FlashcardsShowAnswers");
    }

    private void AnimateFlashcardFlip(FlashcardFlipHost host)
    {
        if (host.IsAnimating)
            return;

        host.AnimateToggle(GetFlipAnimationDurationMs(), () =>
        {
            UpdateQuestionAnswerToggleButton();
            if (ReferenceEquals(host, StudyModeFlipHost))
                UpdateGradeButtonsForFlipState();
        });
    }

    // Hover is lift-only on the shared parent Grid (CardShadowLayer + CardSurface) so the
    // baked plate and face move together. No live DropShadowEffect — rest elevation is the
    // nine-slice plate. FlashcardsGridViewBorder/StudyModeViewBorder clip via RoundedClipBehavior,
    // so both containers carry extra Padding for the plate bleed and lift.
    private void FlashcardStudyCardSurface_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement surface)
            return;

        ClearStudyCardLiveShadow(surface);

        if (surface.Parent is not FrameworkElement cardRoot)
            return;

        cardRoot.RenderTransformOrigin = new Point(0.5, 0.5);
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(cardRoot);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        var liftFrom = translate.Y;
        UiMotion.Stop(cardRoot);
        UiMotion.Run(cardRoot, new[]
        {
            UiMotion.Track(UiMotion.HoverInMs, UiMotion.OutCubic, liftFrom, UiMotion.CardHoverLiftY, value => translate.Y = value)
        });
    }

    private void FlashcardStudyCardSurface_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement surface)
            return;

        ClearStudyCardLiveShadow(surface);

        if (surface.Parent is not FrameworkElement cardRoot)
            return;

        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(cardRoot);
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        var liftFrom = translate.Y;
        UiMotion.Stop(cardRoot);
        UiMotion.Run(cardRoot, new[]
        {
            UiMotion.Track(UiMotion.HoverOutMs, UiMotion.OutCubic, liftFrom, 0, value => translate.Y = value)
        });
    }

    private static void ClearStudyCardLiveShadow(FrameworkElement surface)
    {
        if (surface.Effect is DropShadowEffect)
            surface.Effect = null;

        if (surface.FindName("ShadowLayer") is FrameworkElement shadowLayer
            && shadowLayer.Effect is DropShadowEffect)
        {
            UiMotion.Stop(shadowLayer);
            shadowLayer.Effect = null;
        }
    }

    private void InitializeFlipSpeedSlider(int preferredFlipDelayMilliseconds = 0)
    {
        if (FlipSpeedSlider is null)
            return;

        // Per-collection only: unset (0) uses the built-in default, never another set's speed.
        var initialDelay = preferredFlipDelayMilliseconds > 0
            ? preferredFlipDelayMilliseconds
            : AppSettings.DefaultFlashcardFlipDelayMilliseconds;

        // Guard before Min/Max/Value changes — setting Minimum can clamp Value and fire
        // ValueChanged, which would otherwise overwrite the real preference with 100ms.
        _isApplyingFlipSpeed = true;
        FlipSpeedSlider.Minimum = AppSettings.MinFlashcardFlipDelayMilliseconds;
        FlipSpeedSlider.Maximum = AppSettings.MaxFlashcardFlipDelayMilliseconds;
        FlipSpeedSlider.TickFrequency = AppSettings.FlashcardFlipDelayStepMilliseconds;
        FlipSpeedSlider.SmallChange = AppSettings.FlashcardFlipDelayStepMilliseconds;
        FlipSpeedSlider.LargeChange = AppSettings.FlashcardFlipDelayStepMilliseconds;
        FlipSpeedSlider.Value = AppSettings.NormalizeFlashcardFlipDelayMilliseconds(initialDelay);
        _isApplyingFlipSpeed = false;

        FlipSpeedSlider.ValueChanged += FlipSpeedSlider_ValueChanged;
    }

    private void FlipSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingFlipSpeed || FlipSpeedSlider is null)
            return;

        // Snap the thumb to a clean step (100, 200, …) even if drag lands between ticks.
        var snapped = GetFlipAnimationDurationMs();
        if (Math.Abs(FlipSpeedSlider.Value - snapped) > 0.01)
        {
            _isApplyingFlipSpeed = true;
            FlipSpeedSlider.Value = snapped;
            _isApplyingFlipSpeed = false;
        }

        // Persist on this set only (ToDocument / autosave → FlipDelayMilliseconds).
        if (!_isInitializing)
            UpdateEditedIndicator();
    }

    private int GetFlipAnimationDurationMs()
    {
        if (FlipSpeedSlider is null)
            return AppSettings.DefaultFlashcardFlipDelayMilliseconds;

        return AppSettings.NormalizeFlashcardFlipDelayMilliseconds(
            (int)Math.Round(FlipSpeedSlider.Value));
    }

    private TranslateTransform EnsureStudyModeCardTranslate()
    {
        if (StudyModeCard.RenderTransform is TransformGroup group)
        {
            var translate = group.Children.OfType<TranslateTransform>().FirstOrDefault();
            if (translate is not null)
                return translate;
        }

        if (StudyModeCard.RenderTransform is TranslateTransform loneTranslate)
            return loneTranslate;

        var created = new TranslateTransform();
        StudyModeCard.RenderTransform = created;
        return created;
    }

    private void ApplyStudyModeState()
    {
        if (_items.Count == 0)
        {
            _isStudyMode = false;
            _studyModeIndex = 0;
            ResetStudyHistory();
        }

        // Ensure index is within bounds
        if (_studyModeIndex < 0)
            _studyModeIndex = 0;
        if (_studyModeIndex >= _items.Count)
            _studyModeIndex = Math.Max(0, _items.Count - 1);

        ApplyStudyModeLayout(_isStudyMode);
        StartStudyModeButton.IsEnabled = _items.Count > 0 && !_isCramMode;
        StartStudyModeButton.Content = LocalizationService.GetString(_isStudyMode && !_isCramMode ? "StudyModeExit" : "StudyModeStart");
        CramModeButton.IsEnabled = _isCramMode || (!_isStudyMode && _allItems.Any(item => item.SetIndex == _currentSetIndex));
        CramModeButton.Content = LocalizationService.GetString(_isCramMode ? "StudyModeExit" : "FlashcardCramStart");
        CramModeBanner.Visibility = _isStudyMode && _isCramMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateQuestionAnswerToggleButton();
        UpdateStudyModeFullscreenButton();

        if (!_isStudyMode || _items.Count == 0)
            return;

        // Count progress for the current study session. Cram mode tracks learning-step "steps
        // remaining" per card transiently instead of the real IsKnown/IsUnknown flags, which are
        // never touched while cramming.
        var knownCount = _isCramMode
            ? _items.Count(i => _cramStepsRemaining.GetValueOrDefault(i.Id, CramStepsToMaster) <= 0)
            : _items.Count(i => _sessionKnownCardIds.Contains(i.Id));
        var remainingCount = _items.Count - knownCount;

        SetStudyModeCardInteractive(true);

        // Cram mode never "completes" - once a lap is fully mastered, start a fresh one instead
        // of showing the completion screen, so practice can continue for as long as wanted.
        if (remainingCount == 0 && _isCramMode)
        {
            _cramStepsRemaining.Clear();
            _cramLapNumber++;
            knownCount = 0;
            remainingCount = _items.Count;
        }

        // Show completion if all cards are known
        if (remainingCount == 0)
        {
            SetStudyProgressComplete();
            SetStudyModeCardInteractive(false);
            StudyModeProgressText.Text = string.Format(
                LocalizationService.GetString("StudyModeProgress"),
                _items.Count,
                _items.Count);
            ShowStudyModeCompletionText();
            StudyModePreviousButton.IsEnabled = false;
            StudyModeNextButton.IsEnabled = false;
            StudyModeAgainButton.IsEnabled = false;
            StudyModeHardButton.IsEnabled = false;
            StudyModeGoodButton.IsEnabled = false;
            StudyModeEasyButton.IsEnabled = false;
            StudyModeFlipHintText.Visibility = Visibility.Hidden;

            if (!_studyCompletionDialogShown)
            {
                _studyCompletionDialogShown = true;
                ShowStudyCompletionDialog();
            }

            return;
        }
        else
        {
            HideStudyModeCompletionText();
        }

        var currentItem = (_studyModeIndex >= 0 && _studyModeIndex < _items.Count)
            ? _items[_studyModeIndex]
            : null;

        if (currentItem is null)
        {
            if (!TryMoveToNextStudyItem(fromHistory: false))
            {
                SetStudyProgressComplete();
                SetStudyModeCardInteractive(false);
                StudyModeProgressText.Text = string.Format(
                    LocalizationService.GetString("StudyModeProgress"),
                    _items.Count,
                    _items.Count);
                ShowStudyModeCompletionText();
                StudyModePreviousButton.IsEnabled = _studyHistoryPosition > 0;
                StudyModeNextButton.IsEnabled = false;
                StudyModeAgainButton.IsEnabled = false;
                StudyModeHardButton.IsEnabled = false;
                StudyModeGoodButton.IsEnabled = false;
                StudyModeEasyButton.IsEnabled = false;
                StudyModeFlipHintText.Visibility = Visibility.Hidden;
                return;
            }

            currentItem = _items[_studyModeIndex];
        }

        EnsureCurrentStudyItemInHistory(currentItem);
        UpdateStudyProgressLine(_studyHistory.Distinct().Count(), knownCount, _items.Count);

        StudyModeCard.DataContext = currentItem;
        StudyModeFlipHost.IsFlipped = false;
        StudyModeProgressText.Text = string.Format(
            LocalizationService.GetString("StudyModeProgress"),
            _studyModeIndex + 1,
            _items.Count);

        StudyModePreviousButton.IsEnabled = !_isStudyModeCardAnimating && _studyHistoryPosition > 0;
        StudyModeNextButton.IsEnabled = !_isStudyModeCardAnimating && CanMoveToNextStudyItem(fromHistory: true);
        UpdateGradeButtonsForFlipState();

        if (!_isInitializing)
            UpdateEditedIndicator();
    }

    private void SetStudyModeCardInteractive(bool isInteractive)
    {
        UiMotion.Stop(StudyModeCard);
        UiMotion.Clear(StudyModeCard, UIElement.OpacityProperty);

        var translate = EnsureStudyModeCardTranslate();
        UiMotion.Clear(translate, TranslateTransform.XProperty);
        translate.X = 0;

        StudyModeCard.Visibility = isInteractive ? Visibility.Visible : Visibility.Hidden;
        StudyModeCard.IsHitTestVisible = isInteractive;
        StudyModeCard.Opacity = isInteractive ? 1 : 0;
        StudyModeCard.Cursor = isInteractive ? Cursors.Hand : Cursors.Arrow;
    }

    private void UpdateStudyProgressLine(int studiedCount, int knownCount, int totalCount)
    {
        if (StudyModeProgressTrack is null || StudyModeProgressFill is null)
            return;

        UpdateLiveStatsText(studiedCount);

        if (totalCount <= 0)
        {
            SetStudyProgressFillWidth(0);
            return;
        }

        var targetWidth = GetStudyProgressTrackWidth() * Math.Clamp((double)knownCount / totalCount, 0, 1);
        var currentWidth = StudyModeProgressFill.Width;
        if (double.IsNaN(currentWidth) || currentWidth < 0)
            currentWidth = 0;

        UiMotion.AnimateWidth(StudyModeProgressFill, currentWidth, targetWidth, UiMotion.FastMs, UiMotion.OutCubic);
    }

    private void SetStudyProgressComplete()
    {
        if (StudyModeProgressTrack is null || StudyModeProgressFill is null)
            return;

        UpdateLiveStatsText(_studyHistory.Distinct().Count());

        UiMotion.Stop(StudyModeProgressFill);
        SetStudyProgressFillWidth(GetStudyProgressTrackWidth());
    }

    private static CardMaturity GetCardMaturity(FlashcardPreviewItem item)
        => FlashcardStatisticsCalculator.ClassifyMaturity(item.DueDate, item.LastReviewedAt);

    /// <summary>Known/Unknown only reflect the outcome of a card's *last* grade, not real
    /// progress - a card seen once and a card mastered over months both read "Known". The live
    /// stats line instead shows maturity tiers (New/Learning/Mature), the same scheduling-derived
    /// breakdown Anki itself surfaces, so it actually says something about study progress.</summary>
    private void UpdateLiveStatsText(int studiedCount)
    {
        if (StudyModeLiveStatsText is null)
            return;

        if (_isCramMode)
        {
            StudyModeLiveStatsText.Text = string.Format(
                LocalizationService.GetString("FlashcardCramLiveStatsFormat"),
                _cramLapNumber,
                _cramCardsReviewed);
            return;
        }

        var now = DateTime.Now;
        var dueTodayCount = _allItems.Count(item =>
            item.SetIndex == _currentSetIndex && FlashcardReviewScheduler.IsDue(item.DueDate, now));

        var maturities = _items.Select(GetCardMaturity).ToList();
        var newCount = maturities.Count(m => m == CardMaturity.New);
        var learningCount = maturities.Count(m => m == CardMaturity.Learning);
        var matureCount = maturities.Count(m => m == CardMaturity.Mature);

        StudyModeLiveStatsText.Text = string.Format(
            LocalizationService.GetString("StudyModeLiveStatsFormat"),
            studiedCount,
            newCount,
            learningCount,
            matureCount,
            dueTodayCount);
    }

    private void SetStudyProgressFillWidth(double width)
    {
        if (StudyModeProgressFill is null)
            return;

        StudyModeProgressFill.Width = width;
        StudyModeProgressFill.Height = 4;
    }

    private void ApplyStudyModeLayout(bool showStudyMode)
    {
        ApplyStudyModeEditorChrome(showStudyMode);

        if (_isInitializing || UiMotion.PrefersReducedMotion || showStudyMode == _studyModeLayoutVisible)
        {
            SetStudyModeLayoutImmediate(showStudyMode);
            _studyModeLayoutVisible = showStudyMode;
            return;
        }

        if (showStudyMode)
        {
            UiMotion.CrossFadePanels(
                FlashcardsGridViewBorder,
                StudyModeViewBorder,
                1,
                UiMotion.SectionMs,
                () => _studyModeLayoutVisible = true);
        }
        else
        {
            UiMotion.CrossFadePanels(
                StudyModeViewBorder,
                FlashcardsGridViewBorder,
                -1,
                UiMotion.SectionMs,
                () => _studyModeLayoutVisible = false);
        }
    }

    private void SetStudyModeLayoutImmediate(bool showStudyMode)
    {
        FlashcardsGridViewBorder.Visibility = showStudyMode ? Visibility.Collapsed : Visibility.Visible;
        StudyModeViewBorder.Visibility = showStudyMode ? Visibility.Visible : Visibility.Collapsed;
        FlashcardsGridViewBorder.Opacity = 1;
        StudyModeViewBorder.Opacity = 1;
    }

    /// <summary>
    /// Tags/options are editor chrome — hide while studying so the card fits without scrolling.
    /// </summary>
    private void ApplyStudyModeEditorChrome(bool showStudyMode)
    {
        var chromeVisibility = showStudyMode ? Visibility.Collapsed : Visibility.Visible;
        if (FlashcardsTagsBorder is not null)
            FlashcardsTagsBorder.Visibility = chromeVisibility;
        if (showStudyMode && _isOptionsPanelOpen)
            SetOptionsPanelOpen(false);
        if (OptionsPanelToggleButton is not null)
            OptionsPanelToggleButton.Visibility = chromeVisibility;
        if (FlashcardsOptionsPanel is not null && !_isOptionsPanelOpen)
            FlashcardsOptionsPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowStudyModeCompletionText()
    {
        StudyModeCompletionText.Text = LocalizationService.GetString("StudyModeComplete");
        if (StudyModeCompletionText.Visibility == Visibility.Visible)
            return;

        StudyModeProgressText.Visibility = Visibility.Collapsed;
        StudyModeCompletionText.Visibility = Visibility.Visible;

        if (_studyCelebrationShown)
            return;

        _studyCelebrationShown = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (CelebrationOverlay != null)
                StudyCelebration.PlayConfetti(CelebrationOverlay);
            StudyCelebration.RevealScore(StudyModeCompletionText);
        });
    }

    private void HideStudyModeCompletionText()
    {
        if (StudyModeCompletionText.Visibility != Visibility.Visible)
        {
            StudyModeProgressText.Visibility = Visibility.Visible;
            return;
        }

        StudyCelebration.Stop(CelebrationOverlay);

        UiMotion.Fade(
            StudyModeCompletionText,
            0,
            UiMotion.QuickMs,
            UiMotion.InQuad,
            () =>
            {
                StudyModeCompletionText.Visibility = Visibility.Collapsed;
                StudyModeCompletionText.Opacity = 1;
                StudyModeProgressText.Visibility = Visibility.Visible;
            });
    }

    private void ShowStudyCompletionDialog()
    {
        var dialogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3400) };
        dialogTimer.Tick += (_, _) =>
        {
            dialogTimer.Stop();
            StudyCelebration.Stop(CelebrationOverlay);
            ModernDialog.ShowSuccess(
                this,
                LocalizationService.GetString("StudyModeCompleteTitle"),
                BuildStudyCompletionSummary());

            _isStudyMode = false;
            // ApplyFilters (not just ApplyStudyModeState) - every card just studied has a fresh
            // DueDate and is no longer due, so the grid needs to actually drop them, not just
            // update layout chrome. A clean slate is correct here (unlike the pause/exit toggle) -
            // the session is genuinely done, there's nothing left to resume into.
            ApplyFilters();
        };
        dialogTimer.Start();
    }

    /// <summary>
    /// Real numbers for the session that just finished, instead of a generic congratulations -
    /// the counts are already tracked by GradeCurrentStudyCard, just never surfaced before.
    /// </summary>
    private string BuildStudyCompletionSummary()
    {
        var again = _sessionGradeCounts.GetValueOrDefault(FlashcardReviewGrade.Again);
        var hard = _sessionGradeCounts.GetValueOrDefault(FlashcardReviewGrade.Hard);
        var good = _sessionGradeCounts.GetValueOrDefault(FlashcardReviewGrade.Good);
        var easy = _sessionGradeCounts.GetValueOrDefault(FlashcardReviewGrade.Easy);

        var elapsed = DateTime.Now - _sessionStartedAt;
        var elapsedText = elapsed.TotalHours >= 1
            ? string.Format(CultureInfo.CurrentCulture, "{0:0}h {1:0}m", elapsed.TotalHours, elapsed.Minutes)
            : string.Format(CultureInfo.CurrentCulture, "{0:0}m {1:0}s", elapsed.Minutes, elapsed.Seconds);

        var lines = new List<string>
        {
            string.Format(LocalizationService.GetString("StudyModeCompleteSummaryFormat"), _sessionCardsReviewed),
            string.Format(LocalizationService.GetString("StudyModeCompleteBreakdownFormat"), again, hard, good, easy),
            string.Format(LocalizationService.GetString("StudyModeCompleteTimeFormat"), elapsedText)
        };

        var dueSoonCount = _items.Count(item =>
            item.DueDate.HasValue && (item.DueDate.Value.Date - DateTime.Now.Date).Days <= 3);
        if (dueSoonCount > 0)
            lines.Add(string.Format(LocalizationService.GetString("StudyModeCompleteNextDueFormat"), dueSoonCount));

        return string.Join("\n", lines);
    }

    private sealed class FlashcardPreviewItem : INotifyPropertyChanged
    {
        public Guid Id { get; }
        private bool _isFlipped;
        private bool _isKnown;
        private bool _isUnknown;
        private bool _isStarred;
        private bool _isSelected;
        private string _question = string.Empty;
        private string _answer = string.Empty;
        private string _category = string.Empty;
        private string _hint = string.Empty;
        private bool _isHintRevealed;
        private int _setIndex;
        private string? _questionImageFileName;
        private string? _questionAudioFileName;
        private string? _answerImageFileName;
        private string? _answerAudioFileName;
        private DateTime? _dueDate;
        private DateTime? _lastReviewedAt;
        private ImageSource? _questionImageSource;
        private ImageSource? _answerImageSource;

        public FlashcardPreviewItem(
            Guid id,
            string question,
            string answer,
            int setIndex,
            string? category = null,
            bool isKnown = false,
            bool isUnknown = false,
            string? hint = null,
            double easeFactor = 2.5,
            int intervalDays = 0,
            int repetitions = 0,
            DateTime? dueDate = null,
            DateTime? lastReviewedAt = null,
            double stability = 0,
            double difficulty = 0,
            string? questionImageFileName = null,
            string? questionAudioFileName = null,
            string? answerImageFileName = null,
            string? answerAudioFileName = null,
            bool isStarred = false)
        {
            Id = id == Guid.Empty ? Guid.NewGuid() : id;
            Question = question;
            Answer = answer;
            SetIndex = setIndex;
            Category = category ?? string.Empty;
            Hint = hint ?? string.Empty;
            _isKnown = isKnown && !isUnknown;
            _isUnknown = isUnknown && !isKnown;
            _isStarred = isStarred;
            EaseFactor = easeFactor;
            IntervalDays = intervalDays;
            Repetitions = repetitions;
            DueDate = dueDate;
            LastReviewedAt = lastReviewedAt;
            Stability = stability;
            Difficulty = difficulty;
            QuestionImageFileName = questionImageFileName;
            QuestionAudioFileName = questionAudioFileName;
            AnswerImageFileName = answerImageFileName;
            AnswerAudioFileName = answerAudioFileName;
        }

        /// <summary>SM-2 scheduling state - EaseFactor/IntervalDays/Repetitions are not
        /// individually change-notified (nothing in the UI binds to them directly), only
        /// read/written wholesale by FlashcardReviewScheduler grading and the due-only filter.
        /// DueDate/LastReviewedAt are the exception below - Maturity is derived from them, so they
        /// need to notify for the status chip/filter to stay in sync with grading.</summary>
        public double EaseFactor { get; set; }
        public int IntervalDays { get; set; }
        public int Repetitions { get; set; }

        public DateTime? DueDate
        {
            get => _dueDate;
            set
            {
                if (_dueDate == value)
                    return;
                _dueDate = value;
                OnPropertyChanged();
                NotifyMaturityChanged();
            }
        }

        public DateTime? LastReviewedAt
        {
            get => _lastReviewedAt;
            set
            {
                if (_lastReviewedAt == value)
                    return;
                _lastReviewedAt = value;
                OnPropertyChanged();
                NotifyMaturityChanged();
            }
        }

        /// <summary>FSRS-6 scheduling state - same non-notified, wholesale read/write pattern as
        /// the SM-2 fields above, but only touched when this set's Algorithm is Fsrs6.</summary>
        public double Stability { get; set; }
        public double Difficulty { get; set; }

        /// <summary>Long-term memory-strength classification (see FlashcardStatisticsCalculator),
        /// derived purely from DueDate/LastReviewedAt - deliberately independent of IsKnown/
        /// IsUnknown, which only reflect the outcome of the *last* grade, not how well-entrenched
        /// the card actually is. This is what the grid/study status chip and browse filter show;
        /// IsKnown/IsUnknown remain internal bookkeeping for in-session progress only (see
        /// AreAllStudyItemsKnown/HasStudyProgress and the study progress bar).</summary>
        public CardMaturity Maturity => FlashcardStatisticsCalculator.ClassifyMaturity(DueDate, LastReviewedAt);
        public bool IsMaturityNew => Maturity == CardMaturity.New;
        public bool IsMaturityLearning => Maturity == CardMaturity.Learning;
        public bool IsMaturityMature => Maturity == CardMaturity.Mature;

        private void NotifyMaturityChanged()
        {
            OnPropertyChanged(nameof(Maturity));
            OnPropertyChanged(nameof(IsMaturityNew));
            OnPropertyChanged(nameof(IsMaturityLearning));
            OnPropertyChanged(nameof(IsMaturityMature));
        }

        public string? QuestionImageFileName
        {
            get => _questionImageFileName;
            set
            {
                if (_questionImageFileName == value)
                    return;

                _questionImageFileName = value;
                _questionImageSource = FlashcardMediaStorageService.LoadImage(value, decodePixelWidth: 340);
                OnPropertyChanged();
                OnPropertyChanged(nameof(QuestionImageSource));
                OnPropertyChanged(nameof(HasQuestionImage));
            }
        }

        public string? AnswerImageFileName
        {
            get => _answerImageFileName;
            set
            {
                if (_answerImageFileName == value)
                    return;

                _answerImageFileName = value;
                _answerImageSource = FlashcardMediaStorageService.LoadImage(value, decodePixelWidth: 340);
                OnPropertyChanged();
                OnPropertyChanged(nameof(AnswerImageSource));
                OnPropertyChanged(nameof(HasAnswerImage));
            }
        }

        public string? QuestionAudioFileName
        {
            get => _questionAudioFileName;
            set
            {
                if (_questionAudioFileName == value)
                    return;

                _questionAudioFileName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasQuestionAudio));
            }
        }

        public string? AnswerAudioFileName
        {
            get => _answerAudioFileName;
            set
            {
                if (_answerAudioFileName == value)
                    return;

                _answerAudioFileName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAnswerAudio));
            }
        }

        public ImageSource? QuestionImageSource => _questionImageSource;
        public ImageSource? AnswerImageSource => _answerImageSource;
        public bool HasQuestionImage => !string.IsNullOrWhiteSpace(_questionImageFileName);
        public bool HasAnswerImage => !string.IsNullOrWhiteSpace(_answerImageFileName);
        public bool HasQuestionAudio => !string.IsNullOrWhiteSpace(_questionAudioFileName);
        public bool HasAnswerAudio => !string.IsNullOrWhiteSpace(_answerAudioFileName);

        private bool _isQuestionAudioPlaying;
        private bool _isAnswerAudioPlaying;

        /// <summary>Reflects the single shared AudioClipPlayer's state - only ever true for at
        /// most one card/side across the whole window, since only one clip can play at a time.</summary>
        public bool IsQuestionAudioPlaying
        {
            get => _isQuestionAudioPlaying;
            set
            {
                if (_isQuestionAudioPlaying == value)
                    return;

                _isQuestionAudioPlaying = value;
                OnPropertyChanged();
            }
        }

        public bool IsAnswerAudioPlaying
        {
            get => _isAnswerAudioPlaying;
            set
            {
                if (_isAnswerAudioPlaying == value)
                    return;

                _isAnswerAudioPlaying = value;
                OnPropertyChanged();
            }
        }

        public string Question
        {
            get => _question;
            set
            {
                if (_question != value)
                {
                    _question = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Answer
        {
            get => _answer;
            set
            {
                if (_answer != value)
                {
                    _answer = value;
                    OnPropertyChanged();
                }
            }
        }

        public int SetIndex
        {
            get => _setIndex;
            set
            {
                if (_setIndex == value)
                    return;

                _setIndex = Math.Max(DefaultSetIndex, value);
                OnPropertyChanged();
            }
        }

        public string Category
        {
            get => _category;
            set
            {
                var normalized = value?.Trim() ?? string.Empty;
                if (_category == normalized)
                    return;

                _category = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCategory));
            }
        }

        public bool HasCategory => !string.IsNullOrWhiteSpace(_category);

        public string Hint
        {
            get => _hint;
            set
            {
                var normalized = value?.Trim() ?? string.Empty;
                if (_hint == normalized)
                    return;

                _hint = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasHint));
                OnPropertyChanged(nameof(ShowHintPrompt));
            }
        }

        public bool HasHint => !string.IsNullOrWhiteSpace(_hint);

        public bool IsHintRevealed
        {
            get => _isHintRevealed;
            set
            {
                if (_isHintRevealed == value)
                    return;

                _isHintRevealed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowHintPrompt));
            }
        }

        public bool ShowHintPrompt => HasHint && !_isHintRevealed;

        public bool IsFlipped
        {
            get => _isFlipped;
            set
            {
                if (_isFlipped == value)
                    return;
                _isFlipped = value;
                OnPropertyChanged();
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
                OnPropertyChanged();
            }
        }

        public bool IsKnown
        {
            get => _isKnown;
            set
            {
                if (_isKnown == value)
                    return;
                _isKnown = value;
                OnPropertyChanged();
            }
        }

        public bool IsUnknown
        {
            get => _isUnknown;
            set
            {
                if (_isUnknown == value)
                    return;
                _isUnknown = value;
                OnPropertyChanged();
            }
        }

        /// <summary>User-authored personal marking (Quizlet's "star") - purely manual, never
        /// touched by grading/scheduling. Independent of Maturity and IsKnown/IsUnknown.</summary>
        public bool IsStarred
        {
            get => _isStarred;
            set
            {
                if (_isStarred == value)
                    return;
                _isStarred = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>A single, collection-wide choice (Settings > Scheduling algorithm), matching Anki's
    /// own FSRS toggle - not a per-set option. Read live rather than cached, so a change made in
    /// Settings while this editor is already open takes effect on the next grade.</summary>
    private static SpacedRepetitionAlgorithm CurrentAlgorithm => AppSettingsService.Load().FlashcardSchedulingAlgorithm;

    private void StudyModeAgainButton_Click(object sender, RoutedEventArgs e) => GradeCurrentStudyCard(FlashcardReviewGrade.Again);

    private void StudyModeHardButton_Click(object sender, RoutedEventArgs e) => GradeCurrentStudyCard(FlashcardReviewGrade.Hard);

    private void StudyModeGoodButton_Click(object sender, RoutedEventArgs e) => GradeCurrentStudyCard(FlashcardReviewGrade.Good);

    private void StudyModeEasyButton_Click(object sender, RoutedEventArgs e) => GradeCurrentStudyCard(FlashcardReviewGrade.Easy);

    /// <summary>
    /// Applies an SM-2 grade to the current card and always advances - replaces the old asymmetric
    /// "Known auto-advances, Unknown toggles in place" behavior with one consistent grade-then-move
    /// flow, matching every rival app. Requires the card to have been flipped first (self-testing:
    /// recall, check the answer, then grade yourself).
    /// </summary>
    private void GradeCurrentStudyCard(FlashcardReviewGrade grade)
    {
        if (_isStudyModeCardAnimating
            || !StudyModeFlipHost.IsFlipped
            || _items.Count == 0
            || _studyModeIndex < 0
            || _studyModeIndex >= _items.Count)
        {
            return;
        }

        var currentItem = _items[_studyModeIndex];

        if (_isCramMode)
        {
            // Mirrors Anki's cram/no-reschedule learning-step behavior, adapted to this app's
            // position-based rotation instead of literal clock delays: Again resets progress back
            // to square one, Hard makes no progress but isn't punished as hard as Again, Good
            // needs CramStepsToMaster correct grades in a row to retire the card for this lap, and
            // Easy retires it immediately. Never touches the card's real SM-2 state
            // (EaseFactor/IntervalDays/Repetitions/DueDate/LastReviewedAt/IsKnown/IsUnknown).
            var stepsRemaining = _cramStepsRemaining.GetValueOrDefault(currentItem.Id, CramStepsToMaster);
            _cramStepsRemaining[currentItem.Id] = grade switch
            {
                FlashcardReviewGrade.Again => CramStepsToMaster,
                FlashcardReviewGrade.Hard => stepsRemaining,
                FlashcardReviewGrade.Good => Math.Max(0, stepsRemaining - 1),
                FlashcardReviewGrade.Easy => 0,
                _ => stepsRemaining
            };

            _cramCardsReviewed++;
        }
        else if (CurrentAlgorithm == SpacedRepetitionAlgorithm.Fsrs6)
        {
            var result = FsrsScheduler.ApplyGrade(
                new FsrsScheduler.ReviewState(currentItem.Stability, currentItem.Difficulty, currentItem.LastReviewedAt),
                grade,
                DateTime.Now);

            currentItem.Stability = result.Stability;
            currentItem.Difficulty = result.Difficulty;
            currentItem.DueDate = result.DueDate;
            currentItem.LastReviewedAt = DateTime.Now;
            currentItem.IsKnown = result.IsKnown;
            currentItem.IsUnknown = result.IsUnknown;
            UpdateSessionKnownState(currentItem.Id, result.IsKnown);

            _sessionGradeCounts[grade] = _sessionGradeCounts.GetValueOrDefault(grade) + 1;
            _sessionCardsReviewed++;
        }
        else
        {
            var result = FlashcardReviewScheduler.ApplyGrade(
                new FlashcardReviewScheduler.ReviewState(currentItem.EaseFactor, currentItem.IntervalDays, currentItem.Repetitions),
                grade,
                DateTime.Now);

            currentItem.EaseFactor = result.EaseFactor;
            currentItem.IntervalDays = result.IntervalDays;
            currentItem.Repetitions = result.Repetitions;
            currentItem.DueDate = result.DueDate;
            currentItem.LastReviewedAt = DateTime.Now;
            currentItem.IsKnown = result.IsKnown;
            currentItem.IsUnknown = result.IsUnknown;
            UpdateSessionKnownState(currentItem.Id, result.IsKnown);

            _sessionGradeCounts[grade] = _sessionGradeCounts.GetValueOrDefault(grade) + 1;
            _sessionCardsReviewed++;
        }

        AnimateStudyModeCardChange(1, () => TryMoveToNextStudyItem(fromHistory: false));
    }

    /// <summary>Enables the grade buttons only once the answer has actually been revealed.</summary>
    private void UpdateGradeButtonsForFlipState()
    {
        var canGrade = !_isStudyModeCardAnimating && StudyModeFlipHost.IsFlipped;
        StudyModeAgainButton.IsEnabled = canGrade;
        StudyModeHardButton.IsEnabled = canGrade;
        StudyModeGoodButton.IsEnabled = canGrade;
        StudyModeEasyButton.IsEnabled = canGrade;
        StudyModeFlipHintText.Visibility = canGrade ? Visibility.Hidden : Visibility.Visible;
        UpdateGradeIntervalPreviews();
    }

    /// <summary>Shows each grade button's projected next-due interval (Anki-style "again 1d, good
    /// 6d, easy 13d" hints) so grading doesn't require guessing or reading scheduler source. Not
    /// shown in cram mode, since cram never touches real scheduling and has no interval to preview.</summary>
    private void UpdateGradeIntervalPreviews()
    {
        if (_isCramMode || _studyModeIndex < 0 || _studyModeIndex >= _items.Count)
        {
            StudyModeAgainIntervalText.Text = string.Empty;
            StudyModeHardIntervalText.Text = string.Empty;
            StudyModeGoodIntervalText.Text = string.Empty;
            StudyModeEasyIntervalText.Text = string.Empty;
            return;
        }

        var currentItem = _items[_studyModeIndex];
        var now = DateTime.Now;

        StudyModeAgainIntervalText.Text = FormatIntervalPreview(currentItem, FlashcardReviewGrade.Again, now);
        StudyModeHardIntervalText.Text = FormatIntervalPreview(currentItem, FlashcardReviewGrade.Hard, now);
        StudyModeGoodIntervalText.Text = FormatIntervalPreview(currentItem, FlashcardReviewGrade.Good, now);
        StudyModeEasyIntervalText.Text = FormatIntervalPreview(currentItem, FlashcardReviewGrade.Easy, now);
    }

    private string FormatIntervalPreview(FlashcardPreviewItem item, FlashcardReviewGrade grade, DateTime now)
    {
        var dueDate = CurrentAlgorithm == SpacedRepetitionAlgorithm.Fsrs6
            ? FsrsScheduler.ApplyGrade(
                new FsrsScheduler.ReviewState(item.Stability, item.Difficulty, item.LastReviewedAt),
                grade,
                now).DueDate
            : FlashcardReviewScheduler.ApplyGrade(
                new FlashcardReviewScheduler.ReviewState(item.EaseFactor, item.IntervalDays, item.Repetitions),
                grade,
                now).DueDate;

        var days = Math.Max(1, (dueDate.Date - now.Date).Days);
        return FormatIntervalDays(days);
    }

    private static string FormatIntervalDays(int days)
    {
        if (days < 30)
            return string.Format(LocalizationService.GetString("FlashcardIntervalDays"), days);
        if (days < 365)
            return string.Format(LocalizationService.GetString("FlashcardIntervalMonths"), Math.Max(1, (int)Math.Round(days / 30.0)));
        return string.Format(LocalizationService.GetString("FlashcardIntervalYears"), Math.Max(1, (int)Math.Round(days / 365.0)));
    }

    private void StudyModePreviousButton_Click(object sender, RoutedEventArgs e)
    {
        MoveToPreviousStudyCardWithAnimation();
    }

    private void MoveToPreviousStudyCardWithAnimation()
    {
        if (_items.Count == 0 || _studyHistoryPosition <= 0)
            return;

        AnimateStudyModeCardChange(-1, () =>
        {
            _studyHistoryPosition--;
            MoveToStudyItem(_studyHistory[_studyHistoryPosition], appendToHistory: false);
            return true;
        });
    }

    private void StudyModeNextButton_Click(object sender, RoutedEventArgs e)
    {
        MoveToNextStudyCardWithAnimation();
    }

    private void MoveToNextStudyCardWithAnimation()
    {
        if (_items.Count == 0)
            return;

        AnimateStudyModeCardChange(1, () => TryMoveToNextStudyItem(fromHistory: true));
    }

    private void AnimateStudyModeCardChange(int direction, Func<bool> moveCard)
    {
        if (_isStudyModeCardAnimating)
            return;

        _isStudyModeCardAnimating = true;
        StudyModePreviousButton.IsEnabled = false;
        StudyModeNextButton.IsEnabled = false;
        StudyModeAgainButton.IsEnabled = false;
        StudyModeHardButton.IsEnabled = false;
        StudyModeGoodButton.IsEnabled = false;
        StudyModeEasyButton.IsEnabled = false;

        var translate = EnsureStudyModeCardTranslate();

        var exitOffset = direction >= 0 ? -UiMotion.StudyNavShiftX : UiMotion.StudyNavShiftX;
        var enterOffset = direction >= 0 ? UiMotion.StudyNavShiftX : -UiMotion.StudyNavShiftX;
        var opacityStart = StudyModeCard.Opacity;

        UiMotion.Stop(StudyModeCard);
        UiMotion.Clear(StudyModeCard, UIElement.OpacityProperty);
        UiMotion.Clear(translate, TranslateTransform.XProperty);

        UiMotion.RunComposited(
            StudyModeCard,
            new[]
            {
                UiMotion.Track(UiMotion.NavFadeOutMs, UiMotion.InCubic, opacityStart, 0, value => StudyModeCard.Opacity = value),
                UiMotion.Track(UiMotion.NavFadeOutMs, UiMotion.InCubic, 0, exitOffset, value => translate.X = value)
            },
            () =>
            {
                var moved = moveCard();

                if (!moved
                    || !_isStudyMode
                    || StudyModeViewBorder.Visibility != Visibility.Visible
                    || StudyModeCard.Visibility != Visibility.Visible)
                {
                    StudyModeCard.Opacity = 1;
                    translate.X = 0;
                    _isStudyModeCardAnimating = false;
                    ApplyStudyModeState();
                    return;
                }

                translate.X = enterOffset;
                StudyModeCard.Opacity = 0;

                UiMotion.RunComposited(
                    StudyModeCard,
                    new[]
                    {
                        UiMotion.Track(UiMotion.FastMs, UiMotion.OutCubic, 0, 1, value => StudyModeCard.Opacity = value),
                        UiMotion.Track(UiMotion.FastMs, UiMotion.OutCubic, enterOffset, 0, value => translate.X = value)
                    },
                    () =>
                    {
                        _isStudyModeCardAnimating = false;
                        StudyModeCard.Opacity = 1;
                        translate.X = 0;
                        ApplyStudyModeState();
                    });
            });
    }

    private void EditFlashcardMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
            return;

        var flashcard = ResolveFlashcardFromMenuItem(menuItem);
        if (flashcard is null)
            return;

        EditFlashcard(flashcard);
    }

    private void EditFlashcard(FlashcardPreviewItem flashcard)
    {
        var dialog = new EditFlashcardDialog(categoryOptions: GetCategoryOptions())
        {
            Owner = this,
            Question = flashcard.Question,
            Answer = flashcard.Answer,
            Category = flashcard.Category,
            Hint = flashcard.Hint,
            QuestionImageFileName = flashcard.QuestionImageFileName,
            QuestionAudioFileName = flashcard.QuestionAudioFileName,
            AnswerImageFileName = flashcard.AnswerImageFileName,
            AnswerAudioFileName = flashcard.AnswerAudioFileName
        };

        var result = dialog.ShowDialog();

        if (result == true)
        {
            // Directly update the flashcard
            flashcard.Question = dialog.Question;
            flashcard.Answer = dialog.Answer;
            flashcard.Category = dialog.Category;
            flashcard.Hint = dialog.Hint;
            flashcard.QuestionImageFileName = dialog.QuestionImageFileName;
            flashcard.QuestionAudioFileName = dialog.QuestionAudioFileName;
            flashcard.AnswerImageFileName = dialog.AnswerImageFileName;
            flashcard.AnswerAudioFileName = dialog.AnswerAudioFileName;
            InitializeCategoryFilterOptions();
            ApplyFilters();
            UpdateEditedIndicator();
        }
    }

    // ? Delete flashcard menu item handler
    private void DeleteFlashcardMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
            return;

        var flashcard = ResolveFlashcardFromMenuItem(menuItem);
        if (flashcard is null)
            return;

        // Show confirmation dialog
        var result = ModernMessageBox.Show(
            LocalizationService.GetString("DeleteFlashcardConfirmation"),
            LocalizationService.GetString("DeleteFlashcard"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
            return;

        // Remove from all items
        _items.Remove(flashcard);

        _allItems.Remove(flashcard);

        for (var i = _studyHistory.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_studyHistory[i], flashcard))
                continue;

            _studyHistory.RemoveAt(i);
            if (_studyHistoryPosition >= i)
                _studyHistoryPosition--;
        }

        if (_studyHistoryPosition < -1)
            _studyHistoryPosition = -1;

        // Adjust study mode index if needed
        if (_studyModeIndex >= _items.Count)
            _studyModeIndex = Math.Max(0, _items.Count - 1);

        InitializeCategoryFilterOptions();
        ApplyFilters();
        ApplyStudyModeState();
        UpdateEditedIndicator();
    }

    private sealed class FlashcardSetOption
    {
        public int SetIndex { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class FlashcardStatusFilterOption
    {
        public CardMaturity? Maturity { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class FlashcardCategoryFilterOption
    {
        public string Category { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }
}
