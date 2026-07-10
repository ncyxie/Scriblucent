using NoteCards.Models;
using NoteCards.Services;
using NoteCards.Localization;
using NoteCards;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using NoteCards.Animations;
using NoteCards.Controls;

namespace NoteCards.Views;

public partial class FlashcardsPreviewWindow : Window, INotifyPropertyChanged
{
    private const double StudyProgressBarWidth = 316;

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
    private bool? _statusFilterIsKnown;
    private string _categoryFilter = string.Empty;
    private string _searchText = string.Empty;
    private string _modelDisplayName = string.Empty;
    private string _lastSavedSnapshot = string.Empty;
    private bool _isInitializing = true;
    private bool _studyModeLayoutVisible;
    private bool _allowCloseWithoutPrompt;
    private bool _isApplyingFlipSpeed;
    private FlashcardStudySession? _pendingStudySession;
    private FlashcardNoteLinkOption? _selectedLinkedNote;

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
        Action<Guid>? openNoteAction = null)
    {
        InitializeComponent();
        FreezeStudyModeResources();
        NoteCards.Services.WindowThemeService.Register(this);
        InitializeFlipSpeedSlider();
        _openNoteAction = openNoteAction;
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
                i.IsUnknown))
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
            IsPinned = existingDocument?.IsPinned ?? false
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
                SetIndex = Math.Max(DefaultSetIndex, item.SetIndex),
                IsKnown = item.IsKnown,
                IsUnknown = item.IsUnknown
            })
            .ToList();
    }

    private FlashcardStudySession BuildStudySession()
    {
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
        UpdateEditedIndicator();
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
        var setSnapshot = string.Join(
            '\u001E',
            _setOptions
                .OrderBy(option => option.SetIndex)
                .Select(option => $"{option.SetIndex}\u001D{option.DisplayName.Trim()}"));

        var cardSnapshot = string.Join(
            '\u001E',
            _allItems.Select(item =>
                $"{item.Id}\u001D{item.SetIndex}\u001D{item.Question}\u001D{item.Answer}\u001D{item.Category}\u001D{item.IsKnown}\u001D{item.IsUnknown}"));

        var studyHistorySnapshot = string.Join('\u001E', _studyHistory.Select(item => item.Id));
        var studySnapshot = string.Join(
            '\u001E',
            _isStudyMode.ToString(),
            _currentSetIndex.ToString(),
            _studyModeIndex.ToString(),
            _studyHistoryPosition.ToString(),
            (_studyHistory.Count > 0 ? studyHistorySnapshot : string.Empty),
            GetCurrentStudyCardId()?.ToString() ?? string.Empty);

        return string.Join(
            '\u001F',
            TitleTextBox.Text,
            _selectedLinkedNote?.Id.ToString() ?? string.Empty,
            string.Join('\u001E', Tags),
            setSnapshot,
            cardSnapshot,
            studySnapshot);
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
        DialogResult = true;
        Close();
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
            IsKnown = null,
            DisplayName = LocalizationService.GetString("FlashcardStatusAll")
        });
        _statusFilterOptions.Add(new FlashcardStatusFilterOption
        {
            IsKnown = true,
            DisplayName = LocalizationService.GetString("Known")
        });
        _statusFilterOptions.Add(new FlashcardStatusFilterOption
        {
            IsKnown = false,
            DisplayName = LocalizationService.GetString("Unknown")
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

        _statusFilterIsKnown = option.IsKnown;
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
        }
    }

    private void FlipCurrentStudyCard()
    {
        if (!_isStudyMode
            || _isStudyModeCardAnimating
            || StudyModeFlipHost.IsAnimating
            || _items.Count == 0
            || _items.All(item => item.IsKnown)
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

    private void ApplyFilters()
    {
        var normalizedQuery = (_searchText ?? string.Empty).Trim();

        var filteredItems = _allItems.Where(item => item.SetIndex == _currentSetIndex);
        if (_statusFilterIsKnown.HasValue)
        {
            filteredItems = _statusFilterIsKnown.Value
                ? filteredItems.Where(item => item.IsKnown)
                : filteredItems.Where(item => item.IsUnknown);
        }

        if (!string.IsNullOrWhiteSpace(_categoryFilter))
        {
            filteredItems = filteredItems.Where(item =>
                string.Equals(item.Category, _categoryFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            filteredItems = filteredItems.Where(item =>
                item.Question.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || item.Answer.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || item.Category.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase));
        }

        var orderedItems = filteredItems.ToList();

        _items.Clear();
        foreach (var item in orderedItems)
            _items.Add(item);

        UpdateQuestionAnswerToggleButton();

        if (_pendingStudySession is not null)
        {
            RestoreStudySession(_pendingStudySession);
            _pendingStudySession = null;
        }
        else
        {
            _studyModeIndex = 0;
            ResetStudyHistory();
        }

        ApplyStudyModeState();
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
            if (!item.IsKnown && !item.IsUnknown)
                continue;

            item.IsKnown = false;
            item.IsUnknown = false;
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
        {
            item.IsKnown = false;
            item.IsUnknown = false;
        }

        ResetStudyHistory();
        ApplyFilters();
        UpdateEditedIndicator();
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

    private void MoveToStudyItem(FlashcardPreviewItem item, bool appendToHistory)
    {
        var index = _items.IndexOf(item);
        if (index < 0)
            return;

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

            if (_items[candidateIndex].IsKnown)
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

        ShuffleModeButton.Content = LocalizationService.GetString("ShuffleCards");
    }

    private void UpdateStudyModeFullscreenButton()
    {
        if (StudyModeFullscreenButton is null)
            return;

        StudyModeFullscreenButton.Content = LocalizationService.GetString(IsStudyFullscreen
            ? "StudyModeExitFullscreen"
            : "StudyModeFullscreen");
        StudyModeFullscreenButton.ToolTip = LocalizationService.GetString("StudyModeFullscreenTooltip");
    }

    private bool IsStudyFullscreen => WindowStyle == WindowStyle.None;

    private void ToggleFullscreenMode()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.NoResize;
        }

        UpdateStudyModeFullscreenButton();
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
        ApplyFilters();
        UiMotion.ClosePopup(FiltersPopup);
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
            Category = string.Empty
        };

        if (dialog.ShowDialog() != true)
            return;

        var flashcard = new FlashcardPreviewItem(Guid.NewGuid(), dialog.Question, dialog.Answer, normalizedSetIndex, dialog.Category, isKnown: false, isUnknown: false);
        _allItems.Add(flashcard);

        var selectedSetIndex = GetSelectedSetIndex() ?? _currentSetIndex;
        ApplySetFilter(selectedSetIndex);
        UpdateEditedIndicator();
    }

    private void StartStudyModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
            return;

        if (_isStudyMode)
        {
            if (IsStudyFullscreen)
                ToggleFullscreenMode();

            _isStudyMode = false;
            ApplyStudyModeState();
            return;
        }

        var startDecision = GetStudyStartDecision();
        if (startDecision == StudyStartDecision.Cancel)
            return;

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
        _isStudyMode = true;
        ApplyStudyModeState();
    }

    private StudyStartDecision GetStudyStartDecision()
    {
        if (AreAllStudyItemsKnown())
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
            && !_items[_studyModeIndex].IsKnown)
        {
            return;
        }

        var nextUnstudiedIndex = _items.ToList().FindIndex(item => !item.IsKnown);
        _studyModeIndex = nextUnstudiedIndex >= 0 ? nextUnstudiedIndex : 0;
    }

    private void StudyModeCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        FlipCurrentStudyCard();
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
                DialogResult = true;
                e.Cancel = false;
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

        host.AnimateToggle(GetFlipAnimationDurationMs(), UpdateQuestionAnswerToggleButton);
    }

    private void FreezeStudyModeResources()
    {
        if (FindResource("FlashcardStudyCardShadow") is DropShadowEffect studyShadow && studyShadow.CanFreeze)
            studyShadow.Freeze();

        if (FindResource("FlashcardStudyCardHoverShadow") is DropShadowEffect hoverShadow && hoverShadow.CanFreeze)
            hoverShadow.Freeze();
    }

    private void InitializeFlipSpeedSlider()
    {
        if (FlipSpeedSlider is null)
            return;

        FlipSpeedSlider.ValueChanged += FlipSpeedSlider_ValueChanged;

        var settings = AppSettingsService.Load();
        FlipSpeedSlider.Minimum = AppSettings.MinFlashcardFlipDelayMilliseconds;
        FlipSpeedSlider.Maximum = AppSettings.MaxFlashcardFlipDelayMilliseconds;
        _isApplyingFlipSpeed = true;
        FlipSpeedSlider.Value = AppSettings.NormalizeFlashcardFlipDelayMilliseconds(
            settings.FlashcardFlipDelayMilliseconds);
        _isApplyingFlipSpeed = false;
    }

    private void FlipSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingFlipSpeed || FlipSpeedSlider is null)
            return;

        var settings = AppSettingsService.Load();
        settings.FlashcardFlipDelayMilliseconds = AppSettings.NormalizeFlashcardFlipDelayMilliseconds(
            (int)Math.Round(FlipSpeedSlider.Value));
        AppSettingsService.Save(settings);
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
        StartStudyModeButton.IsEnabled = _items.Count > 0;
        StartStudyModeButton.Content = LocalizationService.GetString(_isStudyMode ? "StudyModeExit" : "StudyModeStart");
        UpdateQuestionAnswerToggleButton();

        if (!_isStudyMode || _items.Count == 0)
            return;

        // Count progress and status breakdown for the current study session
        var knownCount = _items.Count(i => i.IsKnown);
        var unknownCount = _items.Count(i => i.IsUnknown);
        var remainingCount = _items.Count - knownCount;

        SetStudyModeCardInteractive(true);

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
            StudyModeMarkKnownButton.IsEnabled = false;
            StudyModeMarkUnknownButton.IsEnabled = false;

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
                StudyModeMarkKnownButton.IsEnabled = false;
                StudyModeMarkUnknownButton.IsEnabled = false;
                return;
            }

            currentItem = _items[_studyModeIndex];
        }

        EnsureCurrentStudyItemInHistory(currentItem);
        UpdateStudyProgressLine(_studyHistory.Distinct().Count(), knownCount, unknownCount, _items.Count);

        StudyModeCard.DataContext = currentItem;
        StudyModeProgressText.Text = string.Format(
            LocalizationService.GetString("StudyModeProgress"),
            _studyModeIndex + 1,
            _items.Count);

        StudyModePreviousButton.IsEnabled = !_isStudyModeCardAnimating && _studyHistoryPosition > 0;
        StudyModeNextButton.IsEnabled = !_isStudyModeCardAnimating && CanMoveToNextStudyItem(fromHistory: true);
        StudyModeMarkKnownButton.IsEnabled = !_isStudyModeCardAnimating;
        StudyModeMarkUnknownButton.IsEnabled = !_isStudyModeCardAnimating;

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

    private void UpdateStudyProgressLine(int studiedCount, int knownCount, int unknownCount, int totalCount)
    {
        if (StudyModeProgressTrack is null || StudyModeProgressFill is null)
            return;

        var tooltipText = string.Format(
            "Studied: {0} | Known: {1} | Unknown: {2}",
            studiedCount,
            knownCount,
            unknownCount);

        StudyModeProgressTrack.ToolTip = tooltipText;

        if (totalCount <= 0)
        {
            SetStudyProgressFillWidth(0);
            return;
        }

        var targetWidth = StudyProgressBarWidth * Math.Clamp((double)knownCount / totalCount, 0, 1);
        var currentWidth = StudyModeProgressFill.Width;
        if (double.IsNaN(currentWidth) || currentWidth < 0)
            currentWidth = 0;

        UiMotion.AnimateWidth(StudyModeProgressFill, currentWidth, targetWidth, UiMotion.FastMs, UiMotion.OutCubic);
    }

    private void SetStudyProgressComplete()
    {
        if (StudyModeProgressTrack is null || StudyModeProgressFill is null)
            return;

        var studiedCount = _studyHistory.Distinct().Count();
        var knownCount = _items.Count(i => i.IsKnown);
        var unknownCount = _items.Count(i => i.IsUnknown);
        var tooltipText = string.Format(
            "Studied: {0} | Known: {1} | Unknown: {2}",
            studiedCount,
            knownCount,
            unknownCount);

        StudyModeProgressTrack.ToolTip = tooltipText;
        UiMotion.Stop(StudyModeProgressFill);
        SetStudyProgressFillWidth(StudyProgressBarWidth);
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

    private void ShowStudyModeCompletionText()
    {
        StudyModeCompletionText.Text = LocalizationService.GetString("StudyModeComplete");
        if (StudyModeCompletionText.Visibility == Visibility.Visible)
            return;

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
            return;

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
                LocalizationService.GetString("StudyModeCompleteMessage"));

            _isStudyMode = false;
            ApplyStudyModeState();
        };
        dialogTimer.Start();
    }

    private sealed class FlashcardPreviewItem : INotifyPropertyChanged
    {
        public Guid Id { get; }
        private bool _isFlipped;
        private bool _isKnown;
        private bool _isUnknown;
        private bool _isSelected;
        private string _question = string.Empty;
        private string _answer = string.Empty;
        private string _category = string.Empty;
        private int _setIndex;

        public FlashcardPreviewItem(
            Guid id,
            string question,
            string answer,
            int setIndex,
            string? category = null,
            bool isKnown = false,
            bool isUnknown = false)
        {
            Id = id == Guid.Empty ? Guid.NewGuid() : id;
            Question = question;
            Answer = answer;
            SetIndex = setIndex;
            Category = category ?? string.Empty;
            _isKnown = isKnown && !isUnknown;
            _isUnknown = isUnknown && !isKnown;
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
                OnPropertyChanged(nameof(IsUnreviewed));
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
                OnPropertyChanged(nameof(IsUnreviewed));
            }
        }

        public bool IsUnreviewed => !_isKnown && !_isUnknown;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private void StudyModeMarkKnownButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isStudyModeCardAnimating || _items.Count == 0 || _studyModeIndex < 0 || _studyModeIndex >= _items.Count)
            return;

        var currentItem = _items[_studyModeIndex];
        currentItem.IsKnown = true;
        currentItem.IsUnknown = false; // Clear unknown status

        AnimateStudyModeCardChange(1, () => TryMoveToNextStudyItem(fromHistory: false));
    }

    private void StudyModeMarkUnknownButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isStudyModeCardAnimating || _items.Count == 0 || _studyModeIndex < 0 || _studyModeIndex >= _items.Count)
            return;

        var currentItem = _items[_studyModeIndex];
        currentItem.IsUnknown = !currentItem.IsUnknown; // Toggle
        currentItem.IsKnown = false; // Clear known status if marking unknown

        ApplyStudyModeState();
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

    private void StudyModeFullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreenMode();
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
        StudyModeMarkKnownButton.IsEnabled = false;
        StudyModeMarkUnknownButton.IsEnabled = false;

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
            Category = flashcard.Category
        };

        var result = dialog.ShowDialog();

        if (result == true)
        {
            // Directly update the flashcard
            flashcard.Question = dialog.Question;
            flashcard.Answer = dialog.Answer;
            flashcard.Category = dialog.Category;
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
        public bool? IsKnown { get; set; }
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class FlashcardCategoryFilterOption
    {
        public string Category { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }
}
