using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Models;
using Scriblucent.Localization;
using Scriblucent.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Scriblucent.ViewModels;

public class MainViewModel : ViewModelBase
{
    private const string DefaultGroupBackground = "#F8FAFF";
    private const int MassDeleteAnimatedMax = 8;
    private int _recentItemsLimit = AppSettings.DefaultRecentItemsLimit;
    private const string SortLastModifiedDesc = "last-modified-desc";
    private const string SortLastModifiedAsc = "last-modified-asc";
    private const string SortCreatedAtDesc = "created-at-desc";
    private const string SortCreatedAtAsc = "created-at-asc";
    private const string SortTitleAsc = "title-asc";
    private const string SortTitleDesc = "title-desc";
    private const string SortFlashcardCardsDesc = "flashcard-cards-desc";
    private const string SortFlashcardCardsAsc = "flashcard-cards-asc";
    private const string SortMindMapNodesDesc = "mindmap-nodes-desc";
    private const string SortMindMapNodesAsc = "mindmap-nodes-asc";
    private const string SortQuizQuestionsDesc = "quiz-questions-desc";
    private const string SortQuizQuestionsAsc = "quiz-questions-asc";
    private const string DashboardNotes = "Notes";
    private const string DashboardFlashcards = "Flashcards";
    private const string DashboardMindMaps = "MindMaps";
    private const string DashboardQuizzes = "Quizzes";

    private bool _isLoadingSettings;
    private bool _saveNotesQueued;
    private DispatcherTimer? _notesMutationReactionTimer;
    private DispatcherTimer? _flashcardMutationReactionTimer;
    private DispatcherTimer? _quizMutationReactionTimer;
    private DispatcherTimer? _mindMapMutationReactionTimer;
    private string? _cachedPreferredFontFamily;
    private double _cachedPreferredFontSize;
    private int _ungroupedVisibleCount = -1;
    private bool? _hasUngroupedFlashcardSetsCache;
    private bool? _hasUngroupedMindMapsCache;
    private bool? _hasUngroupedQuizzesCache;
    private bool _notesMutationIsLightweightAdd;
    private bool _notesMutationIsLightweightDelete;
    private bool _flashcardMutationIsLightweightAdd;
    private bool _flashcardMutationIsLightweightDelete;
    private bool _quizMutationIsLightweightAdd;
    private bool _quizMutationIsLightweightDelete;
    private bool _mindMapMutationIsLightweightAdd;
    private bool _mindMapMutationIsLightweightDelete;
    private FlashcardSetViewModel? _flashcardMutationLightweightAddedSet;
    private QuizViewModel? _quizMutationLightweightAddedQuiz;
    private MindMapViewModel? _mindMapMutationLightweightAddedMindMap;

    private bool _notesBatchAddInProgress;
    private bool _flashcardBatchAddInProgress;
    private bool _flashcardDataLoadInProgress;
    private bool _flashcardSetsReorderInProgress;
    private bool _mindMapBatchAddInProgress;
    private bool _mindMapDataLoadInProgress;
    private bool _mindMapsReorderInProgress;
    private bool _quizBatchAddInProgress;
    private bool _quizDataLoadInProgress;
    private bool _quizzesReorderInProgress;
    private bool _notesPinReorderInProgress;
    private bool _notesPersistedContentLoaded;
    private bool _notesLoadComplete;
    private bool _flashcardsPersistedContentLoaded;
    private bool _mindMapsPersistedContentLoaded;
    private bool _quizzesPersistedContentLoaded;
    // Distinct from the *PersistedContentLoaded flags above (those just guard against re-entering
    // the load call while it's in flight). These gate the autosave path and must only flip true once
    // the background-loaded data has actually been applied to the collection - same reasoning as
    // _notesLoadComplete, so autosave can never fire while the collection is still empty mid-load.
    private bool _flashcardsLoadCompleted;
    private bool _mindMapsLoadCompleted;
    private bool _quizzesLoadCompleted;
    private bool _deferredDashboardLoadsScheduled;
    private bool _isNotesDashboardReady;
    private bool _isFlashcardsDashboardReady;
    private bool _isMindMapsDashboardReady;
    private bool _isQuizzesDashboardReady;
    private const int NotesLoadBatchSize = 48;
    private const int NotesSingleCommitMax = 120;

    public event EventHandler? NotesDashboardReady;

    public bool IsNotesDashboardReady => _isNotesDashboardReady;
    public bool IsNotesDashboardLoading => IsNotesView && !_isNotesDashboardReady;
    public bool ShowNotesEmptyDashboard => _isNotesDashboardReady && Notes.Count == 0;
    public bool ShouldShowNotesDashboardContent => Notes.Count > 0 || IsNotesDashboardLoading;

    public bool IsFlashcardsDashboardLoading => IsFlashcardsView && !_isFlashcardsDashboardReady;
    public bool ShowFlashcardsEmptyDashboard => _isFlashcardsDashboardReady && FlashcardSets.Count == 0;
    public bool ShouldShowFlashcardsDashboardContent => FlashcardSets.Count > 0 || IsFlashcardsDashboardLoading;

    public bool IsMindMapsDashboardLoading => IsMindMapsView && !_isMindMapsDashboardReady;
    public bool ShowMindMapsEmptyDashboard => _isMindMapsDashboardReady && MindMaps.Count == 0;
    public bool ShouldShowMindMapsDashboardContent => MindMaps.Count > 0 || IsMindMapsDashboardLoading;

    public bool IsQuizzesDashboardLoading => IsQuizzesView && !_isQuizzesDashboardReady;
    public bool ShowQuizzesEmptyDashboard => _isQuizzesDashboardReady && Quizzes.Count == 0;
    public bool ShouldShowQuizzesDashboardContent => Quizzes.Count > 0 || IsQuizzesDashboardLoading;

    public bool IsDashboardRevealMotionEnabled { get; private set; }
    private NoteCardViewModel? _notesMutationLightweightAddedNote;
    private DispatcherTimer? _autoSaveTimer;
    private bool _enableScrollbar = true;
    private bool _enableVerticalScrollbar = true;
    private bool _enableHorizontalScrollbar = true;
    private string _selectedLanguage = LocalizationService.English;
    private string _selectedTheme = "Light";
    private string _activeDashboard = DashboardNotes;
    private string _selectedSortOptionKey = SortLastModifiedDesc;
    private string _selectedFlashcardSortOptionKey = SortLastModifiedDesc;
    private string _selectedMindMapSortOptionKey = SortLastModifiedDesc;
    private string _selectedQuizSortOptionKey = SortLastModifiedDesc;
    private readonly Dictionary<Guid, NoteGroupData> _groupMetadata = new();
    private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedFlashcardTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedMindMapTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selectedQuizTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _massSelectedNoteIds = new();
    private readonly HashSet<Guid> _massSelectedFlashcardSetIds = new();
    private readonly HashSet<Guid> _massSelectedMindMapIds = new();
    private readonly HashSet<Guid> _massSelectedQuizIds = new();
    private bool _isMassSelectMode;
    private string _massSelectDashboard = DashboardNotes;
    private DateTime _calendarSelectedDate = DateTime.Today;
    private AppSettings _settings;
    private readonly Dictionary<Guid, FlashcardSetGroupData> _flashcardSetGroupMetadata = new();
    private readonly Dictionary<Guid, MindMapGroupData> _mindMapGroupMetadata = new();
    private readonly Dictionary<Guid, QuizGroupData> _quizGroupMetadata = new();
    private readonly ObservableCollection<QuizViewModel> _quizzes = new();
    public ObservableCollection<FlashcardSetGroupViewModel> FlashcardSetGroups { get; } = new();
    public ObservableCollection<MindMapGroupViewModel> MindMapGroups { get; } = new();
    public ObservableCollection<QuizGroupViewModel> QuizGroups { get; } = new();

    public bool EnableScrollbar
    {
        get => _enableScrollbar;
        set
        {
            if (_enableScrollbar != value)
            {
                _enableScrollbar = value;
                OnPropertyChanged(nameof(EnableScrollbar));
                SaveAppSettings();
            }
        }
    }

    public bool EnableVerticalScrollbar
    {
        get => _enableVerticalScrollbar;
        set
        {
            if (_enableVerticalScrollbar != value)
            {
                _enableVerticalScrollbar = value;
                OnPropertyChanged(nameof(EnableVerticalScrollbar));
                SaveAppSettings();
            }
        }
    }

    public bool EnableHorizontalScrollbar
    {
        get => _enableHorizontalScrollbar;
        set
        {
            if (_enableHorizontalScrollbar != value)
            {
                _enableHorizontalScrollbar = value;
                OnPropertyChanged(nameof(EnableHorizontalScrollbar));
                SaveAppSettings();
            }
        }
    }

    private string GetFlashcardsFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "flashcards.json");
    }

    private string GetFlashcardSetsFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "flashcard-sets.json");
    }

    private string GetMindMapsFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "mind-maps.json");
    }

    private string GetQuizzesFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "quizzes.json");
    }

    public void DuplicateQuiz(QuizViewModel sourceQuiz)
    {
        if (sourceQuiz is null)
            return;

        // Create a deep copy of the quiz document
        var newDocument = CloneQuizDocument(sourceQuiz.Document);

        AddOrUpdateQuiz(newDocument);
    }

    private static QuizDocument CloneQuizDocument(QuizDocument source)
    {
        return new QuizDocument
        {
            Id = Guid.NewGuid(), // New ID for the copy
            Title = $"{source.Title} (Copy)",
            Tags = source.Tags.ToList(),
            Questions = source.Questions.Select(CloneQuizQuestion).ToList(),
            CreatedAt = DateTime.UtcNow,
            LastModified = DateTime.Now,
            AiModelDisplayName = source.AiModelDisplayName,
            SourceNoteId = source.SourceNoteId,
            GroupId = source.GroupId,
            Schedules = source.Schedules?.Select(schedule => new NoteScheduleEntry
            {
                ScheduledAt = schedule.ScheduledAt,
                Note = schedule.Note
            }).ToList() ?? new List<NoteScheduleEntry>()
        };
    }

    private static QuizQuestion CloneQuizQuestion(QuizQuestion source)
    {
        return new QuizQuestion
        {
            Type = source.Type,
            Question = source.Question,
            Options = source.Options.Select(o => new QuizOption
            {
                Text = o.Text,
                IsCorrect = o.IsCorrect
            }).ToList(),
            Explanation = source.Explanation,
            Hint = source.Hint,
            SetIndex = source.SetIndex,
            PlainTextAnswer = source.PlainTextAnswer,
            PlainTextCharLimit = source.PlainTextCharLimit,
            PlainTextMatchCase = source.PlainTextMatchCase,
            PlainTextIgnorePunctuation = source.PlainTextIgnorePunctuation
        };
    }

    private bool _isCalendarFirst = true;
    public bool IsCalendarFirst
    {
        get => _isCalendarFirst;
        set
        {
            if (SetProperty(ref _isCalendarFirst, value))
            {
                CommandManager.InvalidateRequerySuggested();
                SaveAppSettings();
            }
        }
    }

    private bool _isCalendarSectionVisible = true;
    public bool IsCalendarSectionVisible
    {
        get => _isCalendarSectionVisible;
        set
        {
            if (SetProperty(ref _isCalendarSectionVisible, value))
                SaveAppSettings();
        }
    }

    private bool _isRecentSectionVisible = true;
    public bool IsRecentSectionVisible
    {
        get => _isRecentSectionVisible;
        set
        {
            if (SetProperty(ref _isRecentSectionVisible, value))
                SaveAppSettings();
        }
    }

    private bool _isCalendarSectionExpanded = true;
    public bool IsCalendarSectionExpanded
    {
        get => _isCalendarSectionExpanded;
        set
        {
            if (SetProperty(ref _isCalendarSectionExpanded, value))
                SaveAppSettings();
        }
    }

    private bool _isGroupsSectionVisible = true;
    public bool IsGroupsSectionVisible
    {
        get => _isGroupsSectionVisible;
        set
        {
            if (SetProperty(ref _isGroupsSectionVisible, value))
                SaveAppSettings();
        }
    }

    private bool _isUngroupedSectionVisible = true;
    private bool _isNotesDashboardSectionVisible = true;
    private bool _isNotesDashboardSectionExpanded = true;
    private bool _isFlashcardGroupsSectionVisible = true;
    private bool _isFlashcardDashboardGroupsSectionVisible = true;
    private bool _isFlashcardDashboardUngroupedSectionVisible = true;
    private bool _isFlashcardGroupsSectionExpanded = true;
    private bool _isFlashcardDashboardGroupsSectionExpanded = true;
    private bool _isFlashcardDashboardUngroupedSectionExpanded = true;
    private bool _isFlashcardDashboardGroupsFirst = true;
    private bool _isMindMapGroupsSectionVisible = true;
    private bool _isMindMapDashboardGroupsSectionVisible = true;
    private bool _isMindMapDashboardUngroupedSectionVisible = true;
    private bool _isQuizGroupsSectionVisible = true;
    private bool _isQuizDashboardGroupsSectionVisible = true;
    private bool _isQuizDashboardUngroupedSectionVisible = true;
    private bool _isMindMapGroupsSectionExpanded = true;
    private bool _isMindMapDashboardGroupsSectionExpanded = true;
    private bool _isMindMapDashboardUngroupedSectionExpanded = true;
    private bool _isMindMapDashboardGroupsFirst = true;
    private bool _isQuizGroupsSectionExpanded = true;
    private bool _isQuizDashboardGroupsSectionExpanded = true;
    private bool _isQuizDashboardUngroupedSectionExpanded = true;
    private bool _isQuizDashboardGroupsFirst = true;
    public bool IsUngroupedSectionVisible
    {
        get => _isUngroupedSectionVisible;
        set
        {
            if (SetProperty(ref _isUngroupedSectionVisible, value))
                SaveAppSettings();
        }
    }

    public bool IsNotesDashboardSectionVisible
    {
        get => _isNotesDashboardSectionVisible;
        set
        {
            if (SetProperty(ref _isNotesDashboardSectionVisible, value))
            {
                if (!value)
                {
                    IsGroupsSectionVisible = false;
                    IsUngroupedSectionVisible = false;
                }

                SaveAppSettings();
            }
        }
    }

    public bool IsFlashcardGroupsSectionVisible
    {
        get => _isFlashcardGroupsSectionVisible;
        set
        {
            if (SetProperty(ref _isFlashcardGroupsSectionVisible, value))
            {
                if (!value)
                {
                    IsFlashcardDashboardGroupsSectionVisible = false;
                    IsFlashcardDashboardUngroupedSectionVisible = false;
                }

                ApplyFlashcardFilters();
                SaveAppSettings();
            }
        }
    }

    public bool IsFlashcardDashboardGroupsSectionVisible
    {
        get => _isFlashcardDashboardGroupsSectionVisible;
        set
        {
            if (SetProperty(ref _isFlashcardDashboardGroupsSectionVisible, value))
                SaveAppSettings();
        }
    }

    public bool IsFlashcardDashboardUngroupedSectionVisible
    {
        get => _isFlashcardDashboardUngroupedSectionVisible;
        set
        {
            if (SetProperty(ref _isFlashcardDashboardUngroupedSectionVisible, value))
                SaveAppSettings();
        }
    }

    public bool IsMindMapGroupsSectionVisible
    {
        get => _isMindMapGroupsSectionVisible;
        set
        {
            if (SetProperty(ref _isMindMapGroupsSectionVisible, value))
            {
                if (!value)
                {
                    IsMindMapDashboardGroupsSectionVisible = false;
                    IsMindMapDashboardUngroupedSectionVisible = false;
                }

                ApplyMindMapFilters();
                SaveAppSettings();
            }
        }
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            var normalized = LocalizationService.NormalizeLanguage(value);
            if (_selectedLanguage != normalized)
            {
                _selectedLanguage = normalized;
                OnPropertyChanged(nameof(SelectedLanguage));
                LocalizationService.SetCulture(_selectedLanguage);
                RefreshSortOptions();
                RefreshFlashcardSortOptions();
                RefreshMindMapSortOptions();
                RefreshQuizSortOptions();
                OnPropertyChanged(nameof(SortButtonText));
                OnPropertyChanged(nameof(ActiveSortButtonText));
                OnPropertyChanged(nameof(UserActivitySummaryTitle));
                OnPropertyChanged(nameof(CalendarSelectedDateDisplay));
                RefreshActivityStats();
                SaveAppSettings();
            }
        }
    }

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            var normalized = ThemeManager.NormalizeThemePreference(value);
            if (_selectedTheme != normalized)
            {
                _selectedTheme = normalized;
                OnPropertyChanged(nameof(SelectedTheme));
                ThemeManager.SetTheme(_selectedTheme);
                SaveAppSettings();
            }
        }
    }
    public object CurrentView { get; set; }
    public string ActiveDashboard => _activeDashboard;
    public bool IsNotesView => string.Equals(_activeDashboard, DashboardNotes, StringComparison.Ordinal);
    public bool IsFlashcardsView
    {
        get => string.Equals(_activeDashboard, DashboardFlashcards, StringComparison.Ordinal);
        set => SetActiveDashboard(value ? DashboardFlashcards : DashboardNotes);
    }
    public bool IsMindMapsView => string.Equals(_activeDashboard, DashboardMindMaps, StringComparison.Ordinal);
    public bool IsQuizzesView => string.Equals(_activeDashboard, DashboardQuizzes, StringComparison.Ordinal);
    private string _newFlashcardAnswer = "";
    private string _newFlashcardQuestion = "";

    private void SetActiveDashboard(string? dashboard)
    {
        var normalized = NormalizeDashboard(dashboard);
        if (string.Equals(_activeDashboard, normalized, StringComparison.Ordinal))
            return;

        if (IsMassSelectMode)
            ExitMassSelect();

        _activeDashboard = normalized;
        CurrentView = normalized;
        OnPropertyChanged(nameof(ActiveDashboard));
        OnPropertyChanged(nameof(CurrentView));
        OnPropertyChanged(nameof(IsNotesView));
        OnPropertyChanged(nameof(IsFlashcardsView));
        OnPropertyChanged(nameof(IsMindMapsView));
        OnPropertyChanged(nameof(IsQuizzesView));
        NotifyActiveDashboardChromeChanged();
        EnsureDashboardPersistedContentLoaded(normalized);
        SaveAppSettings();
    }


    public MainViewModel(
        AppSettings? startupSettings = null,
        bool loadPersistedContent = true,
        bool applyThemeAndCulture = true)
    {
        ApplyAppSettings(startupSettings ?? AppSettingsService.Load(), applyThemeAndCulture);

        Notes = new ObservableCollection<NoteCardViewModel>();
        NoteGroups = new ObservableCollection<NoteGroupViewModel>();
        TagFilters = new ObservableCollection<TagFilterItemViewModel>();
        FlashcardTagFilters = new ObservableCollection<TagFilterItemViewModel>();
        MindMapTagFilters = new ObservableCollection<TagFilterItemViewModel>();
        QuizTagFilters = new ObservableCollection<TagFilterItemViewModel>();
        SortOptions = new ObservableCollection<NoteSortOptionItemViewModel>();
        FlashcardSortOptions = new ObservableCollection<NoteSortOptionItemViewModel>();
        MindMapSortOptions = new ObservableCollection<NoteSortOptionItemViewModel>();
        QuizSortOptions = new ObservableCollection<NoteSortOptionItemViewModel>();
        CalendarScheduledNotes = new ObservableCollection<CalendarScheduledItemViewModel>();
        // Create a view for Notes so we can apply filtering for search
        _notesView = CollectionViewSource.GetDefaultView(Notes);
        _notesView.Filter = FilterUngroupedNotes;
        ApplySortToUngroupedView();
        _flashcardSetsView = CollectionViewSource.GetDefaultView(FlashcardSets);
        _flashcardSetsView.Filter = FilterFlashcardSet;
        ApplySortToFlashcardSetsView();
        _mindMapsView = CollectionViewSource.GetDefaultView(MindMaps);
        _mindMapsView.Filter = FilterMindMap;
        ApplySortToMindMapsView();
        _quizzesView = CollectionViewSource.GetDefaultView(Quizzes);
        _quizzesView.Filter = FilterQuiz;
        ApplySortToQuizzesView();
        Notes.CollectionChanged += OnNotesCollectionChanged;
        FlashcardSets.CollectionChanged += OnFlashcardSetsCollectionChanged;
        MindMaps.CollectionChanged += OnMindMapsCollectionChanged;
        Quizzes.CollectionChanged += OnQuizzesCollectionChanged;
        
        Scriblucent.Services.ActivityTracker.ActivityUpdated += RefreshActivityStats;

        AddNoteCommand = new RelayCommand(AddNote, () => !IsMassSelectMode);
        ToggleSidebarCommand = new RelayCommand(ToggleSidebar);
        ClearTagFiltersCommand = new RelayCommand(ClearActiveTagFilters, () => ActiveHasActiveTagFilters);
        ExitMassSelectCommand = new RelayCommand(ExitMassSelect, () => IsMassSelectMode);
        SelectAllVisibleNotesCommand = new RelayCommand(SelectAllVisibleMassItems, () => IsMassSelectMode);
        DeleteSelectedNotesCommand = new RelayCommand(DeleteSelectedMassItems, () => IsMassSelectMode && ActiveMassSelectedCount > 0);
        RemoveSelectedFromGroupsCommand = new RelayCommand(RemoveSelectedFromGroups, CanUngroupSelectedNotes);
        GroupSelectedNotesCommand = new RelayCommand(GroupSelectedNotes, CanGroupSelectedNotes);
        PinSelectedNotesCommand = new RelayCommand(PinSelectedNotes, CanPinSelectedNotes);
        UnpinSelectedNotesCommand = new RelayCommand(UnpinSelectedNotes, CanUnpinSelectedNotes);
        DuplicateSelectedNotesCommand = new RelayCommand(DuplicateSelectedNotes, CanDuplicateSelectedNotes);
        CurrentView = _activeDashboard;

        ShowNotesCommand = new RelayCommand(() =>
        {
            SetActiveDashboard(DashboardNotes);
        });

        ShowFlashcardsCommand = new RelayCommand(() =>
        {
            SetActiveDashboard(DashboardFlashcards);
        });
        ShowMindMapsCommand = new RelayCommand(() =>
        {
            SetActiveDashboard(DashboardMindMaps);
        });
        ShowQuizzesCommand = new RelayCommand(() =>
        {
            SetActiveDashboard(DashboardQuizzes);
        });
        MoveGroupsUpCommand = new RelayCommand(() => { });
        MoveGroupsDownCommand = new RelayCommand(() => { });
        MoveUngroupedUpCommand = new RelayCommand(() => { });
        MoveUngroupedDownCommand = new RelayCommand(() => { });
        ToggleGroupsSectionCommand = new RelayCommand(() => { });
        ToggleRecentSectionCommand = new RelayCommand(() => { });
        ToggleUngroupedSectionCommand = new RelayCommand(() => { });


        AddFlashcardCommand = new RelayCommand(AddFlashcard);

        SetActiveDashboard(_settings!.LastView);

        if (loadPersistedContent)
            LoadPersistedContent();
    }

    public void LoadPersistedContent()
    {
        EnsureDashboardPersistedContentLoaded(_activeDashboard);
        ScheduleDeferredDashboardLoads();
        ApplyAutoSaveSettings(_settings);
        Application.Current?.Dispatcher.BeginInvoke(RefreshStartupSortOptions, DispatcherPriority.Background);
    }

    private void RefreshStartupSortOptions()
    {
        RefreshSortOptions();
        RefreshFlashcardSortOptions();
        RefreshMindMapSortOptions();
        RefreshQuizSortOptions();
    }

    private void ScheduleDeferredDashboardLoads()
    {
        if (_deferredDashboardLoadsScheduled)
            return;

        _deferredDashboardLoadsScheduled = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            LoadDeferredDashboardPersistedContent();
            return;
        }

        dispatcher.BeginInvoke(LoadDeferredDashboardPersistedContent, DispatcherPriority.ApplicationIdle);
    }

    private void LoadDeferredDashboardPersistedContent()
    {
        foreach (var dashboard in new[] { DashboardNotes, DashboardFlashcards, DashboardMindMaps, DashboardQuizzes })
        {
            if (!string.Equals(dashboard, _activeDashboard, StringComparison.Ordinal))
                EnsureDashboardPersistedContentLoaded(dashboard);
        }
    }

    private void EnsureDashboardPersistedContentLoaded(string dashboard)
    {
        switch (NormalizeDashboard(dashboard))
        {
            case DashboardNotes:
                LoadNotesPersistedContent();
                break;
            case DashboardFlashcards:
                LoadFlashcardsPersistedContent();
                break;
            case DashboardMindMaps:
                LoadMindMapsPersistedContent();
                break;
            case DashboardQuizzes:
                LoadQuizzesPersistedContent();
                break;
        }
    }

    private void LoadNotesPersistedContent()
    {
        if (_notesPersistedContentLoaded)
            return;

        _notesPersistedContentLoaded = true;

        // The notes store can be several MB (thousands of notes); read+parse it off the UI
        // thread so the app stays responsive right after the window first appears, then apply
        // the result back on the UI thread exactly as the old synchronous path did.
        var path = GetNotesFilePath();
        Task.Run(() => ReadNotesFromDisk(path)).ContinueWith(task =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => ApplyLoadedNotes(task.Result), DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private void ApplyLoadedNotes(NotesLoadResult result)
    {
        if (result.Status == NotesLoadStatus.ParseError)
        {
            // The file exists but failed to parse - do NOT create a starter note and save over it,
            // that would permanently discard whatever real notes were on disk. Leave Notes empty for
            // this session and tell the user instead of silently destroying their library.
            var message = result.BackupPath is { } backupPath
                ? string.Format(LocalizationService.GetString("NotesLoadFailedWithBackupFormat"), backupPath)
                : LocalizationService.GetString("NotesLoadFailedNoBackup");
            ModernMessageBox.Show(message, LocalizationService.GetString("NotesLoadFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            // Don't mark the load complete - that would let the autosave timer see an empty
            // Notes collection as "loaded" and silently overwrite notes.json with nothing a
            // few seconds later, destroying the very data this warning is about.
            FinishNotesLoadInitialization(markLoadComplete: false);
            return;
        }

        if (result.Docs.Count == 0)
        {
            var starterDocument = new NoteDocument
            {
                Title = LocalizationService.GetString("FirstNoteTitle"),
                Content = LocalizationService.GetString("FirstNoteContent")
            };
            Notes.Add(CreateNoteCard(starterDocument));
            SaveNotes();
            FinishNotesLoadInitialization();
            return;
        }

        _groupMetadata.Clear();
        foreach (var metadata in result.Groups)
            _groupMetadata[metadata.GroupId] = metadata;

        Notes.Clear();
        _notesLoadComplete = false;
        LoadNotesInBatches(result.Docs);
    }

    private void LoadFlashcardsPersistedContent()
    {
        if (_flashcardsPersistedContentLoaded)
            return;

        _flashcardsPersistedContentLoaded = true;
        _flashcardDataLoadInProgress = true;

        var setsPath = GetFlashcardSetsFilePath();
        var legacyPath = GetFlashcardsFilePath();
        Task.Run(() => ReadFlashcardsFromDisk(setsPath, legacyPath)).ContinueWith(task =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ApplyLoadedFlashcards(task.Result);
                }
                finally
                {
                    _flashcardDataLoadInProgress = false;
                }

                MarkFlashcardsDashboardReady();
            }, DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private void LoadMindMapsPersistedContent()
    {
        if (_mindMapsPersistedContentLoaded)
            return;

        _mindMapsPersistedContentLoaded = true;
        _mindMapDataLoadInProgress = true;

        var path = GetMindMapsFilePath();
        Task.Run(() => ReadMindMapsFromDisk(path)).ContinueWith(task =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ApplyLoadedMindMaps(task.Result);
                }
                finally
                {
                    _mindMapDataLoadInProgress = false;
                }

                MarkMindMapsDashboardReady();
            }, DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private void LoadQuizzesPersistedContent()
    {
        if (_quizzesPersistedContentLoaded)
            return;

        _quizzesPersistedContentLoaded = true;
        _quizDataLoadInProgress = true;

        var path = GetQuizzesFilePath();
        Task.Run(() => ReadQuizzesFromDisk(path)).ContinueWith(task =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ApplyLoadedQuizzes(task.Result);
                }
                finally
                {
                    _quizDataLoadInProgress = false;
                }

                MarkQuizzesDashboardReady();
            }, DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }
    private void AddFlashcard()
    {
        Flashcards.Add(new FlashcardItem
        {
            Question = NewFlashcardQuestion,
            Answer = NewFlashcardAnswer
        });

        NewFlashcardQuestion = "";
        NewFlashcardAnswer = "";

        SaveFlashcards();
    }

    // Shared across the Flashcards/MindMaps/Quizzes background-load result types below: captures
    // exactly what each format's original synchronous load path did to its group-metadata dictionary
    // on a given parse outcome (some parse-failure branches deliberately left the dictionary untouched
    // rather than clearing it - preserved here rather than "simplified" so behavior doesn't change).
    private enum GroupMetadataSyncAction
    {
        Leave,
        ClearAndSet,
        ClearOnly
    }

    private readonly record struct FlashcardsLoadResult(
        bool UsedNewFormat,
        List<FlashcardSetDocument> Sets,
        List<FlashcardSetGroupData> Groups,
        GroupMetadataSyncAction GroupMetadataAction,
        List<FlashcardItem> LegacyItems);

    // Pure read - runs off the UI thread via Task.Run in LoadFlashcardsPersistedContent. Must not
    // touch any instance/UI-bound state; ApplyLoadedFlashcards below applies the result on the UI
    // thread. Mirrors ReadNotesFromDisk/ApplyLoadedNotes.
    private static FlashcardsLoadResult ReadFlashcardsFromDisk(string setsPath, string legacyPath)
    {
        if (File.Exists(setsPath))
        {
            var json = File.ReadAllText(setsPath);
            List<FlashcardSetDocument> sets;
            List<FlashcardSetGroupData> groups = new();
            GroupMetadataSyncAction metadataAction;
            try
            {
                var store = JsonSerializer.Deserialize<FlashcardSetStoreData>(json);
                if (store?.Sets != null)
                {
                    sets = store.Sets;
                    groups = store.Groups ?? new List<FlashcardSetGroupData>();
                    metadataAction = GroupMetadataSyncAction.ClearAndSet;
                }
                else
                {
                    sets = JsonSerializer.Deserialize<List<FlashcardSetDocument>>(json) ?? new();
                    metadataAction = GroupMetadataSyncAction.Leave;
                }
            }
            catch
            {
                try
                {
                    sets = JsonSerializer.Deserialize<List<FlashcardSetDocument>>(json) ?? new();
                    metadataAction = GroupMetadataSyncAction.Leave;
                }
                catch
                {
                    sets = new List<FlashcardSetDocument>();
                    metadataAction = GroupMetadataSyncAction.Leave;
                }
            }

            return new FlashcardsLoadResult(true, sets, groups, metadataAction, new List<FlashcardItem>());
        }

        if (File.Exists(legacyPath))
        {
            var json = File.ReadAllText(legacyPath);
            var data = JsonSerializer.Deserialize<List<FlashcardItem>>(json) ?? new();
            return new FlashcardsLoadResult(false, new List<FlashcardSetDocument>(), new List<FlashcardSetGroupData>(), GroupMetadataSyncAction.Leave, data);
        }

        return new FlashcardsLoadResult(false, new List<FlashcardSetDocument>(), new List<FlashcardSetGroupData>(), GroupMetadataSyncAction.Leave, new List<FlashcardItem>());
    }

    private void ApplyLoadedFlashcards(FlashcardsLoadResult result)
    {
        FlashcardSets.Clear();

        if (result.UsedNewFormat)
        {
            switch (result.GroupMetadataAction)
            {
                case GroupMetadataSyncAction.ClearAndSet:
                    _flashcardSetGroupMetadata.Clear();
                    foreach (var meta in result.Groups)
                        _flashcardSetGroupMetadata[meta.GroupId] = meta;
                    break;
                case GroupMetadataSyncAction.ClearOnly:
                    _flashcardSetGroupMetadata.Clear();
                    break;
                case GroupMetadataSyncAction.Leave:
                    break;
            }

            foreach (var set in result.Sets
                         .Where(set => set != null)
                         .OrderByDescending(set => set.IsPinned)
                         .ThenByDescending(set => set.LastModified)
                         .ThenBy(set => set.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                NormalizeFlashcardSetDocument(set);
                FlashcardSets.Add(new FlashcardSetViewModel(set));
            }

            NormalizeFlashcardSetGroups();
            RebuildFlashcardSetGroups();
            RefreshAvailableFlashcardTags();
            NotifyFlashcardSetsChanged();
            _flashcardsLoadCompleted = true;
            return;
        }

        if (result.LegacyItems.Count > 0)
        {
            Flashcards = new ObservableCollection<FlashcardItem>(result.LegacyItems);
            AddOrUpdateFlashcardSet(new FlashcardSetDocument
            {
                Title = LocalizationService.GetString("FlashcardsPreviewTitle"),
                Cards = result.LegacyItems.ToList(),
                Tags = new List<string>(),
                CreatedAt = DateTime.UtcNow,
                LastModified = DateTime.Now
            });
        }
        else
        {
            Flashcards = new ObservableCollection<FlashcardItem>();
        }

        RefreshAvailableFlashcardTags();
        NotifyFlashcardSetsChanged();
        _flashcardsLoadCompleted = true;
    }
    private void SaveFlashcards()
    {
        var path = GetFlashcardsFilePath();
        var json = JsonSerializer.Serialize(Flashcards);
        File.WriteAllText(path, json);
    }

    public FlashcardSetViewModel AddOrUpdateFlashcardSet(FlashcardSetDocument document)
    {
        NormalizeFlashcardSetDocument(document);

        var existing = FlashcardSets.FirstOrDefault(set => set.Document.Id == document.Id);
        var isNew = existing is null;
        var hadFlashcardSets = FlashcardSets.Count > 0;
        if (isNew)
        {
            var newSetVm = new FlashcardSetViewModel(document);
            if (!hadFlashcardSets)
            {
                FlashcardSets.Add(newSetVm);
                OnPropertyChanged(nameof(HasFlashcardSets));
                if (!document.GroupId.HasValue && MatchesFlashcardSetFilters(newSetVm))
                    NotifyHasUngroupedFlashcardSetsChanged();

                SaveFlashcardSets();
                MotionHost?.PlayDashboardContentReveal(DashboardMotionScope.Flashcards);
            }
            else
            {
                MotionHost?.PrepareMutation(DashboardMotionScope.Flashcards);
                MotionHost?.PrepareItemAdd(DashboardMotionScope.Flashcards, document.Id);
                FlashcardSets.Add(newSetVm);

                if (document.GroupId.HasValue)
                    RebuildFlashcardSetGroups();

                if (!document.GroupId.HasValue && MatchesFlashcardSetFilters(newSetVm))
                    NotifyHasUngroupedFlashcardSetsChanged();

                SaveFlashcardSets();
                MotionHost?.CompleteItemAdd(DashboardMotionScope.Flashcards, document.Id);
            }

            QueueFlashcardMutationReaction();
            return newSetVm;
        }

        var setVm = existing!;
        setVm.Document.Title = document.Title;
        setVm.Document.Tags = document.Tags;
        setVm.Document.SetNames = document.SetNames;
        setVm.Document.Cards = document.Cards;
        setVm.Document.StudySession = document.StudySession;
        setVm.Document.CreatedAt = document.CreatedAt;
        setVm.Document.LastModified = document.LastModified;
        setVm.Document.AiModelDisplayName = document.AiModelDisplayName;
        setVm.Document.SourceNoteId = document.SourceNoteId;
        setVm.Document.GroupId = document.GroupId;
        setVm.Document.Schedules = document.Schedules;
        setVm.Document.IsPinned = document.IsPinned;
        setVm.Document.FlipDelayMilliseconds = document.FlipDelayMilliseconds;
        setVm.NotifyChanged();

        ReorderFlashcardSets();
        NormalizeFlashcardSetGroups();
        RebuildFlashcardSetGroups();
        RefreshAvailableFlashcardTags();
        ApplyFlashcardFilters();
        SaveFlashcardSets();
        NotifyFlashcardSetsChanged();
        RefreshRecentFlashcardSets();

        return setVm;
    }

    public void DeleteFlashcardSet(FlashcardSetViewModel set)
    {
        if (set is null)
            return;

        FlashcardSets.Remove(set);
        ApplyFlashcardSetDeleteReaction();
    }

    private void ApplyFlashcardSetDeleteReaction()
    {
        NormalizeFlashcardSetGroups();
        RebuildFlashcardSetGroups();
        SaveFlashcardSets();
        RefreshAvailableFlashcardTags();
        NotifyFlashcardSetsChanged();
        RefreshRecentFlashcardSets();
        RefreshCalendarScheduledNotes();
        EnsureMassSelectionConsistency();
    }

    public void SaveFlashcardSets()
    {
        try
        {
            var path = GetFlashcardSetsFilePath();
            var documents = FlashcardSets
                .Select(set => set.Document)
                .OrderByDescending(set => set.IsPinned)
                .ThenByDescending(set => set.LastModified)
                .ThenBy(set => set.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var store = new FlashcardSetStoreData
            {
                Sets = documents,
                Groups = _flashcardSetGroupMetadata.Values
                    .OrderBy(g => g.SortOrder ?? int.MaxValue)
                    .ThenBy(g => g.Name)
                    .ToList()
            };

            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = false });
            AtomicFileWriter.WriteAllText(path, json);
        }
        catch
        {
            // Persistence errors are non-fatal; ignored.
        }
    }

    private void ReorderFlashcardSets()
    {
        var ordered = FlashcardSets
            .OrderByDescending(set => set.Document.IsPinned)
            .ThenByDescending(set => set.Document.LastModified)
            .ThenBy(set => set.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _flashcardSetsReorderInProgress = true;
        try
        {
            FlashcardSets.Clear();
            foreach (var set in ordered)
                FlashcardSets.Add(set);
        }
        finally
        {
            _flashcardSetsReorderInProgress = false;
        }
    }

    private void NotifyFlashcardSetsChanged()
    {
        OnPropertyChanged(nameof(HasFlashcardSets));
        OnPropertyChanged(nameof(HasFlashcardSetGroups));
        NotifyHasUngroupedFlashcardSetsChanged();
        OnPropertyChanged(nameof(FlashcardSetCount));
        OnPropertyChanged(nameof(FlashcardSetCountText));
        NotifyFlashcardsDashboardPresentationChanged();
        RefreshRecentFlashcardSets();
    }

    private void NormalizeFlashcardSetGroups()
    {
        var grouped = FlashcardSets
            .Where(set => set.Document.GroupId.HasValue)
            .GroupBy(set => set.Document.GroupId!.Value)
            .ToList();

        foreach (var group in grouped.Where(group => group.Count() < 2))
        {
            foreach (var set in group)
            {
                set.Document.GroupId = null;
                set.NotifyChanged();
            }

            _flashcardSetGroupMetadata.Remove(group.Key);
        }
    }

    public void RebuildFlashcardSetGroups()
    {
        var grouped = FlashcardSets
            .Where(set => set.Document.GroupId.HasValue)
            .GroupBy(set => set.Document.GroupId!.Value)
            .OrderBy(group => EnsureFlashcardSetGroupMetadata(group.Key).SortOrder ?? int.MaxValue)
            .ThenByDescending(group => group.Max(set => set.Document.LastModified))
            .ToList();

        var desiredGroups = new List<FlashcardSetGroupViewModel>();
        foreach (var group in grouped)
        {
            var visibleSets = group
                .Where(MatchesFlashcardSetFilters)
                .OrderByDescending(set => set.Document.IsPinned)
                .ThenByDescending(set => set.Document.LastModified)
                .ThenBy(set => set.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (visibleSets.Count == 0)
                continue;

            var metadata = EnsureFlashcardSetGroupMetadata(group.Key);
            desiredGroups.Add(new FlashcardSetGroupViewModel(group.Key, metadata.Name, metadata.BackgroundColor, visibleSets));
        }

        var desiredIds = desiredGroups.Select(group => group.GroupId).ToHashSet();
        var removingIds = FlashcardSetGroups
            .Where(group => !desiredIds.Contains(group.GroupId))
            .Select(group => group.GroupId)
            .ToList();

        if (TryAnimateGroupRemovals(DashboardMotionScope.Flashcards, removingIds, () => SyncFlashcardSetGroups(desiredGroups)))
            return;

        SyncFlashcardSetGroups(desiredGroups);
    }

    private void SyncFlashcardSetGroups(IReadOnlyList<FlashcardSetGroupViewModel> desiredGroups)
    {
        var desiredIds = desiredGroups.Select(group => group.GroupId).ToHashSet();
        for (var i = FlashcardSetGroups.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(FlashcardSetGroups[i].GroupId))
                FlashcardSetGroups.RemoveAt(i);
        }

        for (var i = 0; i < desiredGroups.Count; i++)
        {
            var desired = desiredGroups[i];
            var existing = FlashcardSetGroups.FirstOrDefault(group => group.GroupId == desired.GroupId);
            if (existing is null)
            {
                FlashcardSetGroups.Insert(Math.Min(i, FlashcardSetGroups.Count), desired);
                continue;
            }

            if (FlashcardSetGroups.IndexOf(existing) != i)
                FlashcardSetGroups.Move(FlashcardSetGroups.IndexOf(existing), i);

            SyncObservableCollectionOrder(existing.Sets, desired.Sets.ToList());
        }

        OnPropertyChanged(nameof(HasFlashcardSetGroups));
        NotifyHasUngroupedFlashcardSetsChanged();
    }

    private FlashcardSetGroupData EnsureFlashcardSetGroupMetadata(Guid groupId)
    {
        if (_flashcardSetGroupMetadata.TryGetValue(groupId, out var metadata))
        {
            metadata.SortOrder ??= GetNextFlashcardSetGroupSortOrder();
            return metadata;
        }

        metadata = new FlashcardSetGroupData
        {
            GroupId = groupId,
            Name = string.Format(LocalizationService.GetString("GroupTitleFormat"), groupId.ToString()[..4].ToUpperInvariant()),
            BackgroundColor = DefaultGroupBackground,
            SortOrder = GetNextFlashcardSetGroupSortOrder()
        };
        _flashcardSetGroupMetadata[groupId] = metadata;
        return metadata;
    }

    private int GetNextFlashcardSetGroupSortOrder()
    {
        return _flashcardSetGroupMetadata.Values
            .Select(group => group.SortOrder ?? -1)
            .DefaultIfEmpty(-1)
            .Max() + 1;
    }

    public bool TryGroupFlashcardSets(FlashcardSetViewModel draggedSet, FlashcardSetViewModel targetSet)
    {
        if (draggedSet is null || targetSet is null || ReferenceEquals(draggedSet, targetSet))
            return false;

        var targetGroupId = targetSet.Document.GroupId ?? draggedSet.Document.GroupId ?? Guid.NewGuid();
        if (draggedSet.Document.GroupId == targetGroupId && targetSet.Document.GroupId == targetGroupId)
            return false;

        var movingIds = BuildGroupMergeMoveIds(
            draggedSet.Document.Id,
            targetSet.Document.Id,
            draggedSet.Document.GroupId,
            targetSet.Document.GroupId,
            targetGroupId);

        void CommitGroup()
        {
            EnsureFlashcardSetGroupMetadata(targetGroupId);

            foreach (var set in new[] { draggedSet, targetSet })
            {
                if (set.Document.GroupId == targetGroupId)
                    continue;

                set.Document.GroupId = targetGroupId;
                set.NotifyChanged();
            }

            NormalizeFlashcardSetGroups();
            RebuildFlashcardSetGroups();
            ApplyFlashcardFilters();
            SaveFlashcardSets();
        }

        if (TryRequestGroupMergeMove(DashboardMotionScope.Flashcards, movingIds, CommitGroup))
            return true;

        CommitGroup();
        return true;
    }

    public bool TryReorderFlashcardSetsWithinGroup(FlashcardSetViewModel dragged, FlashcardSetViewModel target, bool placeAfter)
    {
        if (ReferenceEquals(dragged, target)) return false;
        var groupId = dragged.Document.GroupId;
        if (!groupId.HasValue || target.Document.GroupId != groupId) return false;

        var draggedIdx = FlashcardSets.IndexOf(dragged);
        var targetIdx = FlashcardSets.IndexOf(target);
        if (draggedIdx < 0 || targetIdx < 0) return false;

        var newIdx = placeAfter ? targetIdx + 1 : targetIdx;
        if (draggedIdx < newIdx) newIdx--;
        if (newIdx == draggedIdx) return false;

        FlashcardSets.Move(draggedIdx, Math.Clamp(newIdx, 0, FlashcardSets.Count - 1));
        RebuildFlashcardSetGroups();
        SaveFlashcardSets();
        return true;
    }

    public bool TryMoveFlashcardSetToGroup(FlashcardSetViewModel draggedSet, FlashcardSetGroupViewModel targetGroup)
    {
        if (draggedSet.Document.GroupId == targetGroup.GroupId)
            return false;

        var setId = draggedSet.Document.Id;
        void CommitMove()
        {
            draggedSet.Document.GroupId = targetGroup.GroupId;
            EnsureFlashcardSetGroupMetadata(targetGroup.GroupId);
            draggedSet.NotifyChanged();
            NormalizeFlashcardSetGroups();
            RebuildFlashcardSetGroups();
            _flashcardSetsView.Refresh();
            SaveFlashcardSets();
            RefreshRecentFlashcardSets();
            NotifyFlashcardSetsChanged();
        }

        if (TryRequestItemMove(DashboardMotionScope.Flashcards, setId, CommitMove))
            return true;

        CommitMove();
        return true;
    }

    public bool MoveFlashcardSetGroupUp(FlashcardSetGroupViewModel group)
    {
        return TryMoveFlashcardSetGroup(group, moveUp: true);
    }

    public bool MoveFlashcardSetGroupDown(FlashcardSetGroupViewModel group)
    {
        return TryMoveFlashcardSetGroup(group, moveUp: false);
    }

    private bool TryMoveFlashcardSetGroup(FlashcardSetGroupViewModel group, bool moveUp)
    {
        var currentIndex = FlashcardSetGroups.IndexOf(group);
        var targetIndex = moveUp ? currentIndex - 1 : currentIndex + 1;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= FlashcardSetGroups.Count)
            return false;

        var target = FlashcardSetGroups[targetIndex];
        var currentMeta = EnsureFlashcardSetGroupMetadata(group.GroupId);
        var targetMeta = EnsureFlashcardSetGroupMetadata(target.GroupId);
        (currentMeta.SortOrder, targetMeta.SortOrder) = (targetMeta.SortOrder, currentMeta.SortOrder);
        FlashcardSetGroups.Move(currentIndex, targetIndex);
        SaveFlashcardSets();
        return true;
    }

    public bool RenameFlashcardSetGroup(FlashcardSetGroupViewModel group, string newName)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        var metadata = EnsureFlashcardSetGroupMetadata(group.GroupId);
        metadata.Name = trimmed;
        group.Name = trimmed;
        SaveFlashcardSets();
        return true;
    }

    public bool SetFlashcardSetGroupBackgroundColor(FlashcardSetGroupViewModel group, string backgroundColor)
    {
        if (string.IsNullOrWhiteSpace(backgroundColor))
            return false;

        var metadata = EnsureFlashcardSetGroupMetadata(group.GroupId);
        metadata.BackgroundColor = backgroundColor;
        group.SetBackground(backgroundColor);
        SaveFlashcardSets();
        return true;
    }

    public void DisbandFlashcardSetGroup(FlashcardSetGroupViewModel group, bool deleteSets)
    {
        var setsInGroup = FlashcardSets.Where(set => set.Document.GroupId == group.GroupId).ToList();
        if (deleteSets)
        {
            foreach (var set in setsInGroup)
                FlashcardSets.Remove(set);
        }
        else
        {
            foreach (var set in setsInGroup)
            {
                set.Document.GroupId = null;
                set.NotifyChanged();
            }
        }

        _flashcardSetGroupMetadata.Remove(group.GroupId);
        NormalizeFlashcardSetGroups();
        RebuildFlashcardSetGroups();
        ApplyFlashcardFilters();
        SaveFlashcardSets();
    }

    public void RemoveFlashcardSetFromGroup(FlashcardSetViewModel set)
    {
        if (set?.Document.GroupId is null)
            return;

        var setId = set.Document.Id;
        void CommitMove()
        {
            set.Document.GroupId = null;
            set.NotifyChanged();
            NormalizeFlashcardSetGroups();
            RebuildFlashcardSetGroups();
            ApplyFlashcardFilters();
            SaveFlashcardSets();
        }

        if (TryRequestItemMove(DashboardMotionScope.Flashcards, setId, CommitMove))
            return;

        CommitMove();
    }

    public void DuplicateFlashcardSet(FlashcardSetViewModel sourceSet)
    {
        if (sourceSet is null)
            return;

        AddOrUpdateFlashcardSet(CloneFlashcardSetDocument(sourceSet.Document));
    }

    private static FlashcardSetDocument CloneFlashcardSetDocument(FlashcardSetDocument source)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = $"{source.Title} (Copy)",
            Tags = source.Tags.ToList(),
            SetNames = source.SetNames.ToDictionary(pair => pair.Key, pair => pair.Value),
            Cards = source.Cards.Select(card => new FlashcardItem
            {
                Id = Guid.NewGuid(),
                Question = card.Question,
                Answer = card.Answer,
                Category = card.Category,
                SetIndex = card.SetIndex,
                IsKnown = card.IsKnown,
                IsUnknown = card.IsUnknown
            }).ToList(),
            StudySession = new FlashcardStudySession(),
            CreatedAt = DateTime.UtcNow,
            LastModified = DateTime.Now,
            AiModelDisplayName = source.AiModelDisplayName,
            SourceNoteId = source.SourceNoteId,
            GroupId = source.GroupId,
            FlipDelayMilliseconds = source.FlipDelayMilliseconds
        };

    private static void NormalizeFlashcardSetDocument(FlashcardSetDocument document)
    {
        document.Id = document.Id == Guid.Empty ? Guid.NewGuid() : document.Id;
        document.Title = string.IsNullOrWhiteSpace(document.Title)
            ? LocalizationService.GetString("FlashcardSetUntitled")
            : document.Title.Trim();
        document.Tags = document.Tags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        document.SetNames = document.SetNames?
            .Where(pair => pair.Key >= 1 && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Trim()) ?? new Dictionary<int, string>();
        if (!document.SourceNoteId.HasValue || document.SourceNoteId == Guid.Empty)
        {
            var migratedLinkedNoteId = document.Cards?
                .Where(card => card != null)
                .Select(card => card.LinkedNoteId)
                .FirstOrDefault(id => id.HasValue && id != Guid.Empty);
            document.SourceNoteId = migratedLinkedNoteId;
        }
        document.Cards = document.Cards?
            .Where(card => card != null)
            .Select(card => new FlashcardItem
            {
                Id = card.Id == Guid.Empty ? Guid.NewGuid() : card.Id,
                Question = card.Question?.Trim() ?? string.Empty,
                Answer = card.Answer?.Trim() ?? string.Empty,
                Category = card.Category?.Trim() ?? string.Empty,
                SetIndex = Math.Max(1, card.SetIndex),
                IsKnown = card.IsKnown && !card.IsUnknown,
                IsUnknown = card.IsUnknown && !card.IsKnown,
                LinkedNoteId = null
            })
            .ToList() ?? new List<FlashcardItem>();
        document.CreatedAt = document.CreatedAt == default ? DateTime.UtcNow : document.CreatedAt;
        document.LastModified = document.LastModified == default ? DateTime.Now : document.LastModified;
        document.AiModelDisplayName = document.AiModelDisplayName?.Trim() ?? string.Empty;
        document.StudySession ??= new FlashcardStudySession();

        var cardIds = document.Cards.Select(card => card.Id).ToHashSet();
        document.StudySession.CurrentSetIndex = Math.Max(1, document.StudySession.CurrentSetIndex);
        if (document.StudySession.CurrentCardId.HasValue && !cardIds.Contains(document.StudySession.CurrentCardId.Value))
            document.StudySession.CurrentCardId = null;
        document.StudySession.History = document.StudySession.History
            .Where(id => cardIds.Contains(id))
            .ToList();
        if (document.StudySession.HistoryPosition >= document.StudySession.History.Count)
            document.StudySession.HistoryPosition = document.StudySession.History.Count - 1;
        if (document.StudySession.HistoryPosition < -1)
            document.StudySession.HistoryPosition = -1;

        if (document.FlipDelayMilliseconds > 0)
            document.FlipDelayMilliseconds = AppSettings.NormalizeFlashcardFlipDelayMilliseconds(document.FlipDelayMilliseconds);
    }

    private readonly record struct MindMapsLoadResult(
        bool FileExists,
        List<MindMapDocument> Maps,
        List<MindMapGroupData> Groups,
        GroupMetadataSyncAction GroupMetadataAction);

    // Pure read - runs off the UI thread via Task.Run in LoadMindMapsPersistedContent. Mirrors
    // ReadNotesFromDisk/ReadFlashcardsFromDisk.
    private static MindMapsLoadResult ReadMindMapsFromDisk(string path)
    {
        if (!File.Exists(path))
            return new MindMapsLoadResult(false, new List<MindMapDocument>(), new List<MindMapGroupData>(), GroupMetadataSyncAction.Leave);

        var json = File.ReadAllText(path);

        // Try new format first, fall back to legacy list format
        List<MindMapDocument> maps;
        List<MindMapGroupData> groups = new();
        GroupMetadataSyncAction metadataAction;
        try
        {
            var store = JsonSerializer.Deserialize<MindMapStoreData>(json);
            if (store?.Maps != null)
            {
                maps = store.Maps;
                groups = store.Groups ?? new List<MindMapGroupData>();
                metadataAction = GroupMetadataSyncAction.ClearAndSet;
            }
            else
            {
                maps = JsonSerializer.Deserialize<List<MindMapDocument>>(json) ?? new();
                metadataAction = GroupMetadataSyncAction.Leave;
            }
        }
        catch
        {
            try
            {
                maps = JsonSerializer.Deserialize<List<MindMapDocument>>(json) ?? new();
                metadataAction = GroupMetadataSyncAction.Leave;
            }
            catch
            {
                maps = new List<MindMapDocument>();
                metadataAction = GroupMetadataSyncAction.ClearOnly;
            }
        }

        return new MindMapsLoadResult(true, maps, groups, metadataAction);
    }

    private void ApplyLoadedMindMaps(MindMapsLoadResult result)
    {
        MindMaps.Clear();

        if (!result.FileExists)
        {
            RefreshAvailableMindMapTags();
            NotifyMindMapsChanged();
            _mindMapsLoadCompleted = true;
            return;
        }

        switch (result.GroupMetadataAction)
        {
            case GroupMetadataSyncAction.ClearAndSet:
                _mindMapGroupMetadata.Clear();
                foreach (var meta in result.Groups)
                    _mindMapGroupMetadata[meta.GroupId] = meta;
                break;
            case GroupMetadataSyncAction.ClearOnly:
                _mindMapGroupMetadata.Clear();
                break;
            case GroupMetadataSyncAction.Leave:
                break;
        }

        foreach (var map in result.Maps
                     .Where(map => map != null)
                     .OrderByDescending(map => map.IsPinned)
                     .ThenByDescending(map => map.LastModified)
                     .ThenBy(map => map.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            NormalizeMindMapDocument(map);
            MindMaps.Add(new MindMapViewModel(map));
        }

        NormalizeMindMapGroups();
        RebuildMindMapGroups();
        RefreshAvailableMindMapTags();
        NotifyMindMapsChanged();
        WarmMindMapPreviewsAtIdle();
        _mindMapsLoadCompleted = true;
    }

    /// <summary>
    /// Pre-walks node counts + content previews in small idle-time slices so scrolling into
    /// fresh mind map cards never re-walks the tree during card realization.
    /// </summary>
    private void WarmMindMapPreviewsAtIdle()
    {
        // Same reasoning as WarmNoteContentPreviewsAtIdle: warm in MindMapsView's actual
        // display order, not raw MindMaps load order.
        var pending = new Queue<MindMapViewModel>(MindMapsView.Cast<MindMapViewModel>());
        if (pending.Count == 0)
            return;

        void Pump()
        {
            var budget = System.Diagnostics.Stopwatch.StartNew();
            while (pending.Count > 0 && budget.ElapsedMilliseconds < 4)
                pending.Dequeue().WarmContentPreview();

            if (pending.Count > 0)
                Application.Current?.Dispatcher.BeginInvoke(Pump, DispatcherPriority.ApplicationIdle);
        }

        Application.Current?.Dispatcher.BeginInvoke(Pump, DispatcherPriority.ApplicationIdle);
    }

    public MindMapViewModel AddOrUpdateMindMap(MindMapDocument document)
    {
        NormalizeMindMapDocument(document);

        var existing = MindMaps.FirstOrDefault(map => map.Document.Id == document.Id);
        var isNew = existing is null;
        var hadMindMaps = MindMaps.Count > 0;
        if (isNew)
        {
            var newMapVm = new MindMapViewModel(document);
            if (!hadMindMaps)
            {
                MindMaps.Add(newMapVm);
                OnPropertyChanged(nameof(HasMindMaps));
                if (!document.GroupId.HasValue && MatchesMindMapFilters(newMapVm))
                    NotifyHasUngroupedMindMapsChanged();

                SaveMindMaps();
                MotionHost?.PlayDashboardContentReveal(DashboardMotionScope.MindMaps);
            }
            else
            {
                MotionHost?.PrepareMutation(DashboardMotionScope.MindMaps);
                MotionHost?.PrepareItemAdd(DashboardMotionScope.MindMaps, document.Id);
                MindMaps.Add(newMapVm);

                if (document.GroupId.HasValue)
                    RebuildMindMapGroups();

                if (!document.GroupId.HasValue && MatchesMindMapFilters(newMapVm))
                    NotifyHasUngroupedMindMapsChanged();

                SaveMindMaps();
                MotionHost?.CompleteItemAdd(DashboardMotionScope.MindMaps, document.Id);
            }

            QueueMindMapMutationReaction();
            return newMapVm;
        }

        var mapVm = existing!;
        mapVm.Document.Title = document.Title;
        mapVm.Document.Tags = document.Tags;
        mapVm.Document.Root = document.Root;
        mapVm.Document.LayoutMode = document.LayoutMode;
        mapVm.Document.UseManualPositions = document.UseManualPositions;
        mapVm.Document.CreatedAt = document.CreatedAt;
        mapVm.Document.LastModified = document.LastModified;
        mapVm.Document.AiModelDisplayName = document.AiModelDisplayName;
        mapVm.Document.SourceNoteId = document.SourceNoteId;
        mapVm.Document.GroupId = document.GroupId;
        mapVm.Document.Schedules = document.Schedules;
        mapVm.Document.IsPinned = document.IsPinned;
        mapVm.NotifyChanged();

        ReorderMindMaps();
        NormalizeMindMapGroups();
        RebuildMindMapGroups();
        RefreshAvailableMindMapTags();
        ApplyMindMapFilters();
        SaveMindMaps();
        NotifyMindMapsChanged();
        RefreshRecentMindMaps();

        return mapVm;
    }
    public MindMapGroupViewModel CreateMindMapGroup(string name)
    {
        var groupId = Guid.NewGuid();
        _mindMapGroupMetadata[groupId] = new MindMapGroupData { GroupId = groupId, Name = name, BackgroundColor = DefaultGroupBackground };
        var group = new MindMapGroupViewModel(groupId, name, DefaultGroupBackground, Enumerable.Empty<MindMapViewModel>());
        MindMapGroups.Add(group);
        SaveMindMaps();
        return group;
    }

    public bool RenameMindMapGroup(MindMapGroupViewModel group, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        group.Name = newName.Trim();
        if (_mindMapGroupMetadata.TryGetValue(group.GroupId, out var meta))
            meta.Name = group.Name;
        SaveMindMaps();
        return true;
    }

    public void DeleteMindMapGroup(MindMapGroupViewModel group)
    {
        foreach (var map in MindMaps.Where(m => m.Document.GroupId == group.GroupId))
        {
            map.Document.GroupId = null;
            map.NotifyChanged();
        }

        _mindMapGroupMetadata.Remove(group.GroupId);
        NormalizeMindMapGroups();
        RebuildMindMapGroups();
        SaveMindMaps();
    }

    private void NormalizeMindMapGroups()
    {
        var grouped = MindMaps
            .Where(map => map.Document.GroupId.HasValue)
            .GroupBy(map => map.Document.GroupId!.Value)
            .ToList();

        foreach (var group in grouped.Where(group => group.Count() < 2))
        {
            foreach (var map in group)
            {
                map.Document.GroupId = null;
                map.NotifyChanged();
            }

            _mindMapGroupMetadata.Remove(group.Key);
        }
    }

    public void AddMindMapToGroup(MindMapViewModel map, MindMapGroupViewModel group)
    {
        map.Document.GroupId = group.GroupId;
        SaveMindMaps();
        RebuildMindMapGroups();
    }

    public bool TryGroupMindMaps(MindMapViewModel draggedMap, MindMapViewModel targetMap)
    {
        if (draggedMap is null || targetMap is null || ReferenceEquals(draggedMap, targetMap))
            return false;

        var targetGroupId = targetMap.Document.GroupId ?? draggedMap.Document.GroupId ?? Guid.NewGuid();
        if (draggedMap.Document.GroupId == targetGroupId && targetMap.Document.GroupId == targetGroupId)
            return false;

        var movingIds = BuildGroupMergeMoveIds(
            draggedMap.Document.Id,
            targetMap.Document.Id,
            draggedMap.Document.GroupId,
            targetMap.Document.GroupId,
            targetGroupId);

        void CommitGroup()
        {
            if (!_mindMapGroupMetadata.ContainsKey(targetGroupId))
                _mindMapGroupMetadata[targetGroupId] = new MindMapGroupData
                {
                    GroupId = targetGroupId,
                    Name = string.Format(LocalizationService.GetString("GroupTitleFormat"), targetGroupId.ToString()[..4].ToUpperInvariant()),
                    BackgroundColor = DefaultGroupBackground
                };

            foreach (var map in new[] { draggedMap, targetMap })
            {
                if (map.Document.GroupId == targetGroupId)
                    continue;

                map.Document.GroupId = targetGroupId;
                map.NotifyChanged();
            }

            NormalizeMindMapGroups();
            RebuildMindMapGroups();
            ApplyMindMapFilters();
            SaveMindMaps();
        }

        if (TryRequestGroupMergeMove(DashboardMotionScope.MindMaps, movingIds, CommitGroup))
            return true;

        CommitGroup();
        return true;
    }

    public bool TryReorderMindMapsWithinGroup(MindMapViewModel dragged, MindMapViewModel target, bool placeAfter)
    {
        if (ReferenceEquals(dragged, target)) return false;
        var groupId = dragged.Document.GroupId;
        if (!groupId.HasValue || target.Document.GroupId != groupId) return false;

        var draggedIdx = MindMaps.IndexOf(dragged);
        var targetIdx = MindMaps.IndexOf(target);
        if (draggedIdx < 0 || targetIdx < 0) return false;

        var newIdx = placeAfter ? targetIdx + 1 : targetIdx;
        if (draggedIdx < newIdx) newIdx--;
        if (newIdx == draggedIdx) return false;

        MindMaps.Move(draggedIdx, Math.Clamp(newIdx, 0, MindMaps.Count - 1));
        RebuildMindMapGroups();
        SaveMindMaps();
        return true;
    }

    public bool TryMoveMindMapToGroup(MindMapViewModel draggedMap, MindMapGroupViewModel targetGroup)
    {
        if (draggedMap.Document.GroupId == targetGroup.GroupId)
            return false;

        var mapId = draggedMap.Document.Id;
        void CommitMove()
        {
            draggedMap.Document.GroupId = targetGroup.GroupId;
            if (!_mindMapGroupMetadata.ContainsKey(targetGroup.GroupId))
                _mindMapGroupMetadata[targetGroup.GroupId] = new MindMapGroupData
                {
                    GroupId = targetGroup.GroupId,
                    Name = targetGroup.Name,
                    BackgroundColor = targetGroup.BackgroundColor
                };
            draggedMap.NotifyChanged();
            NormalizeMindMapGroups();
            RebuildMindMapGroups();
            _mindMapsView.Refresh();
            SaveMindMaps();
            RefreshRecentMindMaps();
            NotifyMindMapsChanged();
        }

        if (TryRequestItemMove(DashboardMotionScope.MindMaps, mapId, CommitMove))
            return true;

        CommitMove();
        return true;
    }

    public bool MoveMindMapGroupUp(MindMapGroupViewModel group)
    {
        return TryMoveMindMapGroup(group, moveUp: true);
    }

    public bool MoveMindMapGroupDown(MindMapGroupViewModel group)
    {
        return TryMoveMindMapGroup(group, moveUp: false);
    }

    private bool TryMoveMindMapGroup(MindMapGroupViewModel group, bool moveUp)
    {
        var currentIndex = MindMapGroups.IndexOf(group);
        var targetIndex = moveUp ? currentIndex - 1 : currentIndex + 1;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= MindMapGroups.Count)
            return false;

        var target = MindMapGroups[targetIndex];
        var currentMeta = _mindMapGroupMetadata[group.GroupId];
        var targetMeta = _mindMapGroupMetadata[target.GroupId];
        currentMeta.SortOrder ??= currentIndex;
        targetMeta.SortOrder ??= targetIndex;
        (currentMeta.SortOrder, targetMeta.SortOrder) = (targetMeta.SortOrder, currentMeta.SortOrder);
        MindMapGroups.Move(currentIndex, targetIndex);
        SaveMindMaps();
        return true;
    }

    public bool SetMindMapGroupBackgroundColor(MindMapGroupViewModel group, string backgroundColor)
    {
        if (string.IsNullOrWhiteSpace(backgroundColor))
            return false;

        if (!_mindMapGroupMetadata.TryGetValue(group.GroupId, out var metadata))
            return false;

        metadata.BackgroundColor = backgroundColor;
        group.SetBackground(backgroundColor);
        SaveMindMaps();
        return true;
    }

    public void DisbandMindMapGroup(MindMapGroupViewModel group, bool deleteMaps)
    {
        var mapsInGroup = MindMaps.Where(map => map.Document.GroupId == group.GroupId).ToList();
        if (deleteMaps)
        {
            foreach (var map in mapsInGroup)
                MindMaps.Remove(map);
        }
        else
        {
            foreach (var map in mapsInGroup)
            {
                map.Document.GroupId = null;
                map.NotifyChanged();
            }
        }

        _mindMapGroupMetadata.Remove(group.GroupId);
        NormalizeMindMapGroups();
        RebuildMindMapGroups();
        ApplyMindMapFilters();
        SaveMindMaps();
    }

    public void RemoveMindMapFromGroup(MindMapViewModel map)
    {
        if (map?.Document.GroupId is null)
            return;

        var mapId = map.Document.Id;
        void CommitMove()
        {
            map.Document.GroupId = null;
            map.NotifyChanged();
            NormalizeMindMapGroups();
            RebuildMindMapGroups();
            ApplyMindMapFilters();
            SaveMindMaps();
        }

        if (TryRequestItemMove(DashboardMotionScope.MindMaps, mapId, CommitMove))
            return;

        CommitMove();
    }

    public void SaveMindMaps()
    {
        try
        {
            var path = GetMindMapsFilePath();
            var documents = MindMaps
                .Select(map => map.Document)
                .OrderByDescending(map => map.IsPinned)
                .ThenByDescending(map => map.LastModified)
                .ThenBy(map => map.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var store = new MindMapStoreData
            {
                Maps = documents,
                Groups = _mindMapGroupMetadata.Values
                    .OrderBy(g => g.SortOrder ?? int.MaxValue)
                    .ThenBy(g => g.Name)
                    .ToList()
            };

            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = false });
            AtomicFileWriter.WriteAllText(path, json);
        }
        catch
        {
            // Persistence errors are non-fatal; ignored.
        }
    }

    private void ReorderMindMaps()
    {
        var ordered = MindMaps
            .OrderByDescending(map => map.Document.IsPinned)
            .ThenByDescending(map => map.Document.LastModified)
            .ThenBy(map => map.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _mindMapsReorderInProgress = true;
        try
        {
            MindMaps.Clear();
            foreach (var map in ordered)
                MindMaps.Add(map);
        }
        finally
        {
            _mindMapsReorderInProgress = false;
        }
    }

    public void DeleteMindMap(MindMapViewModel mindMapVm)
    {
        if (mindMapVm is null)
            return;

        MindMaps.Remove(mindMapVm);
        ApplyMindMapDeleteReaction();
    }

    private void ApplyMindMapDeleteReaction()
    {
        NormalizeMindMapGroups();
        RebuildMindMapGroups();
        SaveMindMaps();
        RefreshAvailableMindMapTags();
        NotifyMindMapsChanged();
        RefreshRecentMindMaps();
        RefreshCalendarScheduledNotes();
        EnsureMassSelectionConsistency();
    }

    public MindMapViewModel? DuplicateMindMap(MindMapViewModel sourceMindMap)
    {
        if (sourceMindMap is null)
            return null;

        // Create a deep copy of the mind map document
        var newDocument = CloneMindMapDocument(sourceMindMap.Document);

        return AddOrUpdateMindMap(newDocument);
    }

    private static MindMapDocument CloneMindMapDocument(MindMapDocument source)
    {
        return new MindMapDocument
        {
            Id = Guid.NewGuid(), // New ID for the copy
            Title = $"{source.Title} (Copy)",
            Tags = source.Tags.ToList(),
            Root = CloneMindMapNode(source.Root),
            LayoutMode = source.LayoutMode,
            UseManualPositions = source.UseManualPositions,
            CreatedAt = DateTime.UtcNow,
            LastModified = DateTime.Now,
            AiModelDisplayName = source.AiModelDisplayName,
            SourceNoteId = source.SourceNoteId,
            GroupId = source.GroupId
        };
    }

    private static MindMapNode CloneMindMapNode(MindMapNode source)
    {
        var newNode = new MindMapNode
        {
            Text = source.Text,
            IsExpanded = source.IsExpanded,
            BackgroundColor = source.BackgroundColor,
            BorderColor = source.BorderColor,
            BorderThickness = source.BorderThickness,
            NodeShape = source.NodeShape,
            Icon = source.Icon,
            IconBadgeColor = source.IconBadgeColor,
            ManualX = source.ManualX,
            ManualY = source.ManualY,
            Children = new List<MindMapNode>()
        };

        // Recursively clone children
        foreach (var child in source.Children)
        {
            newNode.Children.Add(CloneMindMapNode(child));
        }

        return newNode;
    }

    private void NotifyMindMapsChanged()
    {
        OnPropertyChanged(nameof(HasMindMaps));
        OnPropertyChanged(nameof(HasMindMapGroups));
        NotifyHasUngroupedMindMapsChanged();
        OnPropertyChanged(nameof(MindMapCount));
        OnPropertyChanged(nameof(MindMapCountText));
        NotifyMindMapsDashboardPresentationChanged();
        RefreshRecentMindMaps();
    }

    private readonly record struct QuizzesLoadResult(
        bool FileExists,
        List<QuizDocument> Documents,
        List<QuizGroupData> Groups,
        GroupMetadataSyncAction GroupMetadataAction);

    // Pure read - runs off the UI thread via Task.Run in LoadQuizzesPersistedContent. Mirrors
    // ReadNotesFromDisk/ReadFlashcardsFromDisk/ReadMindMapsFromDisk.
    private static QuizzesLoadResult ReadQuizzesFromDisk(string path)
    {
        if (!File.Exists(path))
            return new QuizzesLoadResult(false, new List<QuizDocument>(), new List<QuizGroupData>(), GroupMetadataSyncAction.Leave);

        var json = File.ReadAllText(path);
        List<QuizDocument> documents;
        List<QuizGroupData> groups = new();
        GroupMetadataSyncAction metadataAction;
        try
        {
            var store = JsonSerializer.Deserialize<QuizStoreData>(json);
            if (store?.Quizzes != null)
            {
                documents = store.Quizzes;
                groups = store.Groups ?? new List<QuizGroupData>();
                metadataAction = GroupMetadataSyncAction.ClearAndSet;
            }
            else
            {
                documents = JsonSerializer.Deserialize<List<QuizDocument>>(json) ?? new();
                metadataAction = GroupMetadataSyncAction.Leave;
            }
        }
        catch
        {
            documents = JsonSerializer.Deserialize<List<QuizDocument>>(json) ?? new();
            metadataAction = GroupMetadataSyncAction.Leave;
        }

        return new QuizzesLoadResult(true, documents, groups, metadataAction);
    }

    private void ApplyLoadedQuizzes(QuizzesLoadResult result)
    {
        Quizzes.Clear();

        if (!result.FileExists)
        {
            RefreshAvailableQuizTags();
            NotifyQuizzesChanged();
            _quizzesLoadCompleted = true;
            return;
        }

        switch (result.GroupMetadataAction)
        {
            case GroupMetadataSyncAction.ClearAndSet:
                _quizGroupMetadata.Clear();
                foreach (var meta in result.Groups)
                    _quizGroupMetadata[meta.GroupId] = meta;
                break;
            case GroupMetadataSyncAction.ClearOnly:
                _quizGroupMetadata.Clear();
                break;
            case GroupMetadataSyncAction.Leave:
                break;
        }

        foreach (var quiz in result.Documents
                     .Where(quiz => quiz != null)
                     .OrderByDescending(quiz => quiz.IsPinned)
                     .ThenByDescending(quiz => quiz.LastModified)
                     .ThenBy(quiz => quiz.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            NormalizeQuizDocument(quiz);
            Quizzes.Add(new QuizViewModel(quiz));
        }

        NormalizeQuizGroups();
        RebuildQuizGroups();
        RefreshAvailableQuizTags();
        NotifyQuizzesChanged();
        _quizzesLoadCompleted = true;
    }

    public QuizViewModel AddOrUpdateQuiz(QuizDocument document)
    {
        NormalizeQuizDocument(document);

        var existing = Quizzes.FirstOrDefault(quiz => quiz.Document.Id == document.Id);
        var isNew = existing is null;
        var hadQuizzes = Quizzes.Count > 0;
        if (isNew)
        {
            var newQuizVm = new QuizViewModel(document);
            if (!hadQuizzes)
            {
                Quizzes.Add(newQuizVm);
                OnPropertyChanged(nameof(HasQuizzes));
                if (!document.GroupId.HasValue && MatchesQuizFilters(newQuizVm))
                    NotifyHasUngroupedQuizzesChanged();

                SaveQuizzes();
                MotionHost?.PlayDashboardContentReveal(DashboardMotionScope.Quizzes);
            }
            else
            {
                MotionHost?.PrepareMutation(DashboardMotionScope.Quizzes);
                MotionHost?.PrepareItemAdd(DashboardMotionScope.Quizzes, document.Id);
                Quizzes.Add(newQuizVm);

                if (document.GroupId.HasValue)
                    RebuildQuizGroups();

                if (!document.GroupId.HasValue && MatchesQuizFilters(newQuizVm))
                    NotifyHasUngroupedQuizzesChanged();

                SaveQuizzes();
                MotionHost?.CompleteItemAdd(DashboardMotionScope.Quizzes, document.Id);
            }

            QueueQuizMutationReaction();
            return newQuizVm;
        }

        var quizVm = existing!;
        quizVm.Document.Title = document.Title;
        quizVm.Document.Tags = document.Tags;
        quizVm.Document.Questions = document.Questions;
        quizVm.Document.CreatedAt = document.CreatedAt;
        quizVm.Document.LastModified = document.LastModified;
        quizVm.Document.AiModelDisplayName = document.AiModelDisplayName;
        quizVm.Document.SourceNoteId = document.SourceNoteId;
        quizVm.Document.GroupId = document.GroupId;
        quizVm.Document.Schedules = document.Schedules;
        quizVm.Document.IsPinned = document.IsPinned;
        quizVm.Document.TimeLimitSeconds = document.TimeLimitSeconds;
        quizVm.Document.PassingScorePercent = document.PassingScorePercent;
        quizVm.Document.Attempts = document.Attempts;
        quizVm.NotifyChanged();

        ReorderQuizzes();
        NormalizeQuizGroups();
        RebuildQuizGroups();
        RefreshAvailableQuizTags();
        ApplyQuizFilters();
        SaveQuizzes();
        NotifyQuizzesChanged();
        RefreshRecentQuizzes();

        return quizVm;
    }

    public void DeleteQuiz(QuizViewModel quiz)
    {
        if (quiz is null)
            return;

        Quizzes.Remove(quiz);
        ApplyQuizDeleteReaction();
    }

    private void ApplyQuizDeleteReaction()
    {
        NormalizeQuizGroups();
        RebuildQuizGroups();
        SaveQuizzes();
        RefreshAvailableQuizTags();
        NotifyQuizzesChanged();
        RefreshRecentQuizzes();
        RefreshCalendarScheduledNotes();
        EnsureMassSelectionConsistency();
    }

    public void SaveQuizzes()
    {
        try
        {
            var path = GetQuizzesFilePath();
            var documents = Quizzes
                .Select(quiz => quiz.Document)
                .OrderByDescending(quiz => quiz.IsPinned)
                .ThenByDescending(quiz => quiz.LastModified)
                .ThenBy(quiz => quiz.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var store = new QuizStoreData
            {
                Quizzes = documents,
                Groups = _quizGroupMetadata.Values
                    .OrderBy(g => g.SortOrder ?? int.MaxValue)
                    .ThenBy(g => g.Name)
                    .ToList()
            };

            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = false });
            AtomicFileWriter.WriteAllText(path, json);
        }
        catch
        {
            // Persistence errors are non-fatal; ignored.
        }
    }

    public NoteCardViewModel? FindNoteById(Guid noteId)
    {
        return Notes.FirstOrDefault(note => note.Document.Id == noteId);
    }

    private void ReorderQuizzes()
    {
        var ordered = Quizzes
            .OrderByDescending(quiz => quiz.Document.IsPinned)
            .ThenByDescending(quiz => quiz.Document.LastModified)
            .ThenBy(quiz => quiz.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _quizzesReorderInProgress = true;
        try
        {
            Quizzes.Clear();
            foreach (var quiz in ordered)
                Quizzes.Add(quiz);
        }
        finally
        {
            _quizzesReorderInProgress = false;
        }
    }

    private void NotifyQuizzesChanged()
    {
        OnPropertyChanged(nameof(HasQuizzes));
        OnPropertyChanged(nameof(HasQuizGroups));
        NotifyHasUngroupedQuizzesChanged();
        OnPropertyChanged(nameof(QuizCount));
        OnPropertyChanged(nameof(QuizCountText));
        NotifyQuizzesDashboardPresentationChanged();
        RefreshRecentQuizzes();
    }

    private void NormalizeQuizGroups()
    {
        var grouped = Quizzes
            .Where(quiz => quiz.Document.GroupId.HasValue)
            .GroupBy(quiz => quiz.Document.GroupId!.Value)
            .ToList();

        foreach (var group in grouped.Where(group => group.Count() < 2))
        {
            foreach (var quiz in group)
            {
                quiz.Document.GroupId = null;
                quiz.NotifyChanged();
            }

            _quizGroupMetadata.Remove(group.Key);
        }
    }

    public void RebuildQuizGroups()
    {
        var grouped = Quizzes
            .Where(quiz => quiz.Document.GroupId.HasValue)
            .GroupBy(quiz => quiz.Document.GroupId!.Value)
            .OrderBy(group => EnsureQuizGroupMetadata(group.Key).SortOrder ?? int.MaxValue)
            .ThenByDescending(group => group.Max(quiz => quiz.Document.LastModified))
            .ToList();

        var desiredGroups = new List<QuizGroupViewModel>();
        foreach (var group in grouped)
        {
            var visibleQuizzes = group
                .Where(MatchesQuizFilters)
                .OrderByDescending(quiz => quiz.Document.IsPinned)
                .ThenByDescending(quiz => quiz.Document.LastModified)
                .ThenBy(quiz => quiz.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (visibleQuizzes.Count == 0)
                continue;

            var metadata = EnsureQuizGroupMetadata(group.Key);
            desiredGroups.Add(new QuizGroupViewModel(group.Key, metadata.Name, metadata.BackgroundColor, visibleQuizzes));
        }

        var desiredIds = desiredGroups.Select(group => group.GroupId).ToHashSet();
        var removingIds = QuizGroups
            .Where(group => !desiredIds.Contains(group.GroupId))
            .Select(group => group.GroupId)
            .ToList();

        if (TryAnimateGroupRemovals(DashboardMotionScope.Quizzes, removingIds, () => SyncQuizGroups(desiredGroups)))
            return;

        SyncQuizGroups(desiredGroups);
    }

    private void SyncQuizGroups(IReadOnlyList<QuizGroupViewModel> desiredGroups)
    {
        var desiredIds = desiredGroups.Select(group => group.GroupId).ToHashSet();
        for (var i = QuizGroups.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(QuizGroups[i].GroupId))
                QuizGroups.RemoveAt(i);
        }

        for (var i = 0; i < desiredGroups.Count; i++)
        {
            var desired = desiredGroups[i];
            var existing = QuizGroups.FirstOrDefault(group => group.GroupId == desired.GroupId);
            if (existing is null)
            {
                QuizGroups.Insert(Math.Min(i, QuizGroups.Count), desired);
                continue;
            }

            if (QuizGroups.IndexOf(existing) != i)
                QuizGroups.Move(QuizGroups.IndexOf(existing), i);

            SyncObservableCollectionOrder(existing.Quizzes, desired.Quizzes.ToList());
        }

        OnPropertyChanged(nameof(HasQuizGroups));
        NotifyHasUngroupedQuizzesChanged();
    }

    private QuizGroupData EnsureQuizGroupMetadata(Guid groupId)
    {
        if (_quizGroupMetadata.TryGetValue(groupId, out var metadata))
        {
            metadata.SortOrder ??= GetNextQuizGroupSortOrder();
            return metadata;
        }

        metadata = new QuizGroupData
        {
            GroupId = groupId,
            Name = string.Format(LocalizationService.GetString("GroupTitleFormat"), groupId.ToString()[..4].ToUpperInvariant()),
            BackgroundColor = DefaultGroupBackground,
            SortOrder = GetNextQuizGroupSortOrder()
        };
        _quizGroupMetadata[groupId] = metadata;
        return metadata;
    }

    private int GetNextQuizGroupSortOrder()
    {
        return _quizGroupMetadata.Values
            .Select(group => group.SortOrder ?? -1)
            .DefaultIfEmpty(-1)
            .Max() + 1;
    }

    public bool TryGroupQuizzes(QuizViewModel draggedQuiz, QuizViewModel targetQuiz)
    {
        if (draggedQuiz is null || targetQuiz is null || ReferenceEquals(draggedQuiz, targetQuiz))
            return false;

        var targetGroupId = targetQuiz.Document.GroupId ?? draggedQuiz.Document.GroupId ?? Guid.NewGuid();
        if (draggedQuiz.Document.GroupId == targetGroupId && targetQuiz.Document.GroupId == targetGroupId)
            return false;

        var movingIds = BuildGroupMergeMoveIds(
            draggedQuiz.Document.Id,
            targetQuiz.Document.Id,
            draggedQuiz.Document.GroupId,
            targetQuiz.Document.GroupId,
            targetGroupId);

        void CommitGroup()
        {
            EnsureQuizGroupMetadata(targetGroupId);

            foreach (var quiz in new[] { draggedQuiz, targetQuiz })
            {
                if (quiz.Document.GroupId == targetGroupId)
                    continue;

                quiz.Document.GroupId = targetGroupId;
                quiz.NotifyChanged();
            }

            NormalizeQuizGroups();
            RebuildQuizGroups();
            ApplyQuizFilters();
            SaveQuizzes();
        }

        if (TryRequestGroupMergeMove(DashboardMotionScope.Quizzes, movingIds, CommitGroup))
            return true;

        CommitGroup();
        return true;
    }

    public bool TryReorderQuizzesWithinGroup(QuizViewModel dragged, QuizViewModel target, bool placeAfter)
    {
        if (ReferenceEquals(dragged, target)) return false;
        var groupId = dragged.Document.GroupId;
        if (!groupId.HasValue || target.Document.GroupId != groupId) return false;

        var draggedIdx = Quizzes.IndexOf(dragged);
        var targetIdx = Quizzes.IndexOf(target);
        if (draggedIdx < 0 || targetIdx < 0) return false;

        var newIdx = placeAfter ? targetIdx + 1 : targetIdx;
        if (draggedIdx < newIdx) newIdx--;
        if (newIdx == draggedIdx) return false;

        Quizzes.Move(draggedIdx, Math.Clamp(newIdx, 0, Quizzes.Count - 1));
        RebuildQuizGroups();
        SaveQuizzes();
        return true;
    }

    public bool TryMoveQuizToGroup(QuizViewModel draggedQuiz, QuizGroupViewModel targetGroup)
    {
        if (draggedQuiz.Document.GroupId == targetGroup.GroupId)
            return false;

        var quizId = draggedQuiz.Document.Id;
        void CommitMove()
        {
            draggedQuiz.Document.GroupId = targetGroup.GroupId;
            EnsureQuizGroupMetadata(targetGroup.GroupId);
            draggedQuiz.NotifyChanged();
            NormalizeQuizGroups();
            RebuildQuizGroups();
            _quizzesView.Refresh();
            SaveQuizzes();
            RefreshRecentQuizzes();
            NotifyQuizzesChanged();
        }

        if (TryRequestItemMove(DashboardMotionScope.Quizzes, quizId, CommitMove))
            return true;

        CommitMove();
        return true;
    }

    public bool MoveQuizGroupUp(QuizGroupViewModel group)
    {
        return TryMoveQuizGroup(group, moveUp: true);
    }

    public bool MoveQuizGroupDown(QuizGroupViewModel group)
    {
        return TryMoveQuizGroup(group, moveUp: false);
    }

    private bool TryMoveQuizGroup(QuizGroupViewModel group, bool moveUp)
    {
        var currentIndex = QuizGroups.IndexOf(group);
        var targetIndex = moveUp ? currentIndex - 1 : currentIndex + 1;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= QuizGroups.Count)
            return false;

        var target = QuizGroups[targetIndex];
        var currentMeta = EnsureQuizGroupMetadata(group.GroupId);
        var targetMeta = EnsureQuizGroupMetadata(target.GroupId);
        (currentMeta.SortOrder, targetMeta.SortOrder) = (targetMeta.SortOrder, currentMeta.SortOrder);
        QuizGroups.Move(currentIndex, targetIndex);
        SaveQuizzes();
        return true;
    }

    public bool RenameQuizGroup(QuizGroupViewModel group, string newName)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        var metadata = EnsureQuizGroupMetadata(group.GroupId);
        metadata.Name = trimmed;
        group.Name = trimmed;
        SaveQuizzes();
        return true;
    }

    public bool SetQuizGroupBackgroundColor(QuizGroupViewModel group, string backgroundColor)
    {
        if (string.IsNullOrWhiteSpace(backgroundColor))
            return false;

        var metadata = EnsureQuizGroupMetadata(group.GroupId);
        metadata.BackgroundColor = backgroundColor;
        group.SetBackground(backgroundColor);
        SaveQuizzes();
        return true;
    }

    public Guid CreateQuizGroup(string groupName)
    {
        var trimmed = (groupName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return Guid.Empty;

        var groupId = Guid.NewGuid();
        var metadata = EnsureQuizGroupMetadata(groupId);
        metadata.Name = trimmed;
        NormalizeQuizGroups();
        RebuildQuizGroups();
        ApplyQuizFilters();
        SaveQuizzes();
        return groupId;
    }

    public bool CreateQuizGroupForQuiz(QuizViewModel quiz, string groupName)
    {
        if (quiz is null)
            return false;

        var trimmed = (groupName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        var groupId = quiz.Document.GroupId ?? Guid.NewGuid();
        var metadata = EnsureQuizGroupMetadata(groupId);
        metadata.Name = trimmed;
        quiz.Document.GroupId = groupId;
        quiz.NotifyChanged();

        NormalizeQuizGroups();
        RebuildQuizGroups();
        ApplyQuizFilters();
        SaveQuizzes();
        return true;
    }

    public void DisbandQuizGroup(QuizGroupViewModel group, bool deleteQuizzes)
    {
        var quizzesInGroup = Quizzes.Where(quiz => quiz.Document.GroupId == group.GroupId).ToList();
        if (deleteQuizzes)
        {
            foreach (var quiz in quizzesInGroup)
                Quizzes.Remove(quiz);
        }
        else
        {
            foreach (var quiz in quizzesInGroup)
            {
                quiz.Document.GroupId = null;
                quiz.NotifyChanged();
            }
        }

        _quizGroupMetadata.Remove(group.GroupId);
        NormalizeQuizGroups();
        RebuildQuizGroups();
        ApplyQuizFilters();
        SaveQuizzes();
    }

    public void RemoveQuizFromGroup(QuizViewModel quiz)
    {
        if (quiz?.Document.GroupId is null)
            return;

        var quizId = quiz.Document.Id;
        void CommitMove()
        {
            quiz.Document.GroupId = null;
            quiz.NotifyChanged();
            NormalizeQuizGroups();
            RebuildQuizGroups();
            ApplyQuizFilters();
            SaveQuizzes();
        }

        if (TryRequestItemMove(DashboardMotionScope.Quizzes, quizId, CommitMove))
            return;

        CommitMove();
    }

    private static void NormalizeQuizDocument(QuizDocument document)
    {
        document.Id = document.Id == Guid.Empty ? Guid.NewGuid() : document.Id;
        document.Title = string.IsNullOrWhiteSpace(document.Title)
            ? LocalizationService.GetString("QuizUntitled")
            : document.Title.Trim();
        document.Tags = document.Tags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        document.Questions = document.Questions?
            .Where(question => question != null)
            .Select(NormalizeQuizQuestion)
            .Where(question => !string.IsNullOrWhiteSpace(question.Question) && (question.Type == QuizQuestionType.PlainText || question.Options.Count > 0))
            .ToList() ?? new List<QuizQuestion>();
        document.CreatedAt = document.CreatedAt == default ? DateTime.UtcNow : document.CreatedAt;
        document.LastModified = document.LastModified == default ? DateTime.Now : document.LastModified;
        document.AiModelDisplayName = document.AiModelDisplayName?.Trim() ?? string.Empty;
    }

    private static QuizQuestion NormalizeQuizQuestion(QuizQuestion question)
    {
        question.Question = question.Question?.Trim() ?? string.Empty;
        question.Explanation = question.Explanation?.Trim() ?? string.Empty;
        question.Hint = question.Hint?.Trim() ?? string.Empty;
        question.PlainTextAnswer = question.PlainTextAnswer?.Trim() ?? string.Empty;
        question.Options = question.Options?
            .Where(option => option != null && !string.IsNullOrWhiteSpace(option.Text))
            .Select(option => new QuizOption
            {
                Text = option.Text.Trim(),
                IsCorrect = option.IsCorrect
            })
            .ToList() ?? new List<QuizOption>();
        question.SetIndex = Math.Max(1, question.SetIndex);
        return question;
    }

    private static void NormalizeMindMapDocument(MindMapDocument document)
    {
        document.Id = document.Id == Guid.Empty ? Guid.NewGuid() : document.Id;
        document.Title = string.IsNullOrWhiteSpace(document.Title)
            ? LocalizationService.GetString("MindMapUntitled")
            : document.Title.Trim();
        document.Tags = document.Tags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        document.Root ??= new MindMapNode();
        NormalizeMindMapNode(document.Root);
        if (string.IsNullOrWhiteSpace(document.Root.Text))
            document.Root.Text = document.Title;
        document.LayoutMode = NormalizeMindMapLayoutMode(document.LayoutMode);
        document.UseManualPositions = document.UseManualPositions && HasAnyManualMindMapPosition(document.Root);
        document.CreatedAt = document.CreatedAt == default ? DateTime.UtcNow : document.CreatedAt;
        document.LastModified = document.LastModified == default ? DateTime.Now : document.LastModified;
        document.AiModelDisplayName = document.AiModelDisplayName?.Trim() ?? string.Empty;
    }

    private static string NormalizeMindMapLayoutMode(string? layoutMode)
    {
        return layoutMode?.Trim() switch
        {
            "Radial" => "Radial",
            "RightTree" => "RightTree",
            "LeftTree" => "LeftTree",
            "TopDown" => "TopDown",
            _ => "BalancedTree"
        };
    }

    private static void NormalizeMindMapNode(MindMapNode node)
    {
        node.Text = node.Text?.Trim() ?? string.Empty;
        if (node.ManualX is not { } x || node.ManualY is not { } y || !IsFinite(x) || !IsFinite(y))
        {
            node.ManualX = null;
            node.ManualY = null;
        }

        node.Children = node.Children?
            .Where(child => child != null)
            .ToList() ?? new List<MindMapNode>();

        foreach (var child in node.Children)
            NormalizeMindMapNode(child);
    }

    private static bool HasAnyManualMindMapPosition(MindMapNode node)
    {
        if (node.ManualX.HasValue && node.ManualY.HasValue)
            return true;

        return node.Children.Any(HasAnyManualMindMapPosition);
    }

    private static bool IsFinite(double value)
        => !double.IsNaN(value) && !double.IsInfinity(value);


    public ObservableCollection<NoteCardViewModel> Notes { get; }
    public IDashboardMotionHost? MotionHost { get; set; }
    public ObservableCollection<NoteGroupViewModel> NoteGroups { get; }
    public ObservableCollection<TagFilterItemViewModel> TagFilters { get; }
    public ObservableCollection<TagFilterItemViewModel> FlashcardTagFilters { get; }
    public ObservableCollection<TagFilterItemViewModel> MindMapTagFilters { get; }
    public ObservableCollection<TagFilterItemViewModel> QuizTagFilters { get; }
    public ObservableCollection<NoteSortOptionItemViewModel> SortOptions { get; }
    public ObservableCollection<NoteSortOptionItemViewModel> FlashcardSortOptions { get; }
    public ObservableCollection<NoteSortOptionItemViewModel> MindMapSortOptions { get; }
    public ObservableCollection<NoteSortOptionItemViewModel> QuizSortOptions { get; }
    public IEnumerable<NoteSortOptionItemViewModel> ActiveSortOptions => IsMindMapsView
        ? MindMapSortOptions
        : IsQuizzesView ? QuizSortOptions : IsFlashcardsView ? FlashcardSortOptions : SortOptions;
    public IEnumerable<TagFilterItemViewModel> ActiveTagFilters => IsMindMapsView
        ? MindMapTagFilters
        : IsQuizzesView ? QuizTagFilters : IsFlashcardsView ? FlashcardTagFilters : TagFilters;
    public ObservableCollection<CalendarScheduledItemViewModel> CalendarScheduledNotes { get; }
    public IReadOnlyCollection<DateTime> CalendarDatesWithSchedules { get; private set; } = Array.Empty<DateTime>();
     public bool HasGroups => NoteGroups.Count > 0;
    public bool HasNotes => Notes.Count > 0;
    public bool HasUngroupedNotes => UngroupedVisibleCount > 0;

    private int UngroupedVisibleCount
    {
        get
        {
            if (_ungroupedVisibleCount < 0)
                RecalculateUngroupedVisibleCount();

            return _ungroupedVisibleCount;
        }
    }
    public bool HasTagFilters => TagFilters.Count > 0;
    public bool HasFlashcardTagFilters => FlashcardTagFilters.Count > 0;
    public bool HasMindMapTagFilters => MindMapTagFilters.Count > 0;
    public bool HasQuizTagFilters => QuizTagFilters.Count > 0;
    public bool ActiveHasTagFilters => IsMindMapsView
        ? HasMindMapTagFilters
        : IsQuizzesView ? HasQuizTagFilters : IsFlashcardsView ? HasFlashcardTagFilters : HasTagFilters;
    public bool HasActiveTagFilters => _selectedTags.Count > 0;
    public bool HasActiveFlashcardTagFilters => _selectedFlashcardTags.Count > 0;
    public bool HasActiveMindMapTagFilters => _selectedMindMapTags.Count > 0;
    public bool HasActiveQuizTagFilters => _selectedQuizTags.Count > 0;
    public bool ActiveHasActiveTagFilters => IsMindMapsView
        ? HasActiveMindMapTagFilters
        : IsQuizzesView ? HasActiveQuizTagFilters : IsFlashcardsView ? HasActiveFlashcardTagFilters : HasActiveTagFilters;
    public bool IsMassSelectMode
    {
        get => _isMassSelectMode;
        private set
        {
            if (!SetProperty(ref _isMassSelectMode, value))
                return;

            OnPropertyChanged(nameof(IsNotMassSelectMode));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsNotMassSelectMode => !IsMassSelectMode;
    public int SelectedNotesCount => ActiveMassSelectedCount;
    public int ActiveMassSelectedCount => _massSelectDashboard switch
    {
        DashboardFlashcards => _massSelectedFlashcardSetIds.Count,
        DashboardMindMaps => _massSelectedMindMapIds.Count,
        DashboardQuizzes => _massSelectedQuizIds.Count,
        _ => _massSelectedNoteIds.Count
    };
    public string MassSelectSelectionText => string.Format(LocalizationService.GetString("MassSelectSelectedCount"), ActiveMassSelectedCount);
    public string TagFilterButtonText => HasActiveTagFilters
        ? $"{LocalizationService.GetString("FilterTags")} ({_selectedTags.Count})"
        : LocalizationService.GetString("FilterTags");
    public string ActiveTagFilterButtonText
    {
        get
        {
            if (IsMindMapsView)
            {
                return HasActiveMindMapTagFilters
                    ? $"{LocalizationService.GetString("FilterTags")} ({_selectedMindMapTags.Count})"
                    : LocalizationService.GetString("FilterTags");
            }

            if (IsQuizzesView)
            {
                return HasActiveQuizTagFilters
                    ? $"{LocalizationService.GetString("FilterTags")} ({_selectedQuizTags.Count})"
                    : LocalizationService.GetString("FilterTags");
            }

            if (IsFlashcardsView)
            {
                return HasActiveFlashcardTagFilters
                ? $"{LocalizationService.GetString("FilterTags")} ({_selectedFlashcardTags.Count})"
                : LocalizationService.GetString("FilterTags");
            }

            return TagFilterButtonText;
        }
    }
    public string SortButtonText => string.Format(
        LocalizationService.GetString("SortButtonFormat"),
        GetSortOptionDisplayName(_selectedSortOptionKey));
    public string ActiveSortButtonText => IsMindMapsView
        ? string.Format(LocalizationService.GetString("SortButtonFormat"), GetMindMapSortOptionDisplayName(_selectedMindMapSortOptionKey))
        : IsQuizzesView
            ? string.Format(LocalizationService.GetString("SortButtonFormat"), GetQuizSortOptionDisplayName(_selectedQuizSortOptionKey))
        : IsFlashcardsView
            ? string.Format(LocalizationService.GetString("SortButtonFormat"), GetFlashcardSortOptionDisplayName(_selectedFlashcardSortOptionKey))
            : SortButtonText;
    public bool HasCalendarScheduledNotes => CalendarScheduledNotes.Count > 0;

    public DateTime CalendarSelectedDate
    {
        get => _calendarSelectedDate;
        set
        {
            var normalized = value.Date;
            if (!SetProperty(ref _calendarSelectedDate, normalized))
                return;

            RefreshCalendarScheduledNotes();
            OnPropertyChanged(nameof(CalendarSelectedDateDisplay));
        }
    }

    public string CalendarSelectedDateDisplay => CalendarSelectedDate.ToString("dddd, dd MMM yyyy", CultureInfo.CurrentCulture);

    public string SelectedSortOptionKey
    {
        get => _selectedSortOptionKey;
        set
        {
            var normalized = NormalizeSortOptionKey(value);
            if (!SetProperty(ref _selectedSortOptionKey, normalized))
                return;

            UpdateSortOptionSelection();
            ApplySortToUngroupedView();
            OnPropertyChanged(nameof(SortButtonText));
            OnPropertyChanged(nameof(ActiveSortButtonText));
            ApplyFilters();
            SaveAppSettings();
        }
    }

    private readonly ICollectionView _notesView;
    public ICollectionView NotesView => _notesView;
    private readonly ICollectionView _flashcardSetsView;
    public ICollectionView FlashcardSetsView => _flashcardSetsView;
    private readonly ICollectionView _mindMapsView;
    public ICollectionView MindMapsView => _mindMapsView;
    private readonly ICollectionView _quizzesView;
    public ICollectionView QuizzesView => _quizzesView;

    private bool _isRecentSectionExpanded = true;
    public bool IsRecentSectionExpanded
    {
        get => _isRecentSectionExpanded;
        set
        {
            if (SetProperty(ref _isRecentSectionExpanded, value))
                SaveAppSettings();
        }
    }

    private bool _isGroupsSectionExpanded = true;
    public bool IsGroupsSectionExpanded
    {
        get => _isGroupsSectionExpanded;
        set
        {
            if (SetProperty(ref _isGroupsSectionExpanded, value))
                SaveAppSettings();
        }
    }

    private bool _isUngroupedSectionExpanded = true;
    public bool IsUngroupedSectionExpanded
    {
        get => _isUngroupedSectionExpanded;
        set
        {
            if (SetProperty(ref _isUngroupedSectionExpanded, value))
                SaveAppSettings();
        }
    }

    public bool IsMindMapDashboardGroupsSectionVisible
    {
        get => _isMindMapDashboardGroupsSectionVisible;
        set
        {
            if (SetProperty(ref _isMindMapDashboardGroupsSectionVisible, value))
                SaveAppSettings();
        }
    }

    public bool IsMindMapDashboardUngroupedSectionVisible
    {
        get => _isMindMapDashboardUngroupedSectionVisible;
        set
        {
            if (SetProperty(ref _isMindMapDashboardUngroupedSectionVisible, value))
                SaveAppSettings();
        }
    }

    public bool IsQuizGroupsSectionVisible
    {
        get => _isQuizGroupsSectionVisible;
        set
        {
            if (SetProperty(ref _isQuizGroupsSectionVisible, value))
            {
                if (!value)
                {
                    IsQuizDashboardGroupsSectionVisible = false;
                    IsQuizDashboardUngroupedSectionVisible = false;
                }

                ApplyQuizFilters();
                SaveAppSettings();
            }
        }
    }

    public bool IsQuizDashboardGroupsSectionVisible
    {
        get => _isQuizDashboardGroupsSectionVisible;
        set
        {
            if (SetProperty(ref _isQuizDashboardGroupsSectionVisible, value))
                SaveAppSettings();
        }
    }

    public bool IsQuizDashboardUngroupedSectionVisible
    {
        get => _isQuizDashboardUngroupedSectionVisible;
        set
        {
            if (SetProperty(ref _isQuizDashboardUngroupedSectionVisible, value))
                SaveAppSettings();
        }
    }

    public bool IsNotesDashboardSectionExpanded
    {
        get => _isNotesDashboardSectionExpanded;
        set
        {
            if (SetProperty(ref _isNotesDashboardSectionExpanded, value))
                SaveAppSettings();
        }
    }

    public bool IsFlashcardGroupsSectionExpanded
    {
        get => _isFlashcardGroupsSectionExpanded;
        set
        {
            if (SetProperty(ref _isFlashcardGroupsSectionExpanded, value))
                SaveAppSettings();
        }
    }

    public bool IsFlashcardDashboardGroupsSectionExpanded
    {
        get => _isFlashcardDashboardGroupsSectionExpanded;
        set
        {
            if (SetProperty(ref _isFlashcardDashboardGroupsSectionExpanded, value))
                SaveAppSettings();
        }
    }

    public bool IsFlashcardDashboardUngroupedSectionExpanded
    {
        get => _isFlashcardDashboardUngroupedSectionExpanded;
        set
        {
            if (SetProperty(ref _isFlashcardDashboardUngroupedSectionExpanded, value))
                SaveAppSettings();
        }
    }

    public bool IsFlashcardDashboardGroupsFirst
    {
        get => _isFlashcardDashboardGroupsFirst;
        set
        {
            if (SetProperty(ref _isFlashcardDashboardGroupsFirst, value))
                SaveAppSettings();
        }
    }

    public bool IsMindMapGroupsSectionExpanded
    {
        get => _isMindMapGroupsSectionExpanded;
        set
        {
            if (SetProperty(ref _isMindMapGroupsSectionExpanded, value))
                SaveAppSettings();
        }
    }

    public bool IsMindMapDashboardGroupsSectionExpanded
    {
        get => _isMindMapDashboardGroupsSectionExpanded;
        set
        {
            if (SetProperty(ref _isMindMapDashboardGroupsSectionExpanded, value))
                SaveAppSettings();
        }
    }

    public bool IsMindMapDashboardUngroupedSectionExpanded
    {
        get => _isMindMapDashboardUngroupedSectionExpanded;
        set
        {
            if (SetProperty(ref _isMindMapDashboardUngroupedSectionExpanded, value))
                SaveAppSettings();
        }
    }

    public bool IsMindMapDashboardGroupsFirst
    {
        get => _isMindMapDashboardGroupsFirst;
        set
        {
            if (SetProperty(ref _isMindMapDashboardGroupsFirst, value))
                SaveAppSettings();
        }
    }

    public bool IsQuizGroupsSectionExpanded
    {
        get => _isQuizGroupsSectionExpanded;
        set
        {
            if (SetProperty(ref _isQuizGroupsSectionExpanded, value))
                SaveAppSettings();
        }
    }

    public bool IsQuizDashboardGroupsSectionExpanded
    {
        get => _isQuizDashboardGroupsSectionExpanded;
        set
        {
            if (SetProperty(ref _isQuizDashboardGroupsSectionExpanded, value))
                SaveAppSettings();
        }
    }

    public bool IsQuizDashboardUngroupedSectionExpanded
    {
        get => _isQuizDashboardUngroupedSectionExpanded;
        set
        {
            if (SetProperty(ref _isQuizDashboardUngroupedSectionExpanded, value))
                SaveAppSettings();
        }
    }

    public bool IsQuizDashboardGroupsFirst
    {
        get => _isQuizDashboardGroupsFirst;
        set
        {
            if (SetProperty(ref _isQuizDashboardGroupsFirst, value))
                SaveAppSettings();
        }
    }

    private bool _isGroupsFirst = true;
    public bool IsGroupsFirst
    {
        get => _isGroupsFirst;
        set
        {
            if (SetProperty(ref _isGroupsFirst, value))
            {
                CommandManager.InvalidateRequerySuggested();
                SaveAppSettings();
            }
        }
    }

    // Activity Summary Properties
    public string UserActivitySummaryTitle => LocalizationService.GetString("UserActivitySummaryTitle");
    public string StatsTotalNotes => string.Format(LocalizationService.GetString("StatsTotalNotesFormat"), Notes.Count);
    public string StatsWordsTyped => string.Format(LocalizationService.GetString("StatsWordsTypedFormat"), AppSettingsService.Load().TotalWordsTyped);
    public string StatsCharactersTyped => string.Format(LocalizationService.GetString("StatsCharactersTypedFormat"), AppSettingsService.Load().TotalCharactersTyped);
    public string StatsTimeSpent => string.Format(LocalizationService.GetString("StatsTimeSpentFormat"), GetTotalTimeSpent());
    public string StatsLastActive => string.Format(
        LocalizationService.GetString("StatsLastActiveFormat"),
        AppSettingsService.Load().LastActiveDate?.ToString("yyyy-MM-dd HH:mm") ?? LocalizationService.GetString("NotAvailable"));

    private string GetTotalTimeSpent()
    {
        var span = TimeSpan.FromSeconds(AppSettingsService.Load().TotalTimeSpentSeconds);
        return $"{(int)span.TotalHours}h {span.Minutes}m";
    }

    public void RefreshActivityStats()
    {
        OnPropertyChanged(nameof(StatsTotalNotes));
        OnPropertyChanged(nameof(StatsWordsTyped));
        OnPropertyChanged(nameof(StatsCharactersTyped));
        OnPropertyChanged(nameof(StatsTimeSpent));
        OnPropertyChanged(nameof(StatsLastActive));
    }

    private string _searchQuery = string.Empty;
    private string _flashcardSearchQuery = string.Empty;
    private string _mindMapSearchQuery = string.Empty;
    private string _quizSearchQuery = string.Empty;
    private DispatcherTimer? _searchFilterDebounceTimer;
    private Action? _pendingSearchFilterApply;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery != value)
            {
                _searchQuery = value ?? string.Empty;
                OnPropertyChanged(nameof(SearchQuery));
                OnPropertyChanged(nameof(ActiveSearchQuery));
                QueueSearchFilterApply(ApplyFilters);
            }
        }
    }

    public string FlashcardSearchQuery
    {
        get => _flashcardSearchQuery;
        set
        {
            if (_flashcardSearchQuery == (value ?? string.Empty))
                return;

            _flashcardSearchQuery = value ?? string.Empty;
            OnPropertyChanged(nameof(FlashcardSearchQuery));
            OnPropertyChanged(nameof(ActiveSearchQuery));
            QueueSearchFilterApply(ApplyFlashcardFilters);
        }
    }

    public string MindMapSearchQuery
    {
        get => _mindMapSearchQuery;
        set
        {
            if (_mindMapSearchQuery == (value ?? string.Empty))
                return;

            _mindMapSearchQuery = value ?? string.Empty;
            OnPropertyChanged(nameof(MindMapSearchQuery));
            OnPropertyChanged(nameof(ActiveSearchQuery));
            QueueSearchFilterApply(ApplyMindMapFilters);
        }
    }

    public string QuizSearchQuery
    {
        get => _quizSearchQuery;
        set
        {
            if (_quizSearchQuery == (value ?? string.Empty))
                return;

            _quizSearchQuery = value ?? string.Empty;
            OnPropertyChanged(nameof(QuizSearchQuery));
            OnPropertyChanged(nameof(ActiveSearchQuery));
            QueueSearchFilterApply(ApplyQuizFilters);
        }
    }

    // Search-as-you-type debounce: applying the filter re-evaluates the dashboard's
    // ICollectionView (a full Reset, dropping realized containers/scroll position) and
    // rebuilds the Recent/Calendar strips, so doing that on every keystroke is wasteful.
    // The bound TextBox still updates immediately; only the filter pass is delayed.
    private void QueueSearchFilterApply(Action applyFilters)
    {
        _pendingSearchFilterApply = applyFilters;
        _searchFilterDebounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchFilterDebounceTimer.Stop();
        _searchFilterDebounceTimer.Tick -= SearchFilterDebounceTimer_Tick;
        _searchFilterDebounceTimer.Tick += SearchFilterDebounceTimer_Tick;
        _searchFilterDebounceTimer.Start();
    }

    private void SearchFilterDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchFilterDebounceTimer?.Stop();
        var applyFilters = _pendingSearchFilterApply;
        _pendingSearchFilterApply = null;
        applyFilters?.Invoke();
    }

    public string ActiveSearchQuery
    {
        get => IsMindMapsView
            ? MindMapSearchQuery
            : IsQuizzesView ? QuizSearchQuery
            : IsFlashcardsView ? FlashcardSearchQuery : SearchQuery;
        set
        {
            if (IsMindMapsView)
                MindMapSearchQuery = value;
            else if (IsQuizzesView)
                QuizSearchQuery = value;
            else if (IsFlashcardsView)
                FlashcardSearchQuery = value;
            else
                SearchQuery = value;

            OnPropertyChanged(nameof(ActiveSearchQuery));
        }
    }

    public ICommand AddNoteCommand { get; }
    public ICommand ToggleRecentSectionCommand { get; }
    public ICommand ToggleGroupsSectionCommand { get; }
    public ICommand ToggleUngroupedSectionCommand { get; }
    public ICommand MoveGroupsUpCommand { get; }
    public ICommand MoveGroupsDownCommand { get; }
    public ICommand MoveUngroupedUpCommand { get; }
    public ICommand MoveUngroupedDownCommand { get; }
    public ICommand ClearTagFiltersCommand { get; }
    public ICommand ExitMassSelectCommand { get; }
    public ICommand SelectAllVisibleNotesCommand { get; }
    public ICommand DeleteSelectedNotesCommand { get; }
    public ICommand RemoveSelectedFromGroupsCommand { get; }
    public ICommand GroupSelectedNotesCommand { get; }
    public ICommand PinSelectedNotesCommand { get; }
    public ICommand UnpinSelectedNotesCommand { get; }
    public ICommand DuplicateSelectedNotesCommand { get; }
    public ICommand AddFlashcardCommand { get; }
    public ICommand ShowFlashcardsCommand { get; }
    public ICommand ShowQuizzesCommand { get; }
    public ICommand ShowNotesCommand { get; }
    public ICommand ShowMindMapsCommand { get; }

    public void SetTagFilterSelected(string tag, bool isSelected)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        if (isSelected)
            _selectedTags.Add(tag);
        else
            _selectedTags.Remove(tag);

        OnPropertyChanged(nameof(HasActiveTagFilters));
        OnPropertyChanged(nameof(TagFilterButtonText));
        OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
        CommandManager.InvalidateRequerySuggested();
        ApplyFilters();
    }

    public NoteCardViewModel AddNoteFromDocument(NoteDocument document)
    {
        EnsureDocumentTypography(document);
        var note = CreateNoteCard(document);
        var hadNotes = Notes.Count > 0;
        var previousUngroupedCount = UngroupedVisibleCount;

        if (!hadNotes)
        {
            Notes.Add(note);
            OnPropertyChanged(nameof(HasNotes));

            if (!document.GroupId.HasValue && MatchesSearch(note))
            {
                if (_ungroupedVisibleCount < 0)
                    RecalculateUngroupedVisibleCount();
                else
                    _ungroupedVisibleCount++;

                if (previousUngroupedCount == 0)
                    OnPropertyChanged(nameof(HasUngroupedNotes));
            }

            MotionHost?.PlayDashboardContentReveal(DashboardMotionScope.Notes);
        }
        else
        {
            MotionHost?.PrepareMutation(DashboardMotionScope.Notes);
            MotionHost?.PrepareItemAdd(DashboardMotionScope.Notes, document.Id);
            Notes.Add(note);

            if (document.GroupId.HasValue)
                RebuildGroups();

            if (!document.GroupId.HasValue && MatchesSearch(note))
            {
                if (_ungroupedVisibleCount < 0)
                    RecalculateUngroupedVisibleCount();
                else
                    _ungroupedVisibleCount++;

                if (previousUngroupedCount == 0)
                    OnPropertyChanged(nameof(HasUngroupedNotes));
            }

            MotionHost?.CompleteItemAdd(DashboardMotionScope.Notes, document.Id);
        }
        QueueSaveNotes();
        QueueNotesMutationReaction();
        return note;
    }

    private void OnNotesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_notesBatchAddInProgress || _notesPinReorderInProgress)
            return;

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
            && e.NewItems?.Count == 1
            && e.NewItems[0] is NoteCardViewModel added)
        {
            _notesMutationIsLightweightAdd = true;
            _notesMutationLightweightAddedNote = added;
            _notesMutationIsLightweightDelete = false;
        }
        else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
        {
            _notesMutationIsLightweightAdd = false;
            _notesMutationLightweightAddedNote = null;
            _notesMutationIsLightweightDelete = true;
        }
        else
        {
            _notesMutationIsLightweightAdd = false;
            _notesMutationLightweightAddedNote = null;
            _notesMutationIsLightweightDelete = false;
        }

        QueueNotesMutationReaction();
    }

    private void RecalculateUngroupedVisibleCount()
    {
        _ungroupedVisibleCount = Notes.Count(note =>
            !note.Document.GroupId.HasValue && MatchesSearch(note));
    }

    private void InvalidateUngroupedVisibleCount()
    {
        _ungroupedVisibleCount = -1;
    }

    private void QueueNotesMutationReaction()
    {
        _notesMutationReactionTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _notesMutationReactionTimer.Stop();
        _notesMutationReactionTimer.Tick -= NotesMutationReactionTimer_Tick;
        _notesMutationReactionTimer.Tick += NotesMutationReactionTimer_Tick;
        _notesMutationReactionTimer.Start();
    }

    private void NotesMutationReactionTimer_Tick(object? sender, EventArgs e)
    {
        _notesMutationReactionTimer?.Stop();

        if (DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.Notes))
        {
            QueueNotesMutationReaction();
            return;
        }

        var previousUngroupedCount = UngroupedVisibleCount;
        var lightweightAdd = _notesMutationIsLightweightAdd;
        var lightweightDelete = _notesMutationIsLightweightDelete;
        var addedNote = _notesMutationLightweightAddedNote;
        _notesMutationIsLightweightAdd = false;
        _notesMutationIsLightweightDelete = false;
        _notesMutationLightweightAddedNote = null;

        if (lightweightDelete)
        {
            RefreshActivityStats();
            return;
        }

        if (lightweightAdd && addedNote != null)
            ApplyLightweightNotesAddReaction(addedNote);
        else
            ApplyFilters();

        RecalculateUngroupedVisibleCount();
        if (previousUngroupedCount != UngroupedVisibleCount)
            OnPropertyChanged(nameof(HasUngroupedNotes));

        NotifyNotesChanged();
        RefreshActivityStats();
        EnsureMassSelectionConsistency();
    }

    private void ApplyLightweightNotesAddReaction(NoteCardViewModel addedNote)
    {
        if (addedNote.Document.Tags is { Count: > 0 })
            RefreshAvailableTags();

        RefreshRecentNotes();
        RefreshCalendarScheduledNotes();
    }

    private void OnFlashcardSetsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_flashcardBatchAddInProgress || _flashcardDataLoadInProgress || _flashcardSetsReorderInProgress)
            return;

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            return;

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
            && e.NewItems?.Count == 1
            && e.NewItems[0] is FlashcardSetViewModel added)
        {
            _flashcardMutationIsLightweightAdd = true;
            _flashcardMutationLightweightAddedSet = added;
            _flashcardMutationIsLightweightDelete = false;
        }
        else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
        {
            _flashcardMutationIsLightweightAdd = false;
            _flashcardMutationLightweightAddedSet = null;
            _flashcardMutationIsLightweightDelete = true;
        }
        else
        {
            _flashcardMutationIsLightweightAdd = false;
            _flashcardMutationLightweightAddedSet = null;
            _flashcardMutationIsLightweightDelete = false;
        }

        QueueFlashcardMutationReaction();
    }

    private void QueueFlashcardMutationReaction()
    {
        _flashcardMutationReactionTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _flashcardMutationReactionTimer.Stop();
        _flashcardMutationReactionTimer.Tick -= FlashcardMutationReactionTimer_Tick;
        _flashcardMutationReactionTimer.Tick += FlashcardMutationReactionTimer_Tick;
        _flashcardMutationReactionTimer.Start();
    }

    private void FlashcardMutationReactionTimer_Tick(object? sender, EventArgs e)
    {
        _flashcardMutationReactionTimer?.Stop();

        if (DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.Flashcards))
        {
            QueueFlashcardMutationReaction();
            return;
        }

        var lightweightAdd = _flashcardMutationIsLightweightAdd;
        var lightweightDelete = _flashcardMutationIsLightweightDelete;
        var addedSet = _flashcardMutationLightweightAddedSet;
        _flashcardMutationIsLightweightAdd = false;
        _flashcardMutationIsLightweightDelete = false;
        _flashcardMutationLightweightAddedSet = null;

        if (lightweightDelete)
        {
            RefreshActivityStats();
            return;
        }

        if (lightweightAdd && addedSet != null)
            ApplyLightweightFlashcardSetAddReaction(addedSet);
        else
        {
            RefreshAvailableFlashcardTags();
            ApplyFlashcardFilters();
        }

        NotifyFlashcardSetsChanged();
        RefreshActivityStats();
        EnsureMassSelectionConsistency();
    }

    private void ApplyLightweightFlashcardSetAddReaction(FlashcardSetViewModel addedSet)
    {
        if (addedSet.Document.Tags is { Count: > 0 })
            RefreshAvailableFlashcardTags();

        RefreshRecentFlashcardSets();
        RefreshCalendarScheduledNotes();
    }

    private void OnMindMapsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_mindMapBatchAddInProgress || _mindMapDataLoadInProgress || _mindMapsReorderInProgress)
            return;

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            return;

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
            && e.NewItems?.Count == 1
            && e.NewItems[0] is MindMapViewModel added)
        {
            _mindMapMutationIsLightweightAdd = true;
            _mindMapMutationLightweightAddedMindMap = added;
            _mindMapMutationIsLightweightDelete = false;
        }
        else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
        {
            _mindMapMutationIsLightweightAdd = false;
            _mindMapMutationLightweightAddedMindMap = null;
            _mindMapMutationIsLightweightDelete = true;
        }
        else
        {
            _mindMapMutationIsLightweightAdd = false;
            _mindMapMutationLightweightAddedMindMap = null;
            _mindMapMutationIsLightweightDelete = false;
        }

        QueueMindMapMutationReaction();
    }

    private void QueueMindMapMutationReaction()
    {
        _mindMapMutationReactionTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _mindMapMutationReactionTimer.Stop();
        _mindMapMutationReactionTimer.Tick -= MindMapMutationReactionTimer_Tick;
        _mindMapMutationReactionTimer.Tick += MindMapMutationReactionTimer_Tick;
        _mindMapMutationReactionTimer.Start();
    }

    private void MindMapMutationReactionTimer_Tick(object? sender, EventArgs e)
    {
        _mindMapMutationReactionTimer?.Stop();

        if (DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.MindMaps))
        {
            QueueMindMapMutationReaction();
            return;
        }

        var lightweightAdd = _mindMapMutationIsLightweightAdd;
        var lightweightDelete = _mindMapMutationIsLightweightDelete;
        var addedMindMap = _mindMapMutationLightweightAddedMindMap;
        _mindMapMutationIsLightweightAdd = false;
        _mindMapMutationIsLightweightDelete = false;
        _mindMapMutationLightweightAddedMindMap = null;

        if (lightweightDelete)
        {
            RefreshActivityStats();
            return;
        }

        if (lightweightAdd && addedMindMap != null)
            ApplyLightweightMindMapAddReaction(addedMindMap);
        else
        {
            RefreshAvailableMindMapTags();
            ApplyMindMapFilters();
        }

        NotifyMindMapsChanged();
        RefreshActivityStats();
        EnsureMassSelectionConsistency();
    }

    private void ApplyLightweightMindMapAddReaction(MindMapViewModel addedMindMap)
    {
        if (addedMindMap.Document.Tags is { Count: > 0 })
            RefreshAvailableMindMapTags();

        RefreshRecentMindMaps();
        RefreshCalendarScheduledNotes();
    }

    private void OnQuizzesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_quizBatchAddInProgress || _quizDataLoadInProgress || _quizzesReorderInProgress)
            return;

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            return;

        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add
            && e.NewItems?.Count == 1
            && e.NewItems[0] is QuizViewModel added)
        {
            _quizMutationIsLightweightAdd = true;
            _quizMutationLightweightAddedQuiz = added;
            _quizMutationIsLightweightDelete = false;
        }
        else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
        {
            _quizMutationIsLightweightAdd = false;
            _quizMutationLightweightAddedQuiz = null;
            _quizMutationIsLightweightDelete = true;
        }
        else
        {
            _quizMutationIsLightweightAdd = false;
            _quizMutationLightweightAddedQuiz = null;
            _quizMutationIsLightweightDelete = false;
        }

        QueueQuizMutationReaction();
    }

    private void QueueQuizMutationReaction()
    {
        _quizMutationReactionTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _quizMutationReactionTimer.Stop();
        _quizMutationReactionTimer.Tick -= QuizMutationReactionTimer_Tick;
        _quizMutationReactionTimer.Tick += QuizMutationReactionTimer_Tick;
        _quizMutationReactionTimer.Start();
    }

    private void QuizMutationReactionTimer_Tick(object? sender, EventArgs e)
    {
        _quizMutationReactionTimer?.Stop();

        if (DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.Quizzes))
        {
            QueueQuizMutationReaction();
            return;
        }

        var lightweightAdd = _quizMutationIsLightweightAdd;
        var lightweightDelete = _quizMutationIsLightweightDelete;
        var addedQuiz = _quizMutationLightweightAddedQuiz;
        _quizMutationIsLightweightAdd = false;
        _quizMutationIsLightweightDelete = false;
        _quizMutationLightweightAddedQuiz = null;

        if (lightweightDelete)
        {
            RefreshActivityStats();
            return;
        }

        if (lightweightAdd && addedQuiz != null)
            ApplyLightweightQuizAddReaction(addedQuiz);
        else
        {
            RefreshAvailableQuizTags();
            ApplyQuizFilters();
        }

        NotifyQuizzesChanged();
        RefreshActivityStats();
        EnsureMassSelectionConsistency();
    }

    private void ApplyLightweightQuizAddReaction(QuizViewModel addedQuiz)
    {
        if (addedQuiz.Document.Tags is { Count: > 0 })
            RefreshAvailableQuizTags();

        RefreshRecentQuizzes();
        RefreshCalendarScheduledNotes();
    }

    private void AddNote()
    {
        if (IsMassSelectMode)
            return;

        var (fontFamily, fontSize) = GetCachedPreferredTypography();

        var document = new NoteDocument
        {
            Title = LocalizationService.GetString("NewNoteTitle"),
            Content = string.Empty,
            FontFamily = fontFamily,
            FontSize = fontSize
        };
        AddNoteFromDocument(document);
    }

    private void EnsureDocumentTypography(NoteDocument document)
    {
        var (fontFamily, fontSize) = GetCachedPreferredTypography();

        if (string.IsNullOrWhiteSpace(document.FontFamily))
            document.FontFamily = fontFamily;

        if (document.FontSize <= 0)
            document.FontSize = fontSize;
    }

    private static (string FontFamily, double FontSize) GetPreferredTypography()
    {
        var settings = AppSettingsService.Load();
        return (
            string.IsNullOrWhiteSpace(settings.PreferredFontFamily) ? "Calibri" : settings.PreferredFontFamily,
            settings.PreferredFontSize > 0 ? settings.PreferredFontSize : 14);
    }

    private (string FontFamily, double FontSize) GetCachedPreferredTypography()
    {
        if (_cachedPreferredFontFamily is null)
        {
            var (fontFamily, fontSize) = GetPreferredTypography();
            _cachedPreferredFontFamily = fontFamily;
            _cachedPreferredFontSize = fontSize;
        }

        return (_cachedPreferredFontFamily, _cachedPreferredFontSize);
    }

    public void InvalidatePreferredTypographyCache()
    {
        _cachedPreferredFontFamily = null;
        _cachedPreferredFontSize = 0;
    }

    private void DuplicateNote(NoteCardViewModel noteCard)
    {
        // Create a copy of the document with new ID and timestamps
        var duplicateDocument = new NoteDocument
        {
            Title = $"{noteCard.Document.Title} (Copy)",
            Content = noteCard.Document.Content,
            Images = noteCard.Document.Images?
                .Select(image => new NoteImageAttachment
                {
                    Id = Guid.NewGuid(),
                    Data = image.Data,
                    Layout = image.Layout,
                    Width = image.Width,
                    Height = image.Height,
                    Left = image.Left,
                    Top = image.Top,
                    PreserveAspectRatio = image.PreserveAspectRatio
                })
                .ToList() ?? new List<NoteImageAttachment>(),
            Tags = noteCard.Document.Tags?.ToList() ?? new List<string>(), // Copy tags if they exist
            FontFamily = noteCard.Document.FontFamily,
            FontSize = noteCard.Document.FontSize,
            CreatedAt = DateTime.Now,
            LastModified = DateTime.Now,
            GroupId = noteCard.Document.GroupId
        };

        AddNoteFromDocument(duplicateDocument);
    }

    private void TogglePin(NoteCardViewModel noteCard)
    {
        noteCard.Document.IsPinned = !noteCard.Document.IsPinned;
        CommitDashboardPinToggle(
            DashboardMotionScope.Notes,
            noteCard,
            noteCard.IsPinned,
            noteCard.NotifyPinChanged,
            ApplyNotePinSort,
            SaveNotes);
    }

    public void ToggleFlashcardSetPin(FlashcardSetViewModel set)
    {
        if (set is null)
            return;

        set.Document.IsPinned = !set.Document.IsPinned;
        CommitDashboardPinToggle(
            DashboardMotionScope.Flashcards,
            set,
            set.IsPinned,
            set.NotifyChanged,
            ApplyFlashcardPinSort,
            SaveFlashcardSets);
    }

    public void ToggleMindMapPin(MindMapViewModel mindMap)
    {
        if (mindMap is null)
            return;

        mindMap.Document.IsPinned = !mindMap.Document.IsPinned;
        CommitDashboardPinToggle(
            DashboardMotionScope.MindMaps,
            mindMap,
            mindMap.IsPinned,
            mindMap.NotifyChanged,
            ApplyMindMapPinSort,
            SaveMindMaps);
    }

    public void ToggleQuizPin(QuizViewModel quiz)
    {
        if (quiz is null)
            return;

        quiz.Document.IsPinned = !quiz.Document.IsPinned;
        CommitDashboardPinToggle(
            DashboardMotionScope.Quizzes,
            quiz,
            quiz.IsPinned,
            quiz.NotifyChanged,
            ApplyQuizPinSort,
            SaveQuizzes);
    }

    /// <summary>
    /// Pin icon motion first, then FLIP reflow reorder (same path as delete) via move-based sort.
    /// </summary>
    private void CommitDashboardPinToggle(
        DashboardMotionScope scope,
        object viewModel,
        bool pinning,
        Action notifyViewModel,
        Action applyPinSort,
        Action save)
    {
        CommitDashboardIndicatorToggle(
            scope,
            viewModel,
            DashboardIndicatorProperties.IsPinned,
            pinning,
            notifyViewModel,
            () => CommitPinReorderWithMotion(scope, applyPinSort, save));
    }

    private void CommitDashboardScheduleToggle(
        DashboardMotionScope scope,
        object viewModel,
        bool showingSchedule,
        Action notifyViewModel,
        Action applyScheduleUpdate,
        Action save)
    {
        CommitDashboardIndicatorToggle(
            scope,
            viewModel,
            DashboardIndicatorProperties.HasSchedule,
            showingSchedule,
            notifyViewModel,
            () =>
            {
                applyScheduleUpdate();
                save();
            });
    }

    /// <summary>
    /// Shared pin/schedule icon reveal or dismiss, then deferred dashboard commit.
    /// </summary>
    private static void CommitDashboardIndicatorToggle(
        DashboardMotionScope scope,
        object viewModel,
        string indicatorProperty,
        bool showIndicator,
        Action notifyViewModel,
        Action commitAfterIconMotion)
    {
        _ = scope;

        if (showIndicator)
        {
            IndicatorRevealMotionBehavior.ScheduleReveal(
                viewModel,
                indicatorProperty,
                deferPlayback: true);
            notifyViewModel();
            IndicatorRevealMotionBehavior.FlushPending();
            ScheduleAfterIndicatorIconMotion(
                commitAfterIconMotion,
                IndicatorRevealMotionBehavior.RevealAnimationMs);
            return;
        }

        notifyViewModel();
        ScheduleAfterIndicatorIconMotion(
            commitAfterIconMotion,
            IndicatorRevealMotionBehavior.DismissAnimationMs);
    }

    private void CommitPinReorderWithMotion(DashboardMotionScope scope, Action applyPinSort, Action save)
    {
        if (MotionHost is null || UiMotion.PrefersReducedMotion)
        {
            applyPinSort();
            save();
            return;
        }

        MotionHost.PrepareMutation(scope);
        applyPinSort();
        MotionHost.CompleteMutation(scope, afterMotion: save);
    }

    private static void ScheduleAfterIndicatorIconMotion(Action commit, int animationMs)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || animationMs <= 0 || UiMotion.PrefersReducedMotion)
        {
            commit();
            return;
        }

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(animationMs + 48)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            commit();
        };
        timer.Start();
    }

    private void ApplyNotePinSort()
    {
        InvalidateUngroupedVisibleCount();
        ReorderNotesForPinSort();
        SyncGroupedNoteOrder();
        RefreshRecentNotes();
        RefreshCalendarScheduledNotes();
    }

    private void SyncGroupedNoteOrder()
    {
        foreach (var group in NoteGroups)
        {
            var ordered = SortNotes(group.Notes);
            SyncObservableCollectionOrder(group.Notes, ordered);
        }
    }

    private void ApplyFlashcardPinSort()
    {
        ReorderFlashcardSetsForPinSort();
        SyncGroupedFlashcardSetOrder();
        RefreshRecentFlashcardSets();
    }

    private void SyncGroupedFlashcardSetOrder()
    {
        foreach (var group in FlashcardSetGroups)
        {
            var ordered = group.Sets
                .OrderByDescending(set => set.Document.IsPinned)
                .ThenByDescending(set => set.Document.LastModified)
                .ThenBy(set => set.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            SyncObservableCollectionOrder(group.Sets, ordered);
        }
    }

    private void ApplyMindMapPinSort()
    {
        ReorderMindMapsForPinSort();
        SyncGroupedMindMapOrder();
        RefreshRecentMindMaps();
    }

    private void SyncGroupedMindMapOrder()
    {
        foreach (var group in MindMapGroups)
        {
            var ordered = group.MindMaps
                .OrderByDescending(map => map.Document.IsPinned)
                .ThenByDescending(map => map.Document.LastModified)
                .ThenBy(map => map.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            SyncObservableCollectionOrder(group.MindMaps, ordered);
        }
    }

    private void ApplyQuizPinSort()
    {
        ReorderQuizzesForPinSort();
        SyncGroupedQuizOrder();
        RefreshRecentQuizzes();
    }

    private void SyncGroupedQuizOrder()
    {
        foreach (var group in QuizGroups)
        {
            var ordered = group.Quizzes
                .OrderByDescending(quiz => quiz.Document.IsPinned)
                .ThenByDescending(quiz => quiz.Document.LastModified)
                .ThenBy(quiz => quiz.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            SyncObservableCollectionOrder(group.Quizzes, ordered);
        }
    }

    private void RequestDeleteNote(NoteCardViewModel noteCard)
    {
        if (MotionHost is not null)
        {
            MotionHost.RequestDelete(
                DashboardMotionScope.Notes,
                noteCard.Document.Id,
                () => CommitDeleteNote(noteCard),
                ApplyNotesDeleteReaction);
            return;
        }

        CommitDeleteNote(noteCard);
        ApplyNotesDeleteReaction();
    }

    public void RequestDeleteFlashcardSet(FlashcardSetViewModel set)
    {
        if (MotionHost is not null)
        {
            MotionHost.RequestDelete(
                DashboardMotionScope.Flashcards,
                set.Document.Id,
                () => FlashcardSets.Remove(set),
                ApplyFlashcardSetDeleteReaction);
            return;
        }

        DeleteFlashcardSet(set);
    }

    public void RequestDeleteQuiz(QuizViewModel quiz)
    {
        if (MotionHost is not null)
        {
            MotionHost.RequestDelete(
                DashboardMotionScope.Quizzes,
                quiz.Document.Id,
                () => Quizzes.Remove(quiz),
                ApplyQuizDeleteReaction);
            return;
        }

        DeleteQuiz(quiz);
    }

    public void RequestDeleteMindMap(MindMapViewModel mindMapVm)
    {
        if (MotionHost is not null)
        {
            MotionHost.RequestDelete(
                DashboardMotionScope.MindMaps,
                mindMapVm.Document.Id,
                () => MindMaps.Remove(mindMapVm),
                ApplyMindMapDeleteReaction);
            return;
        }

        DeleteMindMap(mindMapVm);
    }

    private void CommitDeleteNote(NoteCardViewModel noteCard)
    {
        _massSelectedNoteIds.Remove(noteCard.Document.Id);
        noteCard.IsSelectedInMassSelect = false;
        Notes.Remove(noteCard);
    }

    private void ApplyNotesDeleteReaction()
    {
        NormalizeGroups();
        RebuildGroups();
        QueueSaveNotes();
        RecalculateUngroupedVisibleCount();
        NotifyNotesChanged();
        RefreshRecentNotes();
        RefreshCalendarScheduledNotes();
        EnsureMassSelectionConsistency();
    }

    public void EnterMassSelect(NoteCardViewModel initialNote)
    {
        _massSelectDashboard = DashboardNotes;
        SetNoteSelectedState(initialNote, true, notify: false);
        IsMassSelectMode = true;
        NotifyMassSelectionChanged();
    }

    public void EnterMassSelect(FlashcardSetViewModel initialSet)
    {
        _massSelectDashboard = DashboardFlashcards;
        SetFlashcardSetSelectedState(initialSet, true, notify: false);
        IsMassSelectMode = true;
        NotifyMassSelectionChanged();
    }

    public void EnterMassSelect(MindMapViewModel initialMap)
    {
        _massSelectDashboard = DashboardMindMaps;
        SetMindMapSelectedState(initialMap, true, notify: false);
        IsMassSelectMode = true;
        NotifyMassSelectionChanged();
    }

    public void EnterMassSelect(QuizViewModel initialQuiz)
    {
        _massSelectDashboard = DashboardQuizzes;
        SetQuizSelectedState(initialQuiz, true, notify: false);
        IsMassSelectMode = true;
        NotifyMassSelectionChanged();
    }

    public void ToggleMassSelectForNote(NoteCardViewModel note)
    {
        if (!IsMassSelectMode || !string.Equals(_massSelectDashboard, DashboardNotes, StringComparison.Ordinal))
            return;

        SetNoteSelectedState(note, !_massSelectedNoteIds.Contains(note.Document.Id));
    }

    public void ToggleMassSelectForDashboardItem(object? item)
    {
        if (!IsMassSelectMode)
            return;

        switch (item)
        {
            case FlashcardSetViewModel set when string.Equals(_massSelectDashboard, DashboardFlashcards, StringComparison.Ordinal):
                SetFlashcardSetSelectedState(set, !_massSelectedFlashcardSetIds.Contains(set.Document.Id));
                break;
            case MindMapViewModel map when string.Equals(_massSelectDashboard, DashboardMindMaps, StringComparison.Ordinal):
                SetMindMapSelectedState(map, !_massSelectedMindMapIds.Contains(map.Document.Id));
                break;
            case QuizViewModel quiz when string.Equals(_massSelectDashboard, DashboardQuizzes, StringComparison.Ordinal):
                SetQuizSelectedState(quiz, !_massSelectedQuizIds.Contains(quiz.Document.Id));
                break;
        }
    }

    public void ExitMassSelect()
    {
        if (!IsMassSelectMode)
            return;

        _massSelectedNoteIds.Clear();
        _massSelectedFlashcardSetIds.Clear();
        _massSelectedMindMapIds.Clear();
        _massSelectedQuizIds.Clear();
        foreach (var note in Notes)
            note.IsSelectedInMassSelect = false;
        foreach (var set in FlashcardSets)
            set.IsSelectedInMassSelect = false;
        foreach (var map in MindMaps)
            map.IsSelectedInMassSelect = false;
        foreach (var quiz in Quizzes)
            quiz.IsSelectedInMassSelect = false;

        IsMassSelectMode = false;
        NotifyMassSelectionChanged();
    }

    public int AddTagsToSelected(string tagsInput)
    {
        if (!IsMassSelectMode || string.IsNullOrWhiteSpace(tagsInput))
            return 0;

        var tags = tagsInput
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tags.Count == 0)
            return 0;

        var affected = 0;
        foreach (var item in GetSelectedTaggableDocuments())
        {
            var changed = false;
            foreach (var tag in tags)
            {
                if (item.Tags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)))
                    continue;

                item.Tags.Add(tag);
                changed = true;
            }

            if (!changed)
                continue;

            affected++;
            item.LastModified = DateTime.Now;
            item.NotifyChanged();
        }

        if (affected == 0)
            return 0;

        SaveActiveMassSelectDashboard();
        RefreshActiveMassSelectDashboard();
        return affected;
    }

    public bool TryGroupNotes(NoteCardViewModel draggedNote, NoteCardViewModel targetNote)
    {
        if (ReferenceEquals(draggedNote, targetNote))
            return false;

        var targetGroupId = targetNote.Document.GroupId;
        var finalGroupId = targetGroupId ?? draggedNote.Document.GroupId ?? Guid.NewGuid();

        if (draggedNote.Document.GroupId == finalGroupId && targetNote.Document.GroupId == finalGroupId)
            return false;

        var movingIds = BuildGroupMergeMoveIds(
            draggedNote.Document.Id,
            targetNote.Document.Id,
            draggedNote.Document.GroupId,
            targetNote.Document.GroupId,
            finalGroupId);

        void CommitGroup()
        {
            draggedNote.Document.GroupId = finalGroupId;
            targetNote.Document.GroupId = finalGroupId;
            EnsureGroupMetadata(finalGroupId);

            draggedNote.NotifyGroupChanged();
            targetNote.NotifyGroupChanged();

            NormalizeGroups();
            RebuildGroups();
            SaveNotes();
        }

        if (TryRequestGroupMergeMove(DashboardMotionScope.Notes, movingIds, CommitGroup))
            return true;

        CommitGroup();
        return true;
    }

    public bool MoveGroupUp(NoteGroupViewModel group)
    {
        return TryMoveGroup(group, moveUp: true);
    }

    public bool MoveGroupDown(NoteGroupViewModel group)
    {
        return TryMoveGroup(group, moveUp: false);
    }

    public bool TryReorderNotesWithinGroup(NoteCardViewModel draggedNote, NoteCardViewModel targetNote, bool placeAfter)
    {
        if (ReferenceEquals(draggedNote, targetNote))
            return false;

        var groupId = draggedNote.Document.GroupId;
        if (!groupId.HasValue || targetNote.Document.GroupId != groupId)
            return false;

        var draggedIndex = Notes.IndexOf(draggedNote);
        var targetIndex = Notes.IndexOf(targetNote);
        if (draggedIndex < 0 || targetIndex < 0)
            return false;

        var newIndex = placeAfter ? targetIndex + 1 : targetIndex;
        if (draggedIndex < newIndex)
            newIndex--;

        if (newIndex == draggedIndex)
            return false;

        Notes.Move(draggedIndex, Math.Clamp(newIndex, 0, Notes.Count - 1));
        RebuildGroups();
        SaveNotes();
        return true;
    }

    public bool TryMoveNoteToGroup(NoteCardViewModel draggedNote, NoteGroupViewModel targetGroup)
    {
        if (draggedNote.Document.GroupId == targetGroup.GroupId)
            return false;

        var noteId = draggedNote.Document.Id;
        void CommitMove()
        {
            draggedNote.Document.GroupId = targetGroup.GroupId;
            EnsureGroupMetadata(targetGroup.GroupId);
            draggedNote.NotifyGroupChanged();
            NormalizeGroups();
            RebuildGroups();
            _notesView.Refresh();
            SaveNotes();
        }

        if (TryRequestItemMove(DashboardMotionScope.Notes, noteId, CommitMove))
            return true;

        CommitMove();
        return true;
    }

    public void RemoveFromGroup(NoteCardViewModel note)
    {
        if (!note.Document.GroupId.HasValue)
            return;

        var noteId = note.Document.Id;
        void CommitMove()
        {
            note.Document.GroupId = null;
            note.NotifyGroupChanged();
            NormalizeGroups();
            RebuildGroups();
            _notesView.Refresh();
            SaveNotes();
        }

        if (TryRequestItemMove(DashboardMotionScope.Notes, noteId, CommitMove))
            return;

        CommitMove();
    }

    public bool TryDropToUngrouped(NoteCardViewModel draggedNote)
    {
        if (!draggedNote.Document.GroupId.HasValue)
            return false;

        RemoveFromGroup(draggedNote);
        return true;
    }

    public bool RenameGroup(NoteGroupViewModel group, string newName)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        var metadata = EnsureGroupMetadata(group.GroupId);
        if (string.Equals(metadata.Name, trimmed, StringComparison.Ordinal))
            return false;

        metadata.Name = trimmed;
        group.Name = trimmed;
        SaveNotes();
        return true;
    }

    public bool SetGroupBackgroundColor(NoteGroupViewModel group, string backgroundColor)
    {
        if (string.IsNullOrWhiteSpace(backgroundColor))
            return false;

        var metadata = EnsureGroupMetadata(group.GroupId);
        if (string.Equals(metadata.BackgroundColor, backgroundColor, StringComparison.OrdinalIgnoreCase))
            return false;

        metadata.BackgroundColor = backgroundColor;
        group.SetBackground(backgroundColor);
        SaveNotes();
        return true;
    }

    public void DisbandGroup(NoteGroupViewModel group, bool deleteNotes)
    {
        var notesInGroup = Notes.Where(n => n.Document.GroupId == group.GroupId).ToList();
        if (deleteNotes)
        {
            foreach (var note in notesInGroup)
                Notes.Remove(note);
        }
        else
        {
            foreach (var note in notesInGroup)
            {
                note.Document.GroupId = null;
                note.NotifyGroupChanged();
            }
        }

        _groupMetadata.Remove(group.GroupId);
        NormalizeGroups();
        RebuildGroups();
        SaveNotes();
    }

    private bool _isSidebarExpanded = false;

    public bool IsSidebarExpanded => _isSidebarExpanded;

    public double SidebarWidth
    {
        get => _isSidebarExpanded ? 232 : 64;
    }

    public ICommand ToggleSidebarCommand { get; }

    private void ToggleSidebar()
    {
        _isSidebarExpanded = !_isSidebarExpanded;
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(IsSidebarExpanded));
    }

    private void MoveGroupsUp()
    {
        IsGroupsFirst = true;
    }

    private void MoveGroupsDown()
    {
        IsGroupsFirst = false;
    }

    private void MoveUngroupedUp()
    {
        IsGroupsFirst = false;
    }

    private void MoveUngroupedDown()
    {
        IsGroupsFirst = true;
    }

    public ObservableCollection<NoteCardViewModel> RecentNotes { get; } = new();
    public ObservableCollection<FlashcardSetViewModel> RecentFlashcardSets { get; } = new();
    public ObservableCollection<MindMapViewModel> RecentMindMaps { get; } = new();
    public ObservableCollection<QuizViewModel> RecentQuizzes { get; } = new();
    public bool HasRecentFlashcardSets => RecentFlashcardSets.Count > 0;
    public bool HasRecentMindMaps => RecentMindMaps.Count > 0;
    public bool HasRecentQuizzes => RecentQuizzes.Count > 0;

    public void RefreshRecentNotes()
    {
        var recent = Notes
            .Where(MatchesSearch)
            .OrderByDescending(n => n.Document.LastModified)
            .Take(_recentItemsLimit)
            .ToList();
        RecentNotes.Clear();
        foreach (var note in recent)
            RecentNotes.Add(note);
    }

    public void RefreshRecentFlashcardSets()
    {
        var recent = FlashcardSets
            .Where(MatchesFlashcardSetFilters)
            .OrderByDescending(set => set.Document.LastModified)
            .Take(_recentItemsLimit)
            .ToList();

        RecentFlashcardSets.Clear();
        foreach (var set in recent)
            RecentFlashcardSets.Add(set);

        OnPropertyChanged(nameof(HasRecentFlashcardSets));
    }

    public void RefreshRecentMindMaps()
    {
        var recent = MindMaps
            .Where(MatchesMindMapFilters)
            .OrderByDescending(map => map.Document.LastModified)
            .Take(_recentItemsLimit)
            .ToList();

        RecentMindMaps.Clear();
        foreach (var map in recent)
            RecentMindMaps.Add(map);

        OnPropertyChanged(nameof(HasRecentMindMaps));
    }

    public void RefreshRecentQuizzes()
    {
        var recent = Quizzes
            .Where(MatchesQuizFilters)
            .OrderByDescending(quiz => quiz.Document.LastModified)
            .Take(_recentItemsLimit)
            .ToList();

        RecentQuizzes.Clear();
        foreach (var quiz in recent)
            RecentQuizzes.Add(quiz);

        OnPropertyChanged(nameof(HasRecentQuizzes));
    }

    public void RefreshRecentItemsFromSettings()
    {
        var limit = AppSettings.NormalizeRecentItemsLimit(AppSettingsService.Load().RecentItemsLimit);
        if (_recentItemsLimit == limit)
            return;

        _recentItemsLimit = limit;
        RefreshRecentNotes();
        RefreshRecentFlashcardSets();
        RefreshRecentMindMaps();
        RefreshRecentQuizzes();
    }

    public string SelectedFlashcardSortOptionKey
    {
        get => _selectedFlashcardSortOptionKey;
        set
        {
            var normalized = NormalizeFlashcardSortOptionKey(value);
            if (!SetProperty(ref _selectedFlashcardSortOptionKey, normalized))
                return;

            UpdateFlashcardSortOptionSelection();
            ApplySortToFlashcardSetsView();
            OnPropertyChanged(nameof(ActiveSortButtonText));
            SaveAppSettings();
        }
    }

    public string SelectedMindMapSortOptionKey
    {
        get => _selectedMindMapSortOptionKey;
        set
        {
            var normalized = NormalizeMindMapSortOptionKey(value);
            if (!SetProperty(ref _selectedMindMapSortOptionKey, normalized))
                return;

            UpdateMindMapSortOptionSelection();
            ApplySortToMindMapsView();
            OnPropertyChanged(nameof(ActiveSortButtonText));
            SaveAppSettings();
        }
    }

    public string SelectedQuizSortOptionKey
    {
        get => _selectedQuizSortOptionKey;
        set
        {
            var normalized = NormalizeQuizSortOptionKey(value);
            if (!SetProperty(ref _selectedQuizSortOptionKey, normalized))
                return;

            UpdateQuizSortOptionSelection();
            ApplySortToQuizzesView();
            OnPropertyChanged(nameof(ActiveSortButtonText));
            SaveAppSettings();
        }
    }

    public ObservableCollection<FlashcardSetViewModel> FlashcardSets { get; } = new();
    public bool HasFlashcardSets => FlashcardSets.Count > 0;
    public bool HasFlashcardSetGroups => FlashcardSetGroups.Count > 0;
    public bool HasUngroupedFlashcardSets => _hasUngroupedFlashcardSetsCache ??=
        FlashcardSets.Any(set => !set.Document.GroupId.HasValue && MatchesFlashcardSetFilters(set));

    private void NotifyHasUngroupedFlashcardSetsChanged()
    {
        _hasUngroupedFlashcardSetsCache = null;
        OnPropertyChanged(nameof(HasUngroupedFlashcardSets));
    }
    public int FlashcardSetCount => FlashcardSets.Count;
    public string FlashcardSetCountText => string.Format(LocalizationService.GetString("FlashcardSetCountFormat"), FlashcardSetCount);
    public ObservableCollection<MindMapViewModel> MindMaps { get; } = new();
    public bool HasMindMaps => MindMaps.Count > 0;
    public bool HasMindMapGroups => MindMapGroups.Count > 0;
    public bool HasUngroupedMindMaps => _hasUngroupedMindMapsCache ??=
        MindMaps.Any(map => !map.Document.GroupId.HasValue && MatchesMindMapFilters(map));

    private void NotifyHasUngroupedMindMapsChanged()
    {
        _hasUngroupedMindMapsCache = null;
        OnPropertyChanged(nameof(HasUngroupedMindMaps));
    }
    public int MindMapCount => MindMaps.Count;
    public string MindMapCountText => string.Format(LocalizationService.GetString("MindMapCountFormat"), MindMapCount);
    public ObservableCollection<QuizViewModel> Quizzes => _quizzes;
    public bool HasQuizzes => Quizzes.Count > 0;
    public bool HasQuizGroups => QuizGroups.Count > 0;
    public bool HasUngroupedQuizzes => _hasUngroupedQuizzesCache ??=
        Quizzes.Any(quiz => !quiz.Document.GroupId.HasValue && MatchesQuizFilters(quiz));

    private void NotifyHasUngroupedQuizzesChanged()
    {
        _hasUngroupedQuizzesCache = null;
        OnPropertyChanged(nameof(HasUngroupedQuizzes));
    }
    public int QuizCount => Quizzes.Count;
    public string QuizCountText => string.Format(LocalizationService.GetString("QuizCountFormat"), QuizCount);
    public ObservableCollection<FlashcardItem> Flashcards { get; set; } = new();

    
    public string NewFlashcardQuestion
    {
        get => _newFlashcardQuestion;
        set
        {
            _newFlashcardQuestion = value;
            OnPropertyChanged();
        }
    }

   
    public string NewFlashcardAnswer
    {
        get => _newFlashcardAnswer;
        set
        {
            _newFlashcardAnswer = value;
            OnPropertyChanged();
        }
    }

    private bool FilterUngroupedNotes(object? obj)
    {
        if (obj is not NoteCardViewModel note)
            return false;

        if (note.Document.GroupId.HasValue)
            return false;

        return MatchesSearch(note);
    }

    private bool MatchesSearch(NoteCardViewModel note)
    {
        if (_selectedTags.Count > 0)
        {
            var noteTags = note.Document.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!_selectedTags.All(noteTags.Contains))
                return false;
        }

        if (string.IsNullOrWhiteSpace(SearchQuery))
            return true;

        var tokens = SearchQuery
            .Split([' ', ',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return true;

        var searchable = BuildSearchText(note);
        return tokens.All(token => searchable.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private string BuildSearchText(NoteCardViewModel note)
    {
        var groupName = GetNoteGroupName(note);
        var lastModified = note.Document.LastModified.ToString("yyyy-MM-dd HH:mm dd MMM yyyy", CultureInfo.CurrentCulture);
        var createdAt = note.Document.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm dd MMM yyyy", CultureInfo.CurrentCulture);
        var scheduleSearchText = BuildScheduleSearchText(note.Document);
        var groupState = note.Document.GroupId.HasValue
            ? LocalizationService.GetString("Groups")
            : LocalizationService.GetString("UngroupedNotes");

        return string.Join(' ',
            note.Title,
            note.Content,
            note.TagsSearchText,
            groupName,
            groupState,
            note.Document.FontFamily,
            note.Document.FontSize.ToString(CultureInfo.InvariantCulture),
            lastModified,
            createdAt,
            scheduleSearchText);
    }

    private static string BuildScheduleSearchText(NoteDocument document)
    {
        var schedules = GetEffectiveSchedules(document);
        if (schedules.Count == 0)
            return string.Empty;

        return string.Join(' ', schedules.Select(entry =>
            $"{entry.ScheduledAt:yyyy-MM-dd HH:mm dd MMM yyyy} {entry.Note}"));
    }

    private string GetNoteGroupName(NoteCardViewModel note)
    {
        if (!note.Document.GroupId.HasValue)
            return LocalizationService.GetString("UngroupedNotes");

        if (_groupMetadata.TryGetValue(note.Document.GroupId.Value, out var metadata)
            && !string.IsNullOrWhiteSpace(metadata.Name))
        {
            return metadata.Name;
        }

        return string.Format(
            LocalizationService.GetString("GroupTitleFormat"),
            note.Document.GroupId.Value.ToString()[..4].ToUpperInvariant());
    }

    private void RefreshTagFilters<TDoc>(
        IEnumerable<TDoc> source,
        Func<TDoc, List<string>?> tagsSelector,
        HashSet<string> selectedTags,
        ObservableCollection<TagFilterItemViewModel> targetFilters,
        Action<string, bool> onSelectionChanged,
        Action notifyChanged)
    {
        var tags = source
            .SelectMany(item => tagsSelector(item) ?? new List<string>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        selectedTags.RemoveWhere(selected => !tags.Any(tag => string.Equals(tag, selected, StringComparison.OrdinalIgnoreCase)));

        targetFilters.Clear();
        foreach (var tag in tags)
        {
            var isSelected = selectedTags.Contains(tag);
            targetFilters.Add(new TagFilterItemViewModel(tag, isSelected, onSelectionChanged));
        }

        notifyChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshAvailableTags()
    {
        RefreshTagFilters(Notes, note => note.Document.Tags, _selectedTags, TagFilters, SetTagFilterSelected, () =>
        {
            OnPropertyChanged(nameof(HasTagFilters));
            OnPropertyChanged(nameof(ActiveHasTagFilters));
            OnPropertyChanged(nameof(HasActiveTagFilters));
            OnPropertyChanged(nameof(TagFilterButtonText));
            OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
            OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        });
    }

    private void ClearActiveTagFilters()
    {
        if (IsMindMapsView)
            ClearMindMapTagFilters();
        else if (IsQuizzesView)
            ClearQuizTagFilters();
        else if (IsFlashcardsView)
            ClearFlashcardTagFilters();
        else
            ClearTagFilters();
    }

    private void ClearTagFilters()
    {
        if (_selectedTags.Count == 0)
            return;

        _selectedTags.Clear();
        foreach (var tag in TagFilters)
            tag.IsSelected = false;

        OnPropertyChanged(nameof(HasActiveTagFilters));
        OnPropertyChanged(nameof(TagFilterButtonText));
        OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        CommandManager.InvalidateRequerySuggested();
        ApplyFilters();
    }

    public void SetFlashcardTagFilterSelected(string tag, bool isSelected)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        if (isSelected)
            _selectedFlashcardTags.Add(tag);
        else
            _selectedFlashcardTags.Remove(tag);

        OnPropertyChanged(nameof(HasActiveFlashcardTagFilters));
        OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
        OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        CommandManager.InvalidateRequerySuggested();
        ApplyFlashcardFilters();
    }

    public void SetMindMapTagFilterSelected(string tag, bool isSelected)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        if (isSelected)
            _selectedMindMapTags.Add(tag);
        else
            _selectedMindMapTags.Remove(tag);

        OnPropertyChanged(nameof(HasActiveMindMapTagFilters));
        OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
        OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        CommandManager.InvalidateRequerySuggested();
        ApplyMindMapFilters();
    }

    public void SetQuizTagFilterSelected(string tag, bool isSelected)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        if (isSelected)
            _selectedQuizTags.Add(tag);
        else
            _selectedQuizTags.Remove(tag);

        OnPropertyChanged(nameof(HasActiveQuizTagFilters));
        OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
        OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        CommandManager.InvalidateRequerySuggested();
        ApplyQuizFilters();
    }

    private void NotifyActiveDashboardChromeChanged()
    {
        OnPropertyChanged(nameof(ActiveSearchQuery));
        OnPropertyChanged(nameof(ActiveSortButtonText));
        OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        OnPropertyChanged(nameof(ActiveSortOptions));
        OnPropertyChanged(nameof(ActiveTagFilters));
        OnPropertyChanged(nameof(ActiveHasTagFilters));
        OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
        NotifyNotesDashboardPresentationChanged();
        NotifyFlashcardsDashboardPresentationChanged();
        NotifyMindMapsDashboardPresentationChanged();
        NotifyQuizzesDashboardPresentationChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private void ClearFlashcardTagFilters()
    {
        if (_selectedFlashcardTags.Count == 0)
            return;

        _selectedFlashcardTags.Clear();
        foreach (var tag in FlashcardTagFilters)
            tag.IsSelected = false;

        OnPropertyChanged(nameof(HasActiveFlashcardTagFilters));
        OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
        OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        CommandManager.InvalidateRequerySuggested();
        ApplyFlashcardFilters();
    }

    private void ClearMindMapTagFilters()
    {
        if (_selectedMindMapTags.Count == 0)
            return;

        _selectedMindMapTags.Clear();
        foreach (var tag in MindMapTagFilters)
            tag.IsSelected = false;

        OnPropertyChanged(nameof(HasActiveMindMapTagFilters));
        OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
        OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        CommandManager.InvalidateRequerySuggested();
        ApplyMindMapFilters();
    }

    private void ClearQuizTagFilters()
    {
        if (_selectedQuizTags.Count == 0)
            return;

        _selectedQuizTags.Clear();
        foreach (var tag in QuizTagFilters)
            tag.IsSelected = false;

        OnPropertyChanged(nameof(HasActiveQuizTagFilters));
        OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
        OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        CommandManager.InvalidateRequerySuggested();
        ApplyQuizFilters();
    }

    private void ApplyFilters()
    {
        InvalidateUngroupedVisibleCount();
        _notesView.Refresh();
        RebuildGroups();
        RefreshRecentNotes();
        RefreshCalendarScheduledNotes();
    }

    private void RefreshAvailableFlashcardTags()
    {
        RefreshTagFilters(FlashcardSets, set => set.Document.Tags, _selectedFlashcardTags, FlashcardTagFilters, SetFlashcardTagFilterSelected, () =>
        {
            OnPropertyChanged(nameof(HasFlashcardTagFilters));
            OnPropertyChanged(nameof(ActiveHasTagFilters));
            OnPropertyChanged(nameof(HasActiveFlashcardTagFilters));
            OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
            OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        });
    }

    private void RefreshAvailableMindMapTags()
    {
        RefreshTagFilters(MindMaps, map => map.Document.Tags, _selectedMindMapTags, MindMapTagFilters, SetMindMapTagFilterSelected, () =>
        {
            OnPropertyChanged(nameof(HasMindMapTagFilters));
            OnPropertyChanged(nameof(ActiveHasTagFilters));
            OnPropertyChanged(nameof(HasActiveMindMapTagFilters));
            OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
            OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        });
    }

    private void RefreshAvailableQuizTags()
    {
        RefreshTagFilters(Quizzes, quiz => quiz.Document.Tags, _selectedQuizTags, QuizTagFilters, SetQuizTagFilterSelected, () =>
        {
            OnPropertyChanged(nameof(HasQuizTagFilters));
            OnPropertyChanged(nameof(ActiveHasTagFilters));
            OnPropertyChanged(nameof(HasActiveQuizTagFilters));
            OnPropertyChanged(nameof(ActiveHasActiveTagFilters));
            OnPropertyChanged(nameof(ActiveTagFilterButtonText));
        });
    }

    private void ApplyFlashcardFilters()
    {
        _flashcardSetsView.Refresh();
        RebuildFlashcardSetGroups();
        RefreshRecentFlashcardSets();
        NotifyFlashcardSetsChanged();
    }

    private void ApplyMindMapFilters()
    {
        _mindMapsView.Refresh();
        RebuildMindMapGroups();
        RefreshRecentMindMaps();
        NotifyMindMapsChanged();
    }

    private void ApplyQuizFilters()
    {
        _quizzesView.Refresh();
        RebuildQuizGroups();
        RefreshRecentQuizzes();
        NotifyQuizzesChanged();
    }

    private bool FilterFlashcardSet(object obj)
    {
        if (obj is not FlashcardSetViewModel set)
            return false;

        if (set.Document.GroupId.HasValue)
            return false;

        return MatchesFlashcardSetFilters(set);
    }

    private bool MatchesFlashcardSetFilters(FlashcardSetViewModel set)
    {
        if (!IsFlashcardGroupsSectionVisible)
            return false;

        if (_selectedFlashcardTags.Count > 0)
        {
            var tags = set.Document.Tags ?? new List<string>();
            if (!tags.Any(tag => _selectedFlashcardTags.Contains(tag)))
                return false;
        }

        var query = FlashcardSearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return set.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
               || set.TagsDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
               || set.Document.Cards.Any(card =>
                   (card.Question ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)
                   || (card.Answer ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)
                   || (card.Category ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private bool FilterMindMap(object obj)
    {
        if (obj is not MindMapViewModel map)
            return false;

        if (map.Document.GroupId.HasValue)
            return false;

        return MatchesMindMapFilters(map);
    }

    private bool MatchesMindMapFilters(MindMapViewModel map)
    {
        if (!IsMindMapGroupsSectionVisible)
            return false;

        if (_selectedMindMapTags.Count > 0)
        {
            var tags = map.Document.Tags ?? new List<string>();
            if (!tags.Any(tag => _selectedMindMapTags.Contains(tag)))
                return false;
        }

        var query = MindMapSearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return map.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
               || map.TagsDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
               || MindMapContainsText(map.Document.Root, query);
    }

    private bool FilterQuiz(object obj)
    {
        if (obj is not QuizViewModel quiz)
            return false;

        if (quiz.Document.GroupId.HasValue)
            return false;

        return MatchesQuizFilters(quiz);
    }

    private bool MatchesQuizFilters(QuizViewModel quiz)
    {
        if (!IsQuizGroupsSectionVisible)
            return false;

        if (_selectedQuizTags.Count > 0)
        {
            var tags = quiz.Document.Tags ?? new List<string>();
            if (!tags.Any(tag => _selectedQuizTags.Contains(tag)))
                return false;
        }

        var query = QuizSearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return quiz.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
               || quiz.TagsDisplay.Contains(query, StringComparison.OrdinalIgnoreCase)
               || quiz.Document.Questions.Any(question =>
                   (question.Question ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)
                   || (question.Explanation ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)
                   || (question.PlainTextAnswer ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)
                   || question.Options.Any(option => (option.Text ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool MindMapContainsText(MindMapNode? node, string query)
    {
        if (node is null)
            return false;

        return (node.Text ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)
               || node.Children.Any(child => MindMapContainsText(child, query));
    }

    private void SelectAllVisibleMassItems()
    {
        if (!IsMassSelectMode)
            return;

        switch (_massSelectDashboard)
        {
            case DashboardFlashcards:
                ToggleAllVisibleFlashcardSets();
                break;
            case DashboardMindMaps:
                ToggleAllVisibleMindMaps();
                break;
            case DashboardQuizzes:
                ToggleAllVisibleQuizzes();
                break;
            default:
                ToggleAllVisibleNotes();
                break;
        }
    }

    private void ToggleAllVisibleNotes()
    {
        var visible = Notes.Where(MatchesSearch).ToList();
        if (visible.Count == 0)
            return;

        var allVisibleSelected = visible.All(note => _massSelectedNoteIds.Contains(note.Document.Id));
        var targetNotes = allVisibleSelected ? Notes.ToList() : visible;
        foreach (var note in targetNotes)
            SetNoteSelectedState(note, !allVisibleSelected, notify: false);

        NotifyMassSelectionChanged();
    }

    private void ToggleAllVisibleFlashcardSets()
    {
        var visible = FlashcardSets.Where(MatchesFlashcardSetFilters).ToList();
        if (visible.Count == 0)
            return;

        var allVisibleSelected = visible.All(set => _massSelectedFlashcardSetIds.Contains(set.Document.Id));
        var targetSets = allVisibleSelected ? FlashcardSets.ToList() : visible;
        foreach (var set in targetSets)
            SetFlashcardSetSelectedState(set, !allVisibleSelected, notify: false);

        NotifyMassSelectionChanged();
    }

    private void ToggleAllVisibleMindMaps()
    {
        var visible = MindMaps.Where(MatchesMindMapFilters).ToList();
        if (visible.Count == 0)
            return;

        var allVisibleSelected = visible.All(map => _massSelectedMindMapIds.Contains(map.Document.Id));
        var targetMaps = allVisibleSelected ? MindMaps.ToList() : visible;
        foreach (var map in targetMaps)
            SetMindMapSelectedState(map, !allVisibleSelected, notify: false);

        NotifyMassSelectionChanged();
    }

    private void ToggleAllVisibleQuizzes()
    {
        var visible = Quizzes.Where(MatchesQuizFilters).ToList();
        if (visible.Count == 0)
            return;

        var allVisibleSelected = visible.All(quiz => _massSelectedQuizIds.Contains(quiz.Document.Id));
        var targetQuizzes = allVisibleSelected ? Quizzes.ToList() : visible;
        foreach (var quiz in targetQuizzes)
            SetQuizSelectedState(quiz, !allVisibleSelected, notify: false);

        NotifyMassSelectionChanged();
    }

    private void DeleteSelectedMassItems()
    {
        switch (_massSelectDashboard)
        {
            case DashboardFlashcards:
                DeleteSelectedFlashcardSetsBatch();
                break;
            case DashboardMindMaps:
                DeleteSelectedMindMapsBatch();
                break;
            case DashboardQuizzes:
                DeleteSelectedQuizzesBatch();
                break;
            default:
                DeleteSelectedNotesBatch();
                break;
        }

        NotifyMassSelectionChanged();
    }

    private void DeleteSelectedFlashcardSetsBatch()
    {
        var sets = GetMassSelectedFlashcardSets();
        if (sets.Count == 0)
            return;

        foreach (var set in sets)
        {
            _massSelectedFlashcardSetIds.Remove(set.Document.Id);
            set.IsSelectedInMassSelect = false;
        }

        if (MotionHost is not null && sets.Count <= MassDeleteAnimatedMax)
        {
            MotionHost.RequestDeleteBatch(
                DashboardMotionScope.Flashcards,
                sets.Select(set => set.Document.Id).ToList(),
                () => CommitRemoveFlashcardSetsBatch(sets),
                ApplyFlashcardSetDeleteReaction);
            return;
        }

        MotionHost?.PrepareMutation(DashboardMotionScope.Flashcards);
        CommitRemoveFlashcardSetsBatch(sets);
        ApplyFlashcardSetDeleteReaction();
        MotionHost?.CompleteMutation(DashboardMotionScope.Flashcards);
    }

    private void CommitRemoveFlashcardSetsBatch(IReadOnlyList<FlashcardSetViewModel> sets)
    {
        foreach (var set in sets)
            FlashcardSets.Remove(set);
    }

    private void DeleteSelectedMindMapsBatch()
    {
        var maps = GetMassSelectedMindMaps();
        if (maps.Count == 0)
            return;

        foreach (var map in maps)
        {
            _massSelectedMindMapIds.Remove(map.Document.Id);
            map.IsSelectedInMassSelect = false;
        }

        if (MotionHost is not null && maps.Count <= MassDeleteAnimatedMax)
        {
            MotionHost.RequestDeleteBatch(
                DashboardMotionScope.MindMaps,
                maps.Select(map => map.Document.Id).ToList(),
                () => CommitRemoveMindMapsBatch(maps),
                ApplyMindMapDeleteReaction);
            return;
        }

        MotionHost?.PrepareMutation(DashboardMotionScope.MindMaps);
        CommitRemoveMindMapsBatch(maps);
        ApplyMindMapDeleteReaction();
        MotionHost?.CompleteMutation(DashboardMotionScope.MindMaps);
    }

    private void CommitRemoveMindMapsBatch(IReadOnlyList<MindMapViewModel> maps)
    {
        foreach (var map in maps)
            MindMaps.Remove(map);
    }

    private void DeleteSelectedQuizzesBatch()
    {
        var quizzes = GetMassSelectedQuizzes();
        if (quizzes.Count == 0)
            return;

        foreach (var quiz in quizzes)
        {
            _massSelectedQuizIds.Remove(quiz.Document.Id);
            quiz.IsSelectedInMassSelect = false;
        }

        if (MotionHost is not null && quizzes.Count <= MassDeleteAnimatedMax)
        {
            MotionHost.RequestDeleteBatch(
                DashboardMotionScope.Quizzes,
                quizzes.Select(quiz => quiz.Document.Id).ToList(),
                () => CommitRemoveQuizzesBatch(quizzes),
                ApplyQuizDeleteReaction);
            return;
        }

        MotionHost?.PrepareMutation(DashboardMotionScope.Quizzes);
        CommitRemoveQuizzesBatch(quizzes);
        ApplyQuizDeleteReaction();
        MotionHost?.CompleteMutation(DashboardMotionScope.Quizzes);
    }

    private void CommitRemoveQuizzesBatch(IReadOnlyList<QuizViewModel> quizzes)
    {
        foreach (var quiz in quizzes)
            Quizzes.Remove(quiz);
    }

    private void DeleteSelectedNotesBatch()
    {
        var notes = GetMassSelectedNotes();
        if (notes.Count == 0)
            return;

        foreach (var note in notes)
        {
            _massSelectedNoteIds.Remove(note.Document.Id);
            note.IsSelectedInMassSelect = false;
        }

        if (MotionHost is not null && notes.Count <= MassDeleteAnimatedMax)
        {
            MotionHost.RequestDeleteBatch(
                DashboardMotionScope.Notes,
                notes.Select(note => note.Document.Id).ToList(),
                () => CommitRemoveNotesBatch(notes),
                ApplyNotesDeleteReaction);
            return;
        }

        MotionHost?.PrepareMutation(DashboardMotionScope.Notes);
        CommitRemoveNotesBatch(notes);
        ApplyNotesDeleteReaction();
        MotionHost?.CompleteMutation(DashboardMotionScope.Notes);
    }

    private void CommitRemoveNotesBatch(IReadOnlyList<NoteCardViewModel> notes)
    {
        foreach (var note in notes)
            Notes.Remove(note);
    }

    private void RemoveSelectedFromGroups()
    {
        if (!CanUngroupSelectedNotes())
            return;

        foreach (var item in GetSelectedGroupableDocuments())
        {
            item.GroupId = null;
            item.LastModified = DateTime.Now;
            item.NotifyChanged();
        }

        NormalizeActiveMassSelectGroups();
        RebuildActiveMassSelectGroups();
        SaveActiveMassSelectDashboard();
        RefreshActiveMassSelectDashboard();
    }

    private void GroupSelectedNotes()
    {
        var selected = GetSelectedGroupableDocuments().ToList();
        if (selected.Count < 2)
            return;

        var selectedGroupIds = selected.Select(item => item.GroupId).Distinct().ToList();
        if (selectedGroupIds.Count == 1 && selectedGroupIds[0].HasValue)
            return;

        var targetGroupId = selected.FirstOrDefault(item => item.GroupId.HasValue)?.GroupId ?? Guid.NewGuid();
        var movingIds = selected
            .Where(item => item.GroupId != targetGroupId)
            .Select(item => item.Id)
            .Distinct()
            .ToList();

        if (movingIds.Count == 0)
            return;

        var scope = ResolveMassSelectMotionScope();

        void CommitGroup()
        {
            EnsureActiveMassSelectGroupMetadata(targetGroupId);

            foreach (var item in selected)
            {
                if (item.GroupId == targetGroupId)
                    continue;

                item.GroupId = targetGroupId;
                item.LastModified = DateTime.Now;
                item.NotifyChanged();
            }

            NormalizeActiveMassSelectGroups();
            RebuildActiveMassSelectGroups();
            SaveActiveMassSelectDashboard();
            RefreshActiveMassSelectDashboard();
        }

        if (MotionHost is not null)
        {
            // GroupSlide Loaded enter is the single pop; do not ScheduleGroupCardEnter again.
            MotionHost.RequestItemMoveBatch(scope, movingIds, CommitGroup);
            return;
        }

        CommitGroup();
    }

    private DashboardMotionScope ResolveMassSelectMotionScope()
        => _massSelectDashboard switch
        {
            DashboardFlashcards => DashboardMotionScope.Flashcards,
            DashboardMindMaps => DashboardMotionScope.MindMaps,
            DashboardQuizzes => DashboardMotionScope.Quizzes,
            _ => DashboardMotionScope.Notes
        };

    private void PinSelectedNotes()
    {
        var changed = false;
        foreach (var item in GetSelectedGroupableDocuments().Where(item => !item.IsPinned))
        {
            item.IsPinned = true;
            item.LastModified = DateTime.Now;
            item.NotifyChanged();
            changed = true;
        }

        if (!changed)
            return;

        ReorderActiveMassSelectDashboard();
        RebuildActiveMassSelectGroups();
        RefreshActiveMassSelectDashboard();
        SaveActiveMassSelectDashboard();
    }

    private void UnpinSelectedNotes()
    {
        var changed = false;
        foreach (var item in GetSelectedGroupableDocuments().Where(item => item.IsPinned))
        {
            item.IsPinned = false;
            item.LastModified = DateTime.Now;
            item.NotifyChanged();
            changed = true;
        }

        if (!changed)
            return;

        ReorderActiveMassSelectDashboard();
        RebuildActiveMassSelectGroups();
        RefreshActiveMassSelectDashboard();
        SaveActiveMassSelectDashboard();
    }

    private void DuplicateSelectedNotes()
    {
        switch (_massSelectDashboard)
        {
            case DashboardFlashcards:
                DuplicateSelectedFlashcardSets();
                break;
            case DashboardMindMaps:
                DuplicateSelectedMindMaps();
                break;
            case DashboardQuizzes:
                DuplicateSelectedQuizzes();
                break;
            default:
                DuplicateSelectedScriblucent();
                break;
        }
    }

    private void DuplicateSelectedScriblucent()
    {
        var duplicates = GetMassSelectedNotes()
            .Select(note => new NoteDocument
            {
                Title = $"{note.Document.Title} (Copy)",
                Content = note.Document.Content,
                Tags = note.Document.Tags?.ToList() ?? new List<string>(),
                FontFamily = note.Document.FontFamily,
                FontSize = note.Document.FontSize,
                CreatedAt = DateTime.Now,
                LastModified = DateTime.Now,
                GroupId = note.Document.GroupId,
                IsPinned = note.Document.IsPinned
            })
            .ToList();

        if (duplicates.Count == 0)
            return;

        var cards = duplicates.Select(CreateNoteCard).ToList();
        var addedIds = duplicates.Select(document => document.Id).ToList();

        _notesBatchAddInProgress = true;
        try
        {
            MotionHost?.BeginBatchMutation(DashboardMotionScope.Notes);
            foreach (var (document, card) in duplicates.Zip(cards))
            {
                MotionHost?.RegisterBatchItemAdd(DashboardMotionScope.Notes, document.Id);
                Notes.Add(card);
            }

            SaveNotes();
            CompleteNotesBatchAdd(addedIds);
        }
        finally
        {
            _notesBatchAddInProgress = false;
        }
    }

    private void CompleteNotesBatchAdd(IReadOnlyList<Guid> addedIds)
    {
        RebuildGroups();

        void AfterMotion()
        {
            ApplyFilters();
            RecalculateUngroupedVisibleCount();
            NotifyNotesChanged();
            RefreshActivityStats();
            EnsureMassSelectionConsistency();
        }

        if (MotionHost is null)
            AfterMotion();
        else
            MotionHost.CompleteBatchAdd(DashboardMotionScope.Notes, addedIds, AfterMotion);
    }

    private void DuplicateSelectedFlashcardSets()
    {
        var documents = GetMassSelectedFlashcardSets()
            .Select(set => CloneFlashcardSetDocument(set.Document))
            .ToList();

        if (documents.Count == 0)
            return;

        foreach (var document in documents)
            NormalizeFlashcardSetDocument(document);

        var addedIds = documents.Select(document => document.Id).ToList();

        _flashcardBatchAddInProgress = true;
        try
        {
            MotionHost?.BeginBatchMutation(DashboardMotionScope.Flashcards);
            foreach (var document in documents)
            {
                MotionHost?.RegisterBatchItemAdd(DashboardMotionScope.Flashcards, document.Id);
                FlashcardSets.Add(new FlashcardSetViewModel(document));
            }

            SaveFlashcardSets();
            CompleteFlashcardBatchAdd(addedIds);
        }
        finally
        {
            _flashcardBatchAddInProgress = false;
        }
    }

    private void CompleteFlashcardBatchAdd(IReadOnlyList<Guid> addedIds)
    {
        RebuildFlashcardSetGroups();

        void AfterMotion()
        {
            ReorderFlashcardSets();
            NormalizeFlashcardSetGroups();
            RefreshAvailableFlashcardTags();
            ApplyFlashcardFilters();
            NotifyFlashcardSetsChanged();
            RefreshRecentFlashcardSets();
        }

        if (MotionHost is null)
            AfterMotion();
        else
            MotionHost.CompleteBatchAdd(DashboardMotionScope.Flashcards, addedIds, AfterMotion);
    }

    private void DuplicateSelectedMindMaps()
    {
        var documents = GetMassSelectedMindMaps()
            .Select(map => CloneMindMapDocument(map.Document))
            .ToList();

        if (documents.Count == 0)
            return;

        foreach (var document in documents)
            NormalizeMindMapDocument(document);

        var addedIds = documents.Select(document => document.Id).ToList();

        _mindMapBatchAddInProgress = true;
        try
        {
            MotionHost?.BeginBatchMutation(DashboardMotionScope.MindMaps);
            foreach (var document in documents)
            {
                MotionHost?.RegisterBatchItemAdd(DashboardMotionScope.MindMaps, document.Id);
                MindMaps.Add(new MindMapViewModel(document));
            }

            SaveMindMaps();
            CompleteMindMapBatchAdd(addedIds);
        }
        finally
        {
            _mindMapBatchAddInProgress = false;
        }
    }

    private void CompleteMindMapBatchAdd(IReadOnlyList<Guid> addedIds)
    {
        RebuildMindMapGroups();

        void AfterMotion()
        {
            ReorderMindMaps();
            NormalizeMindMapGroups();
            RefreshAvailableMindMapTags();
            ApplyMindMapFilters();
            NotifyMindMapsChanged();
            RefreshRecentMindMaps();
        }

        if (MotionHost is null)
            AfterMotion();
        else
            MotionHost.CompleteBatchAdd(DashboardMotionScope.MindMaps, addedIds, AfterMotion);
    }

    private void DuplicateSelectedQuizzes()
    {
        var documents = GetMassSelectedQuizzes()
            .Select(quiz => CloneQuizDocument(quiz.Document))
            .ToList();

        if (documents.Count == 0)
            return;

        foreach (var document in documents)
            NormalizeQuizDocument(document);

        var addedIds = documents.Select(document => document.Id).ToList();

        _quizBatchAddInProgress = true;
        try
        {
            MotionHost?.BeginBatchMutation(DashboardMotionScope.Quizzes);
            foreach (var document in documents)
            {
                MotionHost?.RegisterBatchItemAdd(DashboardMotionScope.Quizzes, document.Id);
                Quizzes.Add(new QuizViewModel(document));
            }

            SaveQuizzes();
            CompleteQuizBatchAdd(addedIds);
        }
        finally
        {
            _quizBatchAddInProgress = false;
        }
    }

    private void CompleteQuizBatchAdd(IReadOnlyList<Guid> addedIds)
    {
        RebuildQuizGroups();

        void AfterMotion()
        {
            ReorderQuizzes();
            NormalizeQuizGroups();
            RefreshAvailableQuizTags();
            ApplyQuizFilters();
            NotifyQuizzesChanged();
            RefreshRecentQuizzes();
        }

        if (MotionHost is null)
            AfterMotion();
        else
            MotionHost.CompleteBatchAdd(DashboardMotionScope.Quizzes, addedIds, AfterMotion);
    }

    private bool CanUngroupSelectedNotes()
    {
        if (!IsMassSelectMode || ActiveMassSelectedCount == 0)
            return false;

        return GetSelectedGroupableDocuments().All(item => item.GroupId.HasValue);
    }

    private bool CanGroupSelectedNotes()
    {
        if (!IsMassSelectMode || ActiveMassSelectedCount < 2)
            return false;

        var selectedGroupIds = GetSelectedGroupableDocuments()
            .Select(item => item.GroupId)
            .Distinct()
            .ToList();

        return selectedGroupIds.Count > 1 || !selectedGroupIds[0].HasValue;
    }

    private bool CanPinSelectedNotes()
    {
        return IsMassSelectMode
               && ActiveMassSelectedCount > 0
               && GetSelectedGroupableDocuments().Any(item => !item.IsPinned);
    }

    private bool CanUnpinSelectedNotes()
    {
        return IsMassSelectMode
               && ActiveMassSelectedCount > 0
               && GetSelectedGroupableDocuments().Any(item => item.IsPinned);
    }

    private bool CanDuplicateSelectedNotes()
    {
        return IsMassSelectMode && ActiveMassSelectedCount > 0;
    }

    private sealed class MassSelectDocumentAdapter
    {
        public required Guid Id { get; init; }
        public required Func<Guid?> GetGroupId { get; init; }
        public required Action<Guid?> SetGroupId { get; init; }
        public required Func<bool> GetIsPinned { get; init; }
        public required Action<bool> SetIsPinned { get; init; }
        public required Action<DateTime> SetLastModified { get; init; }
        public required List<string> Tags { get; init; }
        public required Action NotifyChanged { get; init; }

        public Guid? GroupId
        {
            get => GetGroupId();
            set => SetGroupId(value);
        }

        public bool IsPinned
        {
            get => GetIsPinned();
            set => SetIsPinned(value);
        }

        public DateTime LastModified
        {
            set => SetLastModified(value);
        }
    }

    private List<MassSelectDocumentAdapter> GetSelectedGroupableDocuments()
    {
        return _massSelectDashboard switch
        {
            DashboardFlashcards => GetMassSelectedFlashcardSets().Select(set => new MassSelectDocumentAdapter
            {
                Id = set.Document.Id,
                GetGroupId = () => set.Document.GroupId,
                SetGroupId = value => set.Document.GroupId = value,
                GetIsPinned = () => set.Document.IsPinned,
                SetIsPinned = value => set.Document.IsPinned = value,
                SetLastModified = value => set.Document.LastModified = value,
                Tags = set.Document.Tags,
                NotifyChanged = set.NotifyChanged
            }).ToList(),
            DashboardMindMaps => GetMassSelectedMindMaps().Select(map => new MassSelectDocumentAdapter
            {
                Id = map.Document.Id,
                GetGroupId = () => map.Document.GroupId,
                SetGroupId = value => map.Document.GroupId = value,
                GetIsPinned = () => map.Document.IsPinned,
                SetIsPinned = value => map.Document.IsPinned = value,
                SetLastModified = value => map.Document.LastModified = value,
                Tags = map.Document.Tags,
                NotifyChanged = map.NotifyChanged
            }).ToList(),
            DashboardQuizzes => GetMassSelectedQuizzes().Select(quiz => new MassSelectDocumentAdapter
            {
                Id = quiz.Document.Id,
                GetGroupId = () => quiz.Document.GroupId,
                SetGroupId = value => quiz.Document.GroupId = value,
                GetIsPinned = () => quiz.Document.IsPinned,
                SetIsPinned = value => quiz.Document.IsPinned = value,
                SetLastModified = value => quiz.Document.LastModified = value,
                Tags = quiz.Document.Tags,
                NotifyChanged = quiz.NotifyChanged
            }).ToList(),
            _ => GetMassSelectedNotes().Select(note => new MassSelectDocumentAdapter
            {
                Id = note.Document.Id,
                GetGroupId = () => note.Document.GroupId,
                SetGroupId = value => note.Document.GroupId = value,
                GetIsPinned = () => note.Document.IsPinned,
                SetIsPinned = value => note.Document.IsPinned = value,
                SetLastModified = value => note.Document.LastModified = value,
                Tags = note.Document.Tags,
                NotifyChanged = note.NotifyContentChanged
            }).ToList()
        };
    }

    private List<MassSelectDocumentAdapter> GetSelectedTaggableDocuments() => GetSelectedGroupableDocuments();

    private List<NoteCardViewModel> GetMassSelectedNotes()
    {
        if (_massSelectedNoteIds.Count == 0)
            return new List<NoteCardViewModel>();

        return Notes
            .Where(note => _massSelectedNoteIds.Contains(note.Document.Id))
            .ToList();
    }

    private List<FlashcardSetViewModel> GetMassSelectedFlashcardSets()
    {
        if (_massSelectedFlashcardSetIds.Count == 0)
            return new List<FlashcardSetViewModel>();

        return FlashcardSets
            .Where(set => _massSelectedFlashcardSetIds.Contains(set.Document.Id))
            .ToList();
    }

    private List<MindMapViewModel> GetMassSelectedMindMaps()
    {
        if (_massSelectedMindMapIds.Count == 0)
            return new List<MindMapViewModel>();

        return MindMaps
            .Where(map => _massSelectedMindMapIds.Contains(map.Document.Id))
            .ToList();
    }

    private List<QuizViewModel> GetMassSelectedQuizzes()
    {
        if (_massSelectedQuizIds.Count == 0)
            return new List<QuizViewModel>();

        return Quizzes
            .Where(quiz => _massSelectedQuizIds.Contains(quiz.Document.Id))
            .ToList();
    }

    private void SetNoteSelectedState(NoteCardViewModel note, bool isSelected, bool notify = true)
    {
        if (isSelected)
        {
            if (_massSelectedNoteIds.Add(note.Document.Id))
                note.IsSelectedInMassSelect = true;
        }
        else
        {
            if (_massSelectedNoteIds.Remove(note.Document.Id))
                note.IsSelectedInMassSelect = false;
        }

        if (notify)
            NotifyMassSelectionChanged();
    }

    private void SetFlashcardSetSelectedState(FlashcardSetViewModel set, bool isSelected, bool notify = true)
    {
        if (isSelected)
        {
            if (_massSelectedFlashcardSetIds.Add(set.Document.Id))
                set.IsSelectedInMassSelect = true;
        }
        else
        {
            if (_massSelectedFlashcardSetIds.Remove(set.Document.Id))
                set.IsSelectedInMassSelect = false;
        }

        if (notify)
            NotifyMassSelectionChanged();
    }

    private void SetMindMapSelectedState(MindMapViewModel map, bool isSelected, bool notify = true)
    {
        if (isSelected)
        {
            if (_massSelectedMindMapIds.Add(map.Document.Id))
                map.IsSelectedInMassSelect = true;
        }
        else
        {
            if (_massSelectedMindMapIds.Remove(map.Document.Id))
                map.IsSelectedInMassSelect = false;
        }

        if (notify)
            NotifyMassSelectionChanged();
    }

    private void SetQuizSelectedState(QuizViewModel quiz, bool isSelected, bool notify = true)
    {
        if (isSelected)
        {
            if (_massSelectedQuizIds.Add(quiz.Document.Id))
                quiz.IsSelectedInMassSelect = true;
        }
        else
        {
            if (_massSelectedQuizIds.Remove(quiz.Document.Id))
                quiz.IsSelectedInMassSelect = false;
        }

        if (notify)
            NotifyMassSelectionChanged();
    }

    private void EnsureActiveMassSelectGroupMetadata(Guid groupId)
    {
        switch (_massSelectDashboard)
        {
            case DashboardFlashcards:
                EnsureFlashcardSetGroupMetadata(groupId);
                break;
            case DashboardMindMaps:
                if (!_mindMapGroupMetadata.ContainsKey(groupId))
                    _mindMapGroupMetadata[groupId] = new MindMapGroupData
                    {
                        GroupId = groupId,
                        Name = string.Format(LocalizationService.GetString("GroupTitleFormat"), groupId.ToString()[..4].ToUpperInvariant()),
                        BackgroundColor = DefaultGroupBackground
                    };
                break;
            case DashboardQuizzes:
                EnsureQuizGroupMetadata(groupId);
                break;
            default:
                EnsureGroupMetadata(groupId);
                break;
        }
    }

    private void NormalizeActiveMassSelectGroups()
    {
        switch (_massSelectDashboard)
        {
            case DashboardFlashcards:
                NormalizeFlashcardSetGroups();
                break;
            case DashboardMindMaps:
                NormalizeMindMapGroups();
                break;
            case DashboardQuizzes:
                NormalizeQuizGroups();
                break;
            default:
                NormalizeGroups();
                break;
        }
    }

    private void RebuildActiveMassSelectGroups()
    {
        switch (_massSelectDashboard)
        {
            case DashboardFlashcards:
                RebuildFlashcardSetGroups();
                break;
            case DashboardMindMaps:
                RebuildMindMapGroups();
                break;
            case DashboardQuizzes:
                RebuildQuizGroups();
                break;
            default:
                RebuildGroups();
                break;
        }
    }

    private void ReorderActiveMassSelectDashboard()
    {
        switch (_massSelectDashboard)
        {
            case DashboardFlashcards:
                ReorderFlashcardSets();
                break;
            case DashboardMindMaps:
                ReorderMindMaps();
                break;
            case DashboardQuizzes:
                ReorderQuizzes();
                break;
        }
    }

    private void SaveActiveMassSelectDashboard()
    {
        switch (_massSelectDashboard)
        {
            case DashboardFlashcards:
                SaveFlashcardSets();
                break;
            case DashboardMindMaps:
                SaveMindMaps();
                break;
            case DashboardQuizzes:
                SaveQuizzes();
                break;
            default:
                SaveNotes();
                break;
        }
    }

    private void RefreshActiveMassSelectDashboard()
    {
        switch (_massSelectDashboard)
        {
            case DashboardFlashcards:
                RefreshAvailableFlashcardTags();
                ApplyFlashcardFilters();
                NotifyFlashcardSetsChanged();
                break;
            case DashboardMindMaps:
                RefreshAvailableMindMapTags();
                ApplyMindMapFilters();
                NotifyMindMapsChanged();
                break;
            case DashboardQuizzes:
                RefreshAvailableQuizTags();
                ApplyQuizFilters();
                NotifyQuizzesChanged();
                break;
            default:
                RefreshAvailableTags();
                ApplyFilters();
                NotifyNotesChanged();
                break;
        }
    }

    private void EnsureMassSelectionConsistency()
    {
        var existingIds = Notes.Select(note => note.Document.Id).ToHashSet();
        if (_massSelectedNoteIds.Count > 0)
            _massSelectedNoteIds.RemoveWhere(id => !existingIds.Contains(id));

        foreach (var note in Notes)
            note.IsSelectedInMassSelect = _massSelectedNoteIds.Contains(note.Document.Id);

        var existingFlashcardSetIds = FlashcardSets.Select(set => set.Document.Id).ToHashSet();
        if (_massSelectedFlashcardSetIds.Count > 0)
            _massSelectedFlashcardSetIds.RemoveWhere(id => !existingFlashcardSetIds.Contains(id));
        foreach (var set in FlashcardSets)
            set.IsSelectedInMassSelect = _massSelectedFlashcardSetIds.Contains(set.Document.Id);

        var existingMindMapIds = MindMaps.Select(map => map.Document.Id).ToHashSet();
        if (_massSelectedMindMapIds.Count > 0)
            _massSelectedMindMapIds.RemoveWhere(id => !existingMindMapIds.Contains(id));
        foreach (var map in MindMaps)
            map.IsSelectedInMassSelect = _massSelectedMindMapIds.Contains(map.Document.Id);

        var existingQuizIds = Quizzes.Select(quiz => quiz.Document.Id).ToHashSet();
        if (_massSelectedQuizIds.Count > 0)
            _massSelectedQuizIds.RemoveWhere(id => !existingQuizIds.Contains(id));
        foreach (var quiz in Quizzes)
            quiz.IsSelectedInMassSelect = _massSelectedQuizIds.Contains(quiz.Document.Id);

        NotifyMassSelectionChanged();
    }

    private void NotifyMassSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedNotesCount));
        OnPropertyChanged(nameof(ActiveMassSelectedCount));
        OnPropertyChanged(nameof(MassSelectSelectionText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetSortOptionSelected(string key, bool isSelected)
    {
        if (!isSelected)
            return;

        if (IsMindMapsView)
            SelectedMindMapSortOptionKey = key;
        else if (IsQuizzesView)
            SelectedQuizSortOptionKey = key;
        else if (IsFlashcardsView)
            SelectedFlashcardSortOptionKey = key;
        else
            SelectedSortOptionKey = key;
    }

    private void RefreshSortOptions()
    {
        var selectedKey = NormalizeSortOptionKey(_selectedSortOptionKey);
        _selectedSortOptionKey = selectedKey;

        SortOptions.Clear();
        SortOptions.Add(new NoteSortOptionItemViewModel(SortLastModifiedDesc, LocalizationService.GetString("SortByLastModifiedDesc"), selectedKey == SortLastModifiedDesc, SetSortOptionSelected));
        SortOptions.Add(new NoteSortOptionItemViewModel(SortLastModifiedAsc, LocalizationService.GetString("SortByLastModifiedAsc"), selectedKey == SortLastModifiedAsc, SetSortOptionSelected));
        SortOptions.Add(new NoteSortOptionItemViewModel(SortCreatedAtDesc, LocalizationService.GetString("SortByCreatedAtDesc"), selectedKey == SortCreatedAtDesc, SetSortOptionSelected));
        SortOptions.Add(new NoteSortOptionItemViewModel(SortCreatedAtAsc, LocalizationService.GetString("SortByCreatedAtAsc"), selectedKey == SortCreatedAtAsc, SetSortOptionSelected));
        SortOptions.Add(new NoteSortOptionItemViewModel(SortTitleAsc, LocalizationService.GetString("SortByTitleAsc"), selectedKey == SortTitleAsc, SetSortOptionSelected));
        SortOptions.Add(new NoteSortOptionItemViewModel(SortTitleDesc, LocalizationService.GetString("SortByTitleDesc"), selectedKey == SortTitleDesc, SetSortOptionSelected));
    }

    private void RefreshFlashcardSortOptions()
    {
        var selectedKey = NormalizeFlashcardSortOptionKey(_selectedFlashcardSortOptionKey);
        _selectedFlashcardSortOptionKey = selectedKey;

        FlashcardSortOptions.Clear();
        FlashcardSortOptions.Add(new NoteSortOptionItemViewModel(SortLastModifiedDesc, LocalizationService.GetString("SortByLastModifiedDesc"), selectedKey == SortLastModifiedDesc, SetSortOptionSelected));
        FlashcardSortOptions.Add(new NoteSortOptionItemViewModel(SortLastModifiedAsc, LocalizationService.GetString("SortByLastModifiedAsc"), selectedKey == SortLastModifiedAsc, SetSortOptionSelected));
        FlashcardSortOptions.Add(new NoteSortOptionItemViewModel(SortCreatedAtDesc, LocalizationService.GetString("SortByCreatedAtDesc"), selectedKey == SortCreatedAtDesc, SetSortOptionSelected));
        FlashcardSortOptions.Add(new NoteSortOptionItemViewModel(SortCreatedAtAsc, LocalizationService.GetString("SortByCreatedAtAsc"), selectedKey == SortCreatedAtAsc, SetSortOptionSelected));
        FlashcardSortOptions.Add(new NoteSortOptionItemViewModel(SortTitleAsc, LocalizationService.GetString("SortByTitleAsc"), selectedKey == SortTitleAsc, SetSortOptionSelected));
        FlashcardSortOptions.Add(new NoteSortOptionItemViewModel(SortTitleDesc, LocalizationService.GetString("SortByTitleDesc"), selectedKey == SortTitleDesc, SetSortOptionSelected));
        FlashcardSortOptions.Add(new NoteSortOptionItemViewModel(SortFlashcardCardsDesc, LocalizationService.GetString("SortByFlashcardCardsDesc"), selectedKey == SortFlashcardCardsDesc, SetSortOptionSelected));
        FlashcardSortOptions.Add(new NoteSortOptionItemViewModel(SortFlashcardCardsAsc, LocalizationService.GetString("SortByFlashcardCardsAsc"), selectedKey == SortFlashcardCardsAsc, SetSortOptionSelected));
    }

    private void RefreshMindMapSortOptions()
    {
        var selectedKey = NormalizeMindMapSortOptionKey(_selectedMindMapSortOptionKey);
        _selectedMindMapSortOptionKey = selectedKey;

        MindMapSortOptions.Clear();
        MindMapSortOptions.Add(new NoteSortOptionItemViewModel(SortLastModifiedDesc, LocalizationService.GetString("SortByLastModifiedDesc"), selectedKey == SortLastModifiedDesc, SetSortOptionSelected));
        MindMapSortOptions.Add(new NoteSortOptionItemViewModel(SortLastModifiedAsc, LocalizationService.GetString("SortByLastModifiedAsc"), selectedKey == SortLastModifiedAsc, SetSortOptionSelected));
        MindMapSortOptions.Add(new NoteSortOptionItemViewModel(SortCreatedAtDesc, LocalizationService.GetString("SortByCreatedAtDesc"), selectedKey == SortCreatedAtDesc, SetSortOptionSelected));
        MindMapSortOptions.Add(new NoteSortOptionItemViewModel(SortCreatedAtAsc, LocalizationService.GetString("SortByCreatedAtAsc"), selectedKey == SortCreatedAtAsc, SetSortOptionSelected));
        MindMapSortOptions.Add(new NoteSortOptionItemViewModel(SortTitleAsc, LocalizationService.GetString("SortByTitleAsc"), selectedKey == SortTitleAsc, SetSortOptionSelected));
        MindMapSortOptions.Add(new NoteSortOptionItemViewModel(SortTitleDesc, LocalizationService.GetString("SortByTitleDesc"), selectedKey == SortTitleDesc, SetSortOptionSelected));
        MindMapSortOptions.Add(new NoteSortOptionItemViewModel(SortMindMapNodesDesc, LocalizationService.GetString("SortByMindMapNodesDesc"), selectedKey == SortMindMapNodesDesc, SetSortOptionSelected));
        MindMapSortOptions.Add(new NoteSortOptionItemViewModel(SortMindMapNodesAsc, LocalizationService.GetString("SortByMindMapNodesAsc"), selectedKey == SortMindMapNodesAsc, SetSortOptionSelected));
    }

    private void RefreshQuizSortOptions()
    {
        var selectedKey = NormalizeQuizSortOptionKey(_selectedQuizSortOptionKey);
        _selectedQuizSortOptionKey = selectedKey;

        QuizSortOptions.Clear();
        QuizSortOptions.Add(new NoteSortOptionItemViewModel(SortLastModifiedDesc, LocalizationService.GetString("SortByLastModifiedDesc"), selectedKey == SortLastModifiedDesc, SetSortOptionSelected));
        QuizSortOptions.Add(new NoteSortOptionItemViewModel(SortLastModifiedAsc, LocalizationService.GetString("SortByLastModifiedAsc"), selectedKey == SortLastModifiedAsc, SetSortOptionSelected));
        QuizSortOptions.Add(new NoteSortOptionItemViewModel(SortCreatedAtDesc, LocalizationService.GetString("SortByCreatedAtDesc"), selectedKey == SortCreatedAtDesc, SetSortOptionSelected));
        QuizSortOptions.Add(new NoteSortOptionItemViewModel(SortCreatedAtAsc, LocalizationService.GetString("SortByCreatedAtAsc"), selectedKey == SortCreatedAtAsc, SetSortOptionSelected));
        QuizSortOptions.Add(new NoteSortOptionItemViewModel(SortTitleAsc, LocalizationService.GetString("SortByTitleAsc"), selectedKey == SortTitleAsc, SetSortOptionSelected));
        QuizSortOptions.Add(new NoteSortOptionItemViewModel(SortTitleDesc, LocalizationService.GetString("SortByTitleDesc"), selectedKey == SortTitleDesc, SetSortOptionSelected));
        QuizSortOptions.Add(new NoteSortOptionItemViewModel(SortQuizQuestionsDesc, LocalizationService.GetString("SortByQuizQuestionsDesc"), selectedKey == SortQuizQuestionsDesc, SetSortOptionSelected));
        QuizSortOptions.Add(new NoteSortOptionItemViewModel(SortQuizQuestionsAsc, LocalizationService.GetString("SortByQuizQuestionsAsc"), selectedKey == SortQuizQuestionsAsc, SetSortOptionSelected));
    }

    private void UpdateSortOptionSelection()
    {
        foreach (var option in SortOptions)
            option.IsSelected = string.Equals(option.Key, _selectedSortOptionKey, StringComparison.Ordinal);
    }

    private void UpdateFlashcardSortOptionSelection()
    {
        foreach (var option in FlashcardSortOptions)
            option.IsSelected = string.Equals(option.Key, _selectedFlashcardSortOptionKey, StringComparison.Ordinal);
    }

    private void UpdateMindMapSortOptionSelection()
    {
        foreach (var option in MindMapSortOptions)
            option.IsSelected = string.Equals(option.Key, _selectedMindMapSortOptionKey, StringComparison.Ordinal);
    }

    private void UpdateQuizSortOptionSelection()
    {
        foreach (var option in QuizSortOptions)
            option.IsSelected = string.Equals(option.Key, _selectedQuizSortOptionKey, StringComparison.Ordinal);
    }

    private void ApplySortToFlashcardSetsView()
    {
        _flashcardSetsView.SortDescriptions.Clear();
        _flashcardSetsView.SortDescriptions.Add(new SortDescription("Document.IsPinned", ListSortDirection.Descending));

        switch (_selectedFlashcardSortOptionKey)
        {
            case SortLastModifiedAsc:
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Ascending));
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            case SortCreatedAtDesc:
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Descending));
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                break;
            case SortCreatedAtAsc:
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Ascending));
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                break;
            case SortTitleAsc:
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            case SortTitleDesc:
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Descending));
                break;
            case SortFlashcardCardsAsc:
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("CardCount", ListSortDirection.Ascending));
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            case SortFlashcardCardsDesc:
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("CardCount", ListSortDirection.Descending));
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            default:
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                _flashcardSetsView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Descending));
                break;
        }
    }

    private void ApplySortToMindMapsView()
    {
        _mindMapsView.SortDescriptions.Clear();
        _mindMapsView.SortDescriptions.Add(new SortDescription("Document.IsPinned", ListSortDirection.Descending));

        switch (_selectedMindMapSortOptionKey)
        {
            case SortLastModifiedAsc:
                _mindMapsView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Ascending));
                _mindMapsView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            case SortCreatedAtDesc:
                _mindMapsView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Descending));
                _mindMapsView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                break;
            case SortCreatedAtAsc:
                _mindMapsView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Ascending));
                _mindMapsView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                break;
            case SortTitleAsc:
                _mindMapsView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            case SortTitleDesc:
                _mindMapsView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Descending));
                break;
            case SortMindMapNodesAsc:
                _mindMapsView.SortDescriptions.Add(new SortDescription("NodeCount", ListSortDirection.Ascending));
                _mindMapsView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            case SortMindMapNodesDesc:
                _mindMapsView.SortDescriptions.Add(new SortDescription("NodeCount", ListSortDirection.Descending));
                _mindMapsView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            default:
                _mindMapsView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                _mindMapsView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Descending));
                break;
        }
    }

    private void ApplySortToQuizzesView()
    {
        _quizzesView.SortDescriptions.Clear();
        _quizzesView.SortDescriptions.Add(new SortDescription("Document.IsPinned", ListSortDirection.Descending));

        switch (_selectedQuizSortOptionKey)
        {
            case SortLastModifiedAsc:
                _quizzesView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Ascending));
                _quizzesView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            case SortCreatedAtDesc:
                _quizzesView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Descending));
                _quizzesView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                break;
            case SortCreatedAtAsc:
                _quizzesView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Ascending));
                _quizzesView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                break;
            case SortTitleAsc:
                _quizzesView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            case SortTitleDesc:
                _quizzesView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Descending));
                break;
            case SortQuizQuestionsAsc:
                _quizzesView.SortDescriptions.Add(new SortDescription("QuestionCount", ListSortDirection.Ascending));
                _quizzesView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            case SortQuizQuestionsDesc:
                _quizzesView.SortDescriptions.Add(new SortDescription("QuestionCount", ListSortDirection.Descending));
                _quizzesView.SortDescriptions.Add(new SortDescription("Title", ListSortDirection.Ascending));
                break;
            default:
                _quizzesView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                _quizzesView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Descending));
                break;
        }
    }

    private void ApplySortToUngroupedView()
    {
        _notesView.SortDescriptions.Clear();

        // Always sort pinned notes first
        _notesView.SortDescriptions.Add(new SortDescription("Document.IsPinned", ListSortDirection.Descending));

        switch (_selectedSortOptionKey)
        {
            case SortLastModifiedAsc:
                _notesView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Ascending));
                _notesView.SortDescriptions.Add(new SortDescription("Document.Title", ListSortDirection.Ascending));
                break;
            case SortCreatedAtDesc:
                _notesView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Descending));
                _notesView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                break;
            case SortCreatedAtAsc:
                _notesView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Ascending));
                _notesView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                break;
            case SortTitleAsc:
                _notesView.SortDescriptions.Add(new SortDescription("Document.Title", ListSortDirection.Ascending));
                _notesView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                break;
            case SortTitleDesc:
                _notesView.SortDescriptions.Add(new SortDescription("Document.Title", ListSortDirection.Descending));
                _notesView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                break;
            default:
                _notesView.SortDescriptions.Add(new SortDescription("Document.LastModified", ListSortDirection.Descending));
                _notesView.SortDescriptions.Add(new SortDescription("Document.CreatedAt", ListSortDirection.Descending));
                break;
        }
    }

    private void ReorderNotesForPinSort()
    {
        var ordered = SortNotes(Notes);
        DashboardMotionEngine.BeginPinReorder();
        _notesPinReorderInProgress = true;
        try
        {
            SyncObservableCollectionOrder(Notes, ordered);
        }
        finally
        {
            _notesPinReorderInProgress = false;
            DashboardMotionEngine.EndPinReorder();
        }
    }

    private void ReorderFlashcardSetsForPinSort()
    {
        var ordered = SortFlashcardSets(FlashcardSets);
        DashboardMotionEngine.BeginPinReorder();
        _flashcardSetsReorderInProgress = true;
        try
        {
            SyncObservableCollectionOrder(FlashcardSets, ordered);
        }
        finally
        {
            _flashcardSetsReorderInProgress = false;
            DashboardMotionEngine.EndPinReorder();
        }
    }

    private void ReorderMindMapsForPinSort()
    {
        var ordered = SortMindMaps(MindMaps);
        DashboardMotionEngine.BeginPinReorder();
        _mindMapsReorderInProgress = true;
        try
        {
            SyncObservableCollectionOrder(MindMaps, ordered);
        }
        finally
        {
            _mindMapsReorderInProgress = false;
            DashboardMotionEngine.EndPinReorder();
        }
    }

    private void ReorderQuizzesForPinSort()
    {
        var ordered = SortQuizzes(Quizzes);
        DashboardMotionEngine.BeginPinReorder();
        _quizzesReorderInProgress = true;
        try
        {
            SyncObservableCollectionOrder(Quizzes, ordered);
        }
        finally
        {
            _quizzesReorderInProgress = false;
            DashboardMotionEngine.EndPinReorder();
        }
    }

    private List<NoteCardViewModel> SortNotes(IEnumerable<NoteCardViewModel> notes)
    {
        return _selectedSortOptionKey switch
        {
            SortLastModifiedAsc => notes
                .OrderByDescending(n => n.Document.IsPinned)
                .ThenBy(n => n.Document.LastModified)
                .ThenBy(n => n.Document.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortCreatedAtDesc => notes
                .OrderByDescending(n => n.Document.IsPinned)
                .ThenByDescending(n => n.Document.CreatedAt)
                .ThenByDescending(n => n.Document.LastModified)
                .ToList(),
            SortCreatedAtAsc => notes
                .OrderByDescending(n => n.Document.IsPinned)
                .ThenBy(n => n.Document.CreatedAt)
                .ThenByDescending(n => n.Document.LastModified)
                .ToList(),
            SortTitleAsc => notes
                .OrderByDescending(n => n.Document.IsPinned)
                .ThenBy(n => n.Document.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(n => n.Document.LastModified)
                .ToList(),
            SortTitleDesc => notes
                .OrderByDescending(n => n.Document.IsPinned)
                .ThenByDescending(n => n.Document.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(n => n.Document.LastModified)
                .ToList(),
            _ => notes
                .OrderByDescending(n => n.Document.IsPinned)
                .ThenByDescending(n => n.Document.LastModified)
                .ThenByDescending(n => n.Document.CreatedAt)
                .ToList()
        };
    }

    private List<FlashcardSetViewModel> SortFlashcardSets(IEnumerable<FlashcardSetViewModel> sets)
    {
        return _selectedFlashcardSortOptionKey switch
        {
            SortLastModifiedAsc => sets
                .OrderByDescending(set => set.Document.IsPinned)
                .ThenBy(set => set.Document.LastModified)
                .ThenBy(set => set.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortCreatedAtDesc => sets
                .OrderByDescending(set => set.Document.IsPinned)
                .ThenByDescending(set => set.Document.CreatedAt)
                .ThenByDescending(set => set.Document.LastModified)
                .ToList(),
            SortCreatedAtAsc => sets
                .OrderByDescending(set => set.Document.IsPinned)
                .ThenBy(set => set.Document.CreatedAt)
                .ThenByDescending(set => set.Document.LastModified)
                .ToList(),
            SortTitleAsc => sets
                .OrderByDescending(set => set.Document.IsPinned)
                .ThenBy(set => set.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortTitleDesc => sets
                .OrderByDescending(set => set.Document.IsPinned)
                .ThenByDescending(set => set.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortFlashcardCardsAsc => sets
                .OrderByDescending(set => set.Document.IsPinned)
                .ThenBy(set => set.CardCount)
                .ThenBy(set => set.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortFlashcardCardsDesc => sets
                .OrderByDescending(set => set.Document.IsPinned)
                .ThenByDescending(set => set.CardCount)
                .ThenBy(set => set.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => sets
                .OrderByDescending(set => set.Document.IsPinned)
                .ThenByDescending(set => set.Document.LastModified)
                .ThenByDescending(set => set.Document.CreatedAt)
                .ToList()
        };
    }

    private List<MindMapViewModel> SortMindMaps(IEnumerable<MindMapViewModel> mindMaps)
    {
        return _selectedMindMapSortOptionKey switch
        {
            SortLastModifiedAsc => mindMaps
                .OrderByDescending(map => map.Document.IsPinned)
                .ThenBy(map => map.Document.LastModified)
                .ThenBy(map => map.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortCreatedAtDesc => mindMaps
                .OrderByDescending(map => map.Document.IsPinned)
                .ThenByDescending(map => map.Document.CreatedAt)
                .ThenByDescending(map => map.Document.LastModified)
                .ToList(),
            SortCreatedAtAsc => mindMaps
                .OrderByDescending(map => map.Document.IsPinned)
                .ThenBy(map => map.Document.CreatedAt)
                .ThenByDescending(map => map.Document.LastModified)
                .ToList(),
            SortTitleAsc => mindMaps
                .OrderByDescending(map => map.Document.IsPinned)
                .ThenBy(map => map.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortTitleDesc => mindMaps
                .OrderByDescending(map => map.Document.IsPinned)
                .ThenByDescending(map => map.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortMindMapNodesAsc => mindMaps
                .OrderByDescending(map => map.Document.IsPinned)
                .ThenBy(map => map.NodeCount)
                .ThenBy(map => map.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortMindMapNodesDesc => mindMaps
                .OrderByDescending(map => map.Document.IsPinned)
                .ThenByDescending(map => map.NodeCount)
                .ThenBy(map => map.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => mindMaps
                .OrderByDescending(map => map.Document.IsPinned)
                .ThenByDescending(map => map.Document.LastModified)
                .ThenByDescending(map => map.Document.CreatedAt)
                .ToList()
        };
    }

    private List<QuizViewModel> SortQuizzes(IEnumerable<QuizViewModel> quizzes)
    {
        return _selectedQuizSortOptionKey switch
        {
            SortLastModifiedAsc => quizzes
                .OrderByDescending(quiz => quiz.Document.IsPinned)
                .ThenBy(quiz => quiz.Document.LastModified)
                .ThenBy(quiz => quiz.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortCreatedAtDesc => quizzes
                .OrderByDescending(quiz => quiz.Document.IsPinned)
                .ThenByDescending(quiz => quiz.Document.CreatedAt)
                .ThenByDescending(quiz => quiz.Document.LastModified)
                .ToList(),
            SortCreatedAtAsc => quizzes
                .OrderByDescending(quiz => quiz.Document.IsPinned)
                .ThenBy(quiz => quiz.Document.CreatedAt)
                .ThenByDescending(quiz => quiz.Document.LastModified)
                .ToList(),
            SortTitleAsc => quizzes
                .OrderByDescending(quiz => quiz.Document.IsPinned)
                .ThenBy(quiz => quiz.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortTitleDesc => quizzes
                .OrderByDescending(quiz => quiz.Document.IsPinned)
                .ThenByDescending(quiz => quiz.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortQuizQuestionsAsc => quizzes
                .OrderByDescending(quiz => quiz.Document.IsPinned)
                .ThenBy(quiz => quiz.QuestionCount)
                .ThenBy(quiz => quiz.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            SortQuizQuestionsDesc => quizzes
                .OrderByDescending(quiz => quiz.Document.IsPinned)
                .ThenByDescending(quiz => quiz.QuestionCount)
                .ThenBy(quiz => quiz.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => quizzes
                .OrderByDescending(quiz => quiz.Document.IsPinned)
                .ThenByDescending(quiz => quiz.Document.LastModified)
                .ThenByDescending(quiz => quiz.Document.CreatedAt)
                .ToList()
        };
    }

    private static string NormalizeSortOptionKey(string? value)
    {
        if (string.Equals(value, SortLastModifiedAsc, StringComparison.OrdinalIgnoreCase))
            return SortLastModifiedAsc;
        if (string.Equals(value, SortCreatedAtDesc, StringComparison.OrdinalIgnoreCase))
            return SortCreatedAtDesc;
        if (string.Equals(value, SortCreatedAtAsc, StringComparison.OrdinalIgnoreCase))
            return SortCreatedAtAsc;
        if (string.Equals(value, SortTitleAsc, StringComparison.OrdinalIgnoreCase))
            return SortTitleAsc;
        if (string.Equals(value, SortTitleDesc, StringComparison.OrdinalIgnoreCase))
            return SortTitleDesc;

        return SortLastModifiedDesc;
    }

    private static string NormalizeFlashcardSortOptionKey(string? value)
    {
        if (string.Equals(value, SortFlashcardCardsDesc, StringComparison.OrdinalIgnoreCase))
            return SortFlashcardCardsDesc;
        if (string.Equals(value, SortFlashcardCardsAsc, StringComparison.OrdinalIgnoreCase))
            return SortFlashcardCardsAsc;

        return NormalizeSortOptionKey(value);
    }

    private static string NormalizeMindMapSortOptionKey(string? value)
    {
        if (string.Equals(value, SortMindMapNodesDesc, StringComparison.OrdinalIgnoreCase))
            return SortMindMapNodesDesc;
        if (string.Equals(value, SortMindMapNodesAsc, StringComparison.OrdinalIgnoreCase))
            return SortMindMapNodesAsc;

        return NormalizeSortOptionKey(value);
    }

    private static string NormalizeQuizSortOptionKey(string? value)
    {
        if (string.Equals(value, SortQuizQuestionsDesc, StringComparison.OrdinalIgnoreCase))
            return SortQuizQuestionsDesc;
        if (string.Equals(value, SortQuizQuestionsAsc, StringComparison.OrdinalIgnoreCase))
            return SortQuizQuestionsAsc;

        return NormalizeSortOptionKey(value);
    }

    private static string NormalizeDashboard(string? dashboard)
    {
        if (string.Equals(dashboard, DashboardFlashcards, StringComparison.OrdinalIgnoreCase))
            return DashboardFlashcards;
        if (string.Equals(dashboard, DashboardMindMaps, StringComparison.OrdinalIgnoreCase))
            return DashboardMindMaps;
        if (string.Equals(dashboard, DashboardQuizzes, StringComparison.OrdinalIgnoreCase))
            return DashboardQuizzes;

        return DashboardNotes;
    }

    private static string GetSortOptionDisplayName(string sortKey)
    {
        return NormalizeSortOptionKey(sortKey) switch
        {
            SortLastModifiedAsc => LocalizationService.GetString("SortByLastModifiedAsc"),
            SortCreatedAtDesc => LocalizationService.GetString("SortByCreatedAtDesc"),
            SortCreatedAtAsc => LocalizationService.GetString("SortByCreatedAtAsc"),
            SortTitleAsc => LocalizationService.GetString("SortByTitleAsc"),
            SortTitleDesc => LocalizationService.GetString("SortByTitleDesc"),
            _ => LocalizationService.GetString("SortByLastModifiedDesc")
        };
    }

    private static string GetFlashcardSortOptionDisplayName(string sortKey)
    {
        return NormalizeFlashcardSortOptionKey(sortKey) switch
        {
            SortFlashcardCardsDesc => LocalizationService.GetString("SortByFlashcardCardsDesc"),
            SortFlashcardCardsAsc => LocalizationService.GetString("SortByFlashcardCardsAsc"),
            var normalized => GetSortOptionDisplayName(normalized)
        };
    }

    private static string GetMindMapSortOptionDisplayName(string sortKey)
    {
        return NormalizeMindMapSortOptionKey(sortKey) switch
        {
            SortMindMapNodesDesc => LocalizationService.GetString("SortByMindMapNodesDesc"),
            SortMindMapNodesAsc => LocalizationService.GetString("SortByMindMapNodesAsc"),
            var normalized => GetSortOptionDisplayName(normalized)
        };
    }

    private static string GetQuizSortOptionDisplayName(string sortKey)
    {
        return NormalizeQuizSortOptionKey(sortKey) switch
        {
            SortQuizQuestionsDesc => LocalizationService.GetString("SortByQuizQuestionsDesc"),
            SortQuizQuestionsAsc => LocalizationService.GetString("SortByQuizQuestionsAsc"),
            var normalized => GetSortOptionDisplayName(normalized)
        };
    }

    public void RefreshTagFiltersAfterNoteEdit()
    {
        RefreshAvailableTags();
        ApplyFilters();
    }

    public void SetNoteSchedules(NoteCardViewModel note, IEnumerable<NoteScheduleEntry>? schedules)
    {
        if (note is null)
            return;

        var normalized = NormalizeScheduleEntries(schedules);
        var current = GetEffectiveSchedules(note.Document);
        if (AreSchedulesEqual(current, normalized))
            return;

        var hadSchedule = note.HasSchedule;
        var willHaveSchedule = normalized.Count > 0;

        note.Document.Schedules = normalized;
        SyncLegacyScheduleFields(note.Document);
        note.Document.LastModified = DateTime.Now;

        if (hadSchedule == willHaveSchedule)
        {
            note.NotifyContentChanged();
            ApplyNoteScheduleUpdate();
            SaveNotes();
            return;
        }

        CommitDashboardScheduleToggle(
            DashboardMotionScope.Notes,
            note,
            willHaveSchedule,
            note.NotifyContentChanged,
            ApplyNoteScheduleUpdate,
            SaveNotes);
    }

    public void SetFlashcardSetSchedules(FlashcardSetViewModel flashcardSet, IEnumerable<NoteScheduleEntry>? schedules)
    {
        if (flashcardSet is null)
            return;

        var normalized = NormalizeScheduleEntries(schedules);
        var current = NormalizeScheduleEntries(flashcardSet.Document.Schedules);
        if (AreSchedulesEqual(current, normalized))
            return;

        var hadSchedule = flashcardSet.HasSchedule;
        var willHaveSchedule = normalized.Count > 0;

        flashcardSet.Document.Schedules = normalized;
        flashcardSet.Document.LastModified = DateTime.Now;

        if (hadSchedule == willHaveSchedule)
        {
            flashcardSet.NotifyChanged();
            ApplyFlashcardScheduleUpdate();
            SaveFlashcardSets();
            return;
        }

        CommitDashboardScheduleToggle(
            DashboardMotionScope.Flashcards,
            flashcardSet,
            willHaveSchedule,
            flashcardSet.NotifyChanged,
            ApplyFlashcardScheduleUpdate,
            SaveFlashcardSets);
    }

    public void SetMindMapSchedules(MindMapViewModel mindMap, IEnumerable<NoteScheduleEntry>? schedules)
    {
        if (mindMap is null)
            return;

        var normalized = NormalizeScheduleEntries(schedules);
        var current = NormalizeScheduleEntries(mindMap.Document.Schedules);
        if (AreSchedulesEqual(current, normalized))
            return;

        var hadSchedule = mindMap.HasSchedule;
        var willHaveSchedule = normalized.Count > 0;

        mindMap.Document.Schedules = normalized;
        mindMap.Document.LastModified = DateTime.Now;

        if (hadSchedule == willHaveSchedule)
        {
            mindMap.NotifyChanged();
            ApplyMindMapScheduleUpdate();
            SaveMindMaps();
            return;
        }

        CommitDashboardScheduleToggle(
            DashboardMotionScope.MindMaps,
            mindMap,
            willHaveSchedule,
            mindMap.NotifyChanged,
            ApplyMindMapScheduleUpdate,
            SaveMindMaps);
    }

    public void SetQuizSchedules(QuizViewModel quiz, IEnumerable<NoteScheduleEntry>? schedules)
    {
        if (quiz is null)
            return;

        var normalized = NormalizeScheduleEntries(schedules);
        var current = NormalizeScheduleEntries(quiz.Document.Schedules);
        if (AreSchedulesEqual(current, normalized))
            return;

        var hadSchedule = quiz.HasSchedule;
        var willHaveSchedule = normalized.Count > 0;

        quiz.Document.Schedules = normalized;
        quiz.Document.LastModified = DateTime.Now;

        if (hadSchedule == willHaveSchedule)
        {
            quiz.NotifyChanged();
            ApplyQuizScheduleUpdate();
            SaveQuizzes();
            return;
        }

        CommitDashboardScheduleToggle(
            DashboardMotionScope.Quizzes,
            quiz,
            willHaveSchedule,
            quiz.NotifyChanged,
            ApplyQuizScheduleUpdate,
            SaveQuizzes);
    }

    private void ApplyNoteScheduleUpdate()
    {
        InvalidateUngroupedVisibleCount();
        if (!string.IsNullOrWhiteSpace(SearchQuery))
            _notesView.Refresh();

        RefreshRecentNotes();
        RefreshCalendarScheduledNotes();
    }

    private void ApplyFlashcardScheduleUpdate()
    {
        RefreshCalendarScheduledNotes();
        RefreshRecentFlashcardSets();
    }

    private void ApplyMindMapScheduleUpdate()
    {
        RefreshCalendarScheduledNotes();
        RefreshRecentMindMaps();
    }

    private void ApplyQuizScheduleUpdate()
    {
        RefreshCalendarScheduledNotes();
        RefreshRecentQuizzes();
    }

    private void RefreshCalendarScheduledNotes()
    {
        var selected = CalendarSelectedDate.Date;

        var noteItems = Notes
            .Where(MatchesSearch)
            .SelectMany(note => GetEffectiveSchedules(note.Document).Select(entry => new CalendarScheduledItemViewModel
            {
                ItemType = ScheduledItemType.Note,
                Note = note,
                Title = note.Title,
                ScheduledAt = entry.ScheduledAt,
                ScheduleNote = entry.Note ?? string.Empty
            }));

        var flashcardItems = FlashcardSets
            .SelectMany(fs => (fs.Document.Schedules ?? Enumerable.Empty<NoteScheduleEntry>()).Select(entry => new CalendarScheduledItemViewModel
            {
                ItemType = ScheduledItemType.Flashcard,
                FlashcardSet = fs,
                Title = fs.Title,
                ScheduledAt = entry.ScheduledAt,
                ScheduleNote = entry.Note ?? string.Empty
            }));

        var mindMapItems = MindMaps
            .SelectMany(mm => (mm.Document.Schedules ?? Enumerable.Empty<NoteScheduleEntry>()).Select(entry => new CalendarScheduledItemViewModel
            {
                ItemType = ScheduledItemType.MindMap,
                MindMap = mm,
                Title = mm.Title,
                ScheduledAt = entry.ScheduledAt,
                ScheduleNote = entry.Note ?? string.Empty
            }));

        var quizItems = Quizzes
            .SelectMany(q => (q.Document.Schedules ?? Enumerable.Empty<NoteScheduleEntry>()).Select(entry => new CalendarScheduledItemViewModel
            {
                ItemType = ScheduledItemType.Quiz,
                Quiz = q,
                Title = q.Title,
                ScheduledAt = entry.ScheduledAt,
                ScheduleNote = entry.Note ?? string.Empty
            }));

        var allItems = noteItems
            .Concat(flashcardItems)
            .Concat(mindMapItems)
            .Concat(quizItems)
            .ToList();

        CalendarDatesWithSchedules = allItems
            .Select(item => item.ScheduledAt.Date)
            .Distinct()
            .OrderBy(date => date)
            .ToArray();
        OnPropertyChanged(nameof(CalendarDatesWithSchedules));

        var items = allItems
            .Where(item => item.ScheduledAt.Date == selected)
            .OrderBy(item => item.ScheduledAt)
            .ThenBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        CalendarScheduledNotes.Clear();
        foreach (var item in items)
            CalendarScheduledNotes.Add(item);

        OnPropertyChanged(nameof(HasCalendarScheduledNotes));
        OnPropertyChanged(nameof(CalendarSelectedDateDisplay));
    }

    private static bool AreSchedulesEqual(IReadOnlyList<NoteScheduleEntry> left, IReadOnlyList<NoteScheduleEntry> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].ScheduledAt != right[i].ScheduledAt)
                return false;

            if (!string.Equals(left[i].Note ?? string.Empty, right[i].Note ?? string.Empty, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static List<NoteScheduleEntry> NormalizeScheduleEntries(IEnumerable<NoteScheduleEntry>? schedules)
    {
        if (schedules is null)
            return new List<NoteScheduleEntry>();

        return schedules
            .Where(entry => entry != null)
            .Select(entry => new NoteScheduleEntry
            {
                ScheduledAt = entry.ScheduledAt,
                Note = (entry.Note ?? string.Empty).Trim()
            })
            .OrderBy(entry => entry.ScheduledAt)
            .ThenBy(entry => entry.Note, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<NoteScheduleEntry> GetEffectiveSchedules(NoteDocument document)
    {
        EnsureDocumentSchedules(document);
        return document.Schedules;
    }

    private static void EnsureDocumentSchedules(NoteDocument document)
    {
        document.Schedules ??= new List<NoteScheduleEntry>();

        if (document.Schedules.Count == 0 && document.ScheduledAt.HasValue)
        {
            document.Schedules.Add(new NoteScheduleEntry
            {
                ScheduledAt = document.ScheduledAt.Value,
                Note = document.ScheduleNote ?? string.Empty
            });
        }

        if (document.Schedules.Count > 0)
        {
            var normalized = NormalizeScheduleEntries(document.Schedules);
            if (!AreSchedulesEqual(document.Schedules, normalized))
                document.Schedules = normalized;
        }

        SyncLegacyScheduleFields(document);
    }

    private static void SyncLegacyScheduleFields(NoteDocument document)
    {
        if (document.Schedules is null || document.Schedules.Count == 0)
        {
            document.ScheduledAt = null;
            document.ScheduleNote = string.Empty;
            return;
        }

        var primary = document.Schedules[0];
        document.ScheduledAt = primary.ScheduledAt;
        document.ScheduleNote = primary.Note ?? string.Empty;
    }

    private void NormalizeGroups()
    {
        var grouped = Notes
            .Where(n => n.Document.GroupId.HasValue)
            .GroupBy(n => n.Document.GroupId!.Value)
            .ToList();

        foreach (var group in grouped)
        {
            if (group.Count() >= 2)
                continue;

            foreach (var note in group)
            {
                note.Document.GroupId = null;
                note.NotifyGroupChanged();
            }

            _groupMetadata.Remove(group.Key);
        }
    }

    private static void SyncObservableCollectionOrder<T>(ObservableCollection<T> collection, IReadOnlyList<T> desired)
        where T : class
    {
        // Hash-backed lookups instead of List/ObservableCollection .Contains()/.IndexOf() -
        // those are O(n) linear scans, which turned every pass below into O(n^2) for large groups.
        var desiredSet = new HashSet<T>(desired);
        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(collection[i]))
                collection.RemoveAt(i);
        }

        var existingSet = new HashSet<T>(collection);
        foreach (var item in desired)
        {
            if (existingSet.Add(item))
                collection.Add(item);
        }

        var indexOf = new Dictionary<T, int>(collection.Count);
        for (var i = 0; i < collection.Count; i++)
            indexOf[collection[i]] = i;

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            var currentIndex = indexOf[item];
            if (currentIndex == i)
                continue;

            collection.Move(currentIndex, i);

            // Move shifts every index strictly between the old and new position by one;
            // keep the lookup in sync so later iterations still resolve the right index.
            if (currentIndex > i)
            {
                for (var j = currentIndex; j > i; j--)
                    indexOf[collection[j]] = j;
            }
            else
            {
                for (var j = currentIndex; j < i; j++)
                    indexOf[collection[j]] = j;
            }

            indexOf[item] = i;
        }
    }

    private void RebuildGroups()
    {
        _notesView.Refresh();

        var groupedByRecent = Notes
            .Where(n => n.Document.GroupId.HasValue)
            .GroupBy(n => n.Document.GroupId!.Value)
            .OrderByDescending(g => g.Max(n => n.Document.LastModified))
            .ToList();

        var nextOrder = 0;
        foreach (var group in groupedByRecent)
        {
            var metadata = EnsureGroupMetadata(group.Key);
            if (!metadata.SortOrder.HasValue)
                metadata.SortOrder = nextOrder;

            nextOrder = Math.Max(nextOrder, metadata.SortOrder.Value + 1);
        }

        var grouped = groupedByRecent
            .OrderBy(g => EnsureGroupMetadata(g.Key).SortOrder ?? int.MaxValue)
            .ThenByDescending(g => g.Max(n => n.Document.LastModified))
            .ToList();

        var desiredGroups = new List<NoteGroupViewModel>();
        foreach (var group in grouped)
        {
            var visibleNotes = SortNotes(group.Where(MatchesSearch));
            if (visibleNotes.Count == 0)
                continue;

            var metadata = EnsureGroupMetadata(group.Key);
            desiredGroups.Add(new NoteGroupViewModel(group.Key, metadata.Name, metadata.BackgroundColor, visibleNotes));
        }

        var desiredIds = desiredGroups.Select(group => group.GroupId).ToHashSet();
        var removingIds = NoteGroups
            .Where(group => !desiredIds.Contains(group.GroupId))
            .Select(group => group.GroupId)
            .ToList();

        if (TryAnimateGroupRemovals(DashboardMotionScope.Notes, removingIds, () => SyncNoteGroups(desiredGroups)))
            return;

        SyncNoteGroups(desiredGroups);
    }

    private void SyncNoteGroups(IReadOnlyList<NoteGroupViewModel> desiredGroups)
    {
        var desiredIds = desiredGroups.Select(group => group.GroupId).ToHashSet();
        for (var i = NoteGroups.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(NoteGroups[i].GroupId))
                NoteGroups.RemoveAt(i);
        }

        for (var i = 0; i < desiredGroups.Count; i++)
        {
            var desired = desiredGroups[i];
            var existing = NoteGroups.FirstOrDefault(group => group.GroupId == desired.GroupId);
            if (existing is null)
            {
                NoteGroups.Insert(Math.Min(i, NoteGroups.Count), desired);
                continue;
            }

            if (NoteGroups.IndexOf(existing) != i)
                NoteGroups.Move(NoteGroups.IndexOf(existing), i);

            SyncObservableCollectionOrder(existing.Notes, desired.Notes.ToList());
        }

        OnPropertyChanged(nameof(NoteGroups));
        OnPropertyChanged(nameof(NotesView));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasUngroupedNotes));
        OnPropertyChanged(nameof(HasNotes));
        NotifyNotesDashboardPresentationChanged();
    }

    private void NotifyNotesChanged()
    {
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasUngroupedNotes));
        OnPropertyChanged(nameof(NoteGroups));
        OnPropertyChanged(nameof(NotesView));
        NotifyNotesDashboardPresentationChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    public void RebuildMindMapGroups()
    {
        var grouped = MindMaps
            .Where(m => m.Document.GroupId.HasValue)
            .GroupBy(m => m.Document.GroupId!.Value)
            .OrderBy(g =>
            {
                if (_mindMapGroupMetadata.TryGetValue(g.Key, out var meta))
                    return meta.SortOrder ?? int.MaxValue;
                return int.MaxValue;
            })
            .ToList();

        var desiredGroups = new List<MindMapGroupViewModel>();
        foreach (var group in grouped)
        {
            if (!_mindMapGroupMetadata.TryGetValue(group.Key, out var meta))
            {
                meta = new MindMapGroupData { GroupId = group.Key, Name = string.Format(LocalizationService.GetString("GroupTitleFormat"), group.Key.ToString()[..4].ToUpperInvariant()), BackgroundColor = DefaultGroupBackground };
                _mindMapGroupMetadata[group.Key] = meta;
            }

            var visibleMaps = group
                .Where(MatchesMindMapFilters)
                .OrderByDescending(map => map.Document.IsPinned)
                .ThenByDescending(map => map.Document.LastModified)
                .ThenBy(map => map.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (visibleMaps.Count == 0)
                continue;

            desiredGroups.Add(new MindMapGroupViewModel(group.Key, meta.Name, meta.BackgroundColor, visibleMaps));
        }

        var desiredIds = desiredGroups.Select(group => group.GroupId).ToHashSet();
        var removingIds = MindMapGroups
            .Where(group => !desiredIds.Contains(group.GroupId))
            .Select(group => group.GroupId)
            .ToList();

        if (TryAnimateGroupRemovals(DashboardMotionScope.MindMaps, removingIds, () => SyncMindMapGroups(desiredGroups)))
            return;

        SyncMindMapGroups(desiredGroups);
    }

    private void SyncMindMapGroups(IReadOnlyList<MindMapGroupViewModel> desiredGroups)
    {
        var desiredIds = desiredGroups.Select(group => group.GroupId).ToHashSet();
        for (var i = MindMapGroups.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(MindMapGroups[i].GroupId))
                MindMapGroups.RemoveAt(i);
        }

        for (var i = 0; i < desiredGroups.Count; i++)
        {
            var desired = desiredGroups[i];
            var existing = MindMapGroups.FirstOrDefault(group => group.GroupId == desired.GroupId);
            if (existing is null)
            {
                MindMapGroups.Insert(Math.Min(i, MindMapGroups.Count), desired);
                continue;
            }

            if (MindMapGroups.IndexOf(existing) != i)
                MindMapGroups.Move(MindMapGroups.IndexOf(existing), i);

            SyncObservableCollectionOrder(existing.MindMaps, desired.MindMaps.ToList());
        }

        OnPropertyChanged(nameof(MindMapGroups));
        OnPropertyChanged(nameof(HasMindMapGroups));
        NotifyHasUngroupedMindMapsChanged();
    }

    private bool TryAnimateGroupRemovals(DashboardMotionScope scope, IReadOnlyList<Guid> removingIds, Action commitSync)
    {
        if (removingIds.Count == 0 || MotionHost is null)
            return false;

        MotionHost.RequestGroupRemovals(scope, removingIds, commitSync);
        return true;
    }

    private static List<Guid> BuildGroupMergeMoveIds(
        Guid draggedId,
        Guid targetId,
        Guid? draggedGroupId,
        Guid? targetGroupId,
        Guid finalGroupId)
    {
        if (!targetGroupId.HasValue && !draggedGroupId.HasValue)
            return [draggedId, targetId];

        var movingIds = new List<Guid>(2);
        if (draggedGroupId != finalGroupId)
            movingIds.Add(draggedId);
        if (targetGroupId != finalGroupId)
            movingIds.Add(targetId);
        return movingIds;
    }

    private bool TryRequestItemMove(DashboardMotionScope scope, Guid itemId, Action commitMove)
    {
        if (MotionHost is null)
            return false;

        MotionHost.RequestItemMove(scope, itemId, commitMove);
        return true;
    }

    private bool TryRequestGroupMergeMove(
        DashboardMotionScope scope,
        IReadOnlyList<Guid> movingIds,
        Action commitGroup)
    {
        if (MotionHost is null)
            return false;

        // GroupSlide Loaded enter is the single pop; do not ScheduleGroupCardEnter again.
        MotionHost.RequestItemMoveBatch(scope, movingIds, commitGroup);
        return true;
    }
    // Persistence: save/load notes to a local JSON file
    private static string GetNotesFilePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "notes.json");
    }

    public void SaveNotes()
    {
        try
        {
            var docs = new List<NoteDocument>();
            foreach (var vm in Notes)
                docs.Add(vm.Document);

            var store = new NotesStoreData
            {
                Notes = docs,
                Groups = _groupMetadata.Values
                    .OrderBy(g => g.SortOrder ?? int.MaxValue)
                    .ThenBy(g => g.Name)
                    .ToList()
            };

            var opts = new JsonSerializerOptions { WriteIndented = false };
            var json = JsonSerializer.Serialize(store, opts);
            AtomicFileWriter.WriteAllText(GetNotesFilePath(), json);
        }
        catch
        {
            // Persistence errors are non-fatal; ignored.
        }
    }

    private void ApplyAppSettings(AppSettings settings, bool applyThemeAndCulture)
    {
        _isLoadingSettings = true;
        _settings = settings;

        _enableScrollbar = settings.EnableScrollbar;
        _enableVerticalScrollbar = settings.EnableVerticalScrollbar;
        _enableHorizontalScrollbar = settings.EnableHorizontalScrollbar;
        _selectedLanguage = LocalizationService.NormalizeLanguage(settings.Language);
        _selectedTheme = ThemeManager.NormalizeThemePreference(settings.Theme);
        _activeDashboard = NormalizeDashboard(settings.LastView);
        _selectedSortOptionKey = NormalizeSortOptionKey(settings.NoteSortOptionKey);
        _selectedFlashcardSortOptionKey = NormalizeFlashcardSortOptionKey(settings.FlashcardSortOptionKey);
        _selectedMindMapSortOptionKey = NormalizeMindMapSortOptionKey(settings.MindMapSortOptionKey);
        _selectedQuizSortOptionKey = NormalizeQuizSortOptionKey(settings.QuizSortOptionKey);
        _isRecentSectionExpanded = settings.IsRecentSectionExpanded;
        _isGroupsSectionExpanded = settings.IsGroupsSectionExpanded;
        _isUngroupedSectionExpanded = settings.IsUngroupedSectionExpanded;
        _isNotesDashboardSectionVisible = settings.IsNotesDashboardSectionVisible;
        _isNotesDashboardSectionExpanded = settings.IsNotesDashboardSectionExpanded;
        _isCalendarSectionExpanded = settings.IsCalendarSectionExpanded;
        _isFlashcardGroupsSectionExpanded = settings.IsFlashcardGroupsSectionExpanded;
        _isFlashcardGroupsSectionVisible = settings.IsFlashcardGroupsSectionVisible;
        _isFlashcardDashboardGroupsSectionVisible = settings.IsFlashcardDashboardGroupsSectionVisible;
        _isFlashcardDashboardUngroupedSectionVisible = settings.IsFlashcardDashboardUngroupedSectionVisible;
        _isFlashcardDashboardGroupsSectionExpanded = settings.IsFlashcardDashboardGroupsSectionExpanded;
        _isFlashcardDashboardUngroupedSectionExpanded = settings.IsFlashcardDashboardUngroupedSectionExpanded;
        _isFlashcardDashboardGroupsFirst = settings.IsFlashcardDashboardGroupsFirst;
        _isMindMapGroupsSectionExpanded = settings.IsMindMapGroupsSectionExpanded;
        _isMindMapDashboardGroupsSectionVisible = settings.IsMindMapDashboardGroupsSectionVisible;
        _isMindMapDashboardUngroupedSectionVisible = settings.IsMindMapDashboardUngroupedSectionVisible;
        _isMindMapDashboardGroupsSectionExpanded = settings.IsMindMapDashboardGroupsSectionExpanded;
        _isMindMapDashboardUngroupedSectionExpanded = settings.IsMindMapDashboardUngroupedSectionExpanded;
        _isMindMapDashboardGroupsFirst = settings.IsMindMapDashboardGroupsFirst;
        _isQuizGroupsSectionVisible = settings.IsQuizGroupsSectionVisible;
        _isQuizGroupsSectionExpanded = settings.IsQuizGroupsSectionExpanded;
        _isQuizDashboardGroupsSectionVisible = settings.IsQuizDashboardGroupsSectionVisible;
        _isQuizDashboardUngroupedSectionVisible = settings.IsQuizDashboardUngroupedSectionVisible;
        _isQuizDashboardGroupsSectionExpanded = settings.IsQuizDashboardGroupsSectionExpanded;
        _isQuizDashboardUngroupedSectionExpanded = settings.IsQuizDashboardUngroupedSectionExpanded;
        _isQuizDashboardGroupsFirst = settings.IsQuizDashboardGroupsFirst;
        _isRecentSectionVisible = settings.IsRecentSectionVisible;
        _isGroupsSectionVisible = settings.IsGroupsSectionVisible;
        _isUngroupedSectionVisible = settings.IsUngroupedSectionVisible;
        _isCalendarSectionVisible = settings.IsCalendarSectionVisible;
        _isMindMapGroupsSectionVisible = settings.IsMindMapGroupsSectionVisible;
        _isGroupsFirst = settings.IsGroupsFirst;
        _isCalendarFirst = settings.IsCalendarFirst;
        _recentItemsLimit = AppSettings.NormalizeRecentItemsLimit(settings.RecentItemsLimit);
        if (!_isNotesDashboardSectionVisible)
        {
            _isGroupsSectionVisible = false;
            _isUngroupedSectionVisible = false;
        }

        if (!_isFlashcardGroupsSectionVisible)
        {
            _isFlashcardDashboardGroupsSectionVisible = false;
            _isFlashcardDashboardUngroupedSectionVisible = false;
        }

        if (!_isMindMapGroupsSectionVisible)
        {
            _isMindMapDashboardGroupsSectionVisible = false;
            _isMindMapDashboardUngroupedSectionVisible = false;
        }

        if (!_isQuizGroupsSectionVisible)
        {
            _isQuizDashboardGroupsSectionVisible = false;
            _isQuizDashboardUngroupedSectionVisible = false;
        }

        if (applyThemeAndCulture)
        {
            LocalizationService.SetCulture(_selectedLanguage);
            ThemeManager.SetTheme(_selectedTheme);
        }

        _isLoadingSettings = false;
    }

    private void SaveAppSettings()
    {
        if (_isLoadingSettings)
            return;

        var settings = AppSettingsService.Load();
        settings.Language = _selectedLanguage;
        settings.Theme = _selectedTheme;
        settings.NoteSortOptionKey = _selectedSortOptionKey;
        settings.FlashcardSortOptionKey = _selectedFlashcardSortOptionKey;
        settings.MindMapSortOptionKey = _selectedMindMapSortOptionKey;
        settings.QuizSortOptionKey = _selectedQuizSortOptionKey;
        settings.EnableScrollbar = _enableScrollbar;
        settings.EnableVerticalScrollbar = _enableVerticalScrollbar;
        settings.EnableHorizontalScrollbar = _enableHorizontalScrollbar;
        settings.IsRecentSectionExpanded = _isRecentSectionExpanded;
        settings.IsGroupsSectionExpanded = _isGroupsSectionExpanded;
        settings.IsUngroupedSectionExpanded = _isUngroupedSectionExpanded;
        settings.IsNotesDashboardSectionVisible = _isNotesDashboardSectionVisible;
        settings.IsNotesDashboardSectionExpanded = _isNotesDashboardSectionExpanded;
        settings.IsCalendarSectionExpanded = _isCalendarSectionExpanded;
        settings.IsFlashcardGroupsSectionExpanded = _isFlashcardGroupsSectionExpanded;
        settings.IsFlashcardGroupsSectionVisible = _isFlashcardGroupsSectionVisible;
        settings.IsFlashcardDashboardGroupsSectionVisible = _isFlashcardDashboardGroupsSectionVisible;
        settings.IsFlashcardDashboardUngroupedSectionVisible = _isFlashcardDashboardUngroupedSectionVisible;
        settings.IsFlashcardDashboardGroupsSectionExpanded = _isFlashcardDashboardGroupsSectionExpanded;
        settings.IsFlashcardDashboardUngroupedSectionExpanded = _isFlashcardDashboardUngroupedSectionExpanded;
        settings.IsFlashcardDashboardGroupsFirst = _isFlashcardDashboardGroupsFirst;
        settings.IsMindMapGroupsSectionExpanded = _isMindMapGroupsSectionExpanded;
        settings.IsMindMapDashboardGroupsSectionVisible = _isMindMapDashboardGroupsSectionVisible;
        settings.IsMindMapDashboardUngroupedSectionVisible = _isMindMapDashboardUngroupedSectionVisible;
        settings.IsMindMapDashboardGroupsSectionExpanded = _isMindMapDashboardGroupsSectionExpanded;
        settings.IsMindMapDashboardUngroupedSectionExpanded = _isMindMapDashboardUngroupedSectionExpanded;
        settings.IsMindMapDashboardGroupsFirst = _isMindMapDashboardGroupsFirst;
        settings.IsQuizGroupsSectionVisible = _isQuizGroupsSectionVisible;
        settings.IsQuizGroupsSectionExpanded = _isQuizGroupsSectionExpanded;
        settings.IsQuizDashboardGroupsSectionVisible = _isQuizDashboardGroupsSectionVisible;
        settings.IsQuizDashboardUngroupedSectionVisible = _isQuizDashboardUngroupedSectionVisible;
        settings.IsQuizDashboardGroupsSectionExpanded = _isQuizDashboardGroupsSectionExpanded;
        settings.IsQuizDashboardUngroupedSectionExpanded = _isQuizDashboardUngroupedSectionExpanded;
        settings.IsQuizDashboardGroupsFirst = _isQuizDashboardGroupsFirst;
        settings.IsRecentSectionVisible = _isRecentSectionVisible;
        settings.IsGroupsSectionVisible = _isGroupsSectionVisible;
        settings.IsUngroupedSectionVisible = _isUngroupedSectionVisible;
        settings.IsCalendarSectionVisible = _isCalendarSectionVisible;
        settings.IsMindMapGroupsSectionVisible = _isMindMapGroupsSectionVisible;
        settings.IsGroupsFirst = _isGroupsFirst;
        settings.IsCalendarFirst = _isCalendarFirst;
        settings.DefaultViewMode = "Grid";
        settings.RecentItemsLimit = _recentItemsLimit;
        settings.LastView = _activeDashboard;

        AppSettingsService.Save(settings);
        GlobalScrollBarSettingsService.Apply(settings);
    }

    public void RefreshAutoSaveSettings()
    {
        ApplyAutoSaveSettings(AppSettingsService.Load());
    }

    private void ApplyAutoSaveSettings(AppSettings settings)
    {
        if (!settings.EnableAutoSave)
        {
            StopAutoSaveTimer();
            return;
        }

        var intervalSeconds = Math.Clamp(settings.AutoSaveIntervalSeconds, 5, 86400);
        _autoSaveTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(intervalSeconds)
        };
        _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        _autoSaveTimer.Interval = TimeSpan.FromSeconds(intervalSeconds);
        _autoSaveTimer.Start();
    }

    private void StopAutoSaveTimer()
    {
        if (_autoSaveTimer is null)
            return;

        _autoSaveTimer.Stop();
        _autoSaveTimer.Tick -= AutoSaveTimer_Tick;
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        SaveAllDashboardContent();
    }

    public void SaveAllDashboardContent()
    {
        if (_notesLoadComplete)
            SaveNotes();
        if (_flashcardsLoadCompleted)
            SaveFlashcardSets();
        if (_mindMapsLoadCompleted)
            SaveMindMaps();
        if (_quizzesLoadCompleted)
            SaveQuizzes();
    }

    private enum NotesLoadStatus
    {
        Success,
        NotFound,
        ParseError
    }

    private readonly record struct NotesLoadResult(
        NotesLoadStatus Status,
        List<NoteDocument> Docs,
        List<NoteGroupData> Groups,
        string? BackupPath = null);

    private static NotesLoadResult ReadNotesFromDisk(string path)
    {
        if (!File.Exists(path))
            return new NotesLoadResult(NotesLoadStatus.NotFound, new List<NoteDocument>(), new List<NoteGroupData>());

        try
        {
            var json = File.ReadAllText(path);
            var docs = new List<NoteDocument>();
            var groups = new List<NoteGroupData>();
            var store = JsonSerializer.Deserialize<NotesStoreData>(json);
            if (store?.Notes != null && store.Notes.Count > 0)
            {
                docs = store.Notes;
                groups = store.Groups ?? new List<NoteGroupData>();
            }
            else
            {
                var legacyDocs = JsonSerializer.Deserialize<List<NoteDocument>>(json);
                if (legacyDocs != null)
                    docs = legacyDocs;
            }

            return new NotesLoadResult(NotesLoadStatus.Success, docs, groups);
        }
        catch
        {
            // File exists but couldn't be parsed - likely truncated/corrupted by a crash mid-write.
            // Back it up rather than letting the caller silently overwrite it with a starter note.
            var backupPath = TryBackupCorruptFile(path);
            return new NotesLoadResult(NotesLoadStatus.ParseError, new List<NoteDocument>(), new List<NoteGroupData>(), backupPath);
        }
    }

    private static string? TryBackupCorruptFile(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            var backupPath = Path.Combine(directory, $"{baseName}.corrupt-{stamp}{extension}");
            var suffix = 2;
            while (File.Exists(backupPath))
            {
                backupPath = Path.Combine(directory, $"{baseName}.corrupt-{stamp}-{suffix}{extension}");
                suffix++;
            }

            File.Copy(path, backupPath, overwrite: false);
            return backupPath;
        }
        catch
        {
            return null;
        }
    }

    private void LoadNotesInBatches(IReadOnlyList<NoteDocument> docs)
    {
        _notesBatchAddInProgress = true;

        if (docs.Count <= NotesSingleCommitMax)
        {
            Application.Current?.Dispatcher.BeginInvoke(
                () => CommitLoadedNotes(docs),
                DispatcherPriority.Background);
            return;
        }

        void AddBatch(int startIndex)
        {
            var endIndex = Math.Min(startIndex + NotesLoadBatchSize, docs.Count);
            for (var i = startIndex; i < endIndex; i++)
            {
                EnsureDocumentSchedules(docs[i]);
                Notes.Add(CreateNoteCard(docs[i]));
            }

            if (endIndex < docs.Count)
            {
                Application.Current?.Dispatcher.BeginInvoke(
                    () => AddBatch(endIndex),
                    DispatcherPriority.Background);
                return;
            }

            // Must be an explicit lambda, not a bare method-group reference: CompleteNotesStartupPresentation
            // has an optional parameter, and Dispatcher.BeginInvoke(Delegate, DispatcherPriority) falls back to
            // reflection-based DynamicInvoke for delegates it doesn't recognize as a plain zero-arg Action -
            // that throws TargetParameterCountException at the optional parameter, only reachable once a
            // library is large enough to take this batched path (> NotesSingleCommitMax notes).
            Application.Current?.Dispatcher.BeginInvoke(
                () => CompleteNotesStartupPresentation(),
                DispatcherPriority.Background);
        }

        Notes.Clear();
        AddBatch(0);
    }

    private void CommitLoadedNotes(IReadOnlyList<NoteDocument> docs)
    {
        try
        {
            Notes.Clear();
            foreach (var doc in docs)
            {
                EnsureDocumentSchedules(doc);
                Notes.Add(CreateNoteCard(doc));
            }

            CompleteNotesStartupPresentation();
        }
        finally
        {
            _notesBatchAddInProgress = false;
        }
    }

    private void FinishNotesLoadInitialization(bool markLoadComplete = true)
    {
        try
        {
            CompleteNotesStartupPresentation(markLoadComplete);
        }
        finally
        {
            _notesBatchAddInProgress = false;
        }
    }

    private void CompleteNotesStartupPresentation(bool markLoadComplete = true)
    {
        RefreshAvailableTags();
        NormalizeGroups();
        RebuildGroups();
        RecalculateUngroupedVisibleCount();
        _notesBatchAddInProgress = false;
        if (markLoadComplete)
            _notesLoadComplete = true;
        NotifyNotesChanged();
        RefreshRecentNotes();
        RefreshCalendarScheduledNotes();
        MarkNotesDashboardReady();
        WarmNoteContentPreviewsAtIdle();
    }

    /// <summary>
    /// Pre-decodes card content previews (Base64 + RTF extraction) in small idle-time slices
    /// so scrolling into fresh cards never runs the decoder during card realization.
    /// </summary>
    private void WarmNoteContentPreviewsAtIdle()
    {
        // Warm in the order notes are actually scrolled to (NotesView's pinned/sorted order),
        // not raw load order (Notes) — the two diverge until a pin toggle physically reorders
        // Notes to match, so warming raw Notes can spend the idle window on cards far from what
        // the user sees first.
        var pending = new Queue<NoteCardViewModel>(NotesView.Cast<NoteCardViewModel>());
        if (pending.Count == 0)
            return;

        void Pump()
        {
            var budget = System.Diagnostics.Stopwatch.StartNew();
            while (pending.Count > 0 && budget.ElapsedMilliseconds < 4)
                pending.Dequeue().WarmContentPreview();

            if (pending.Count > 0)
                Application.Current?.Dispatcher.BeginInvoke(Pump, DispatcherPriority.ApplicationIdle);
        }

        Application.Current?.Dispatcher.BeginInvoke(Pump, DispatcherPriority.ApplicationIdle);
    }

    private void MarkNotesDashboardReady()
    {
        if (_isNotesDashboardReady)
            return;

        _isNotesDashboardReady = true;
        NotifyNotesDashboardPresentationChanged();
        EnableDashboardRevealMotionIfActive(DashboardNotes);
        NotesDashboardReady?.Invoke(this, EventArgs.Empty);
    }

    private void MarkFlashcardsDashboardReady()
    {
        if (_isFlashcardsDashboardReady)
            return;

        _isFlashcardsDashboardReady = true;
        NotifyFlashcardsDashboardPresentationChanged();
        EnableDashboardRevealMotionIfActive(DashboardFlashcards);
    }

    private void MarkMindMapsDashboardReady()
    {
        if (_isMindMapsDashboardReady)
            return;

        _isMindMapsDashboardReady = true;
        NotifyMindMapsDashboardPresentationChanged();
        EnableDashboardRevealMotionIfActive(DashboardMindMaps);
    }

    private void MarkQuizzesDashboardReady()
    {
        if (_isQuizzesDashboardReady)
            return;

        _isQuizzesDashboardReady = true;
        NotifyQuizzesDashboardPresentationChanged();
        EnableDashboardRevealMotionIfActive(DashboardQuizzes);
    }

    private void EnableDashboardRevealMotionIfActive(string dashboard)
    {
        if (!string.Equals(_activeDashboard, NormalizeDashboard(dashboard), StringComparison.Ordinal))
            return;

        if (IsDashboardRevealMotionEnabled)
            return;

        IsDashboardRevealMotionEnabled = true;
        OnPropertyChanged(nameof(IsDashboardRevealMotionEnabled));
    }

    private void NotifyNotesDashboardPresentationChanged()
    {
        OnPropertyChanged(nameof(IsNotesDashboardLoading));
        OnPropertyChanged(nameof(ShowNotesEmptyDashboard));
        OnPropertyChanged(nameof(ShouldShowNotesDashboardContent));
    }

    private void NotifyFlashcardsDashboardPresentationChanged()
    {
        OnPropertyChanged(nameof(IsFlashcardsDashboardLoading));
        OnPropertyChanged(nameof(ShowFlashcardsEmptyDashboard));
        OnPropertyChanged(nameof(ShouldShowFlashcardsDashboardContent));
    }

    private void NotifyMindMapsDashboardPresentationChanged()
    {
        OnPropertyChanged(nameof(IsMindMapsDashboardLoading));
        OnPropertyChanged(nameof(ShowMindMapsEmptyDashboard));
        OnPropertyChanged(nameof(ShouldShowMindMapsDashboardContent));
    }

    private void NotifyQuizzesDashboardPresentationChanged()
    {
        OnPropertyChanged(nameof(IsQuizzesDashboardLoading));
        OnPropertyChanged(nameof(ShowQuizzesEmptyDashboard));
        OnPropertyChanged(nameof(ShouldShowQuizzesDashboardContent));
    }

    private NoteCardViewModel CreateNoteCard(NoteDocument doc)
    {
        return new NoteCardViewModel(
            doc, 
            RequestDeleteNote, 
            RemoveFromGroup,
            DuplicateNote,
            TogglePin
            );
    }

    private NoteGroupData EnsureGroupMetadata(Guid groupId)
    {
        if (_groupMetadata.TryGetValue(groupId, out var metadata))
        {
            metadata.SortOrder ??= GetNextGroupSortOrder();
            return metadata;
        }

        metadata = new NoteGroupData
        {
            GroupId = groupId,
            Name = string.Format(
                LocalizationService.GetString("GroupTitleFormat"),
                groupId.ToString()[..4].ToUpperInvariant()),
            BackgroundColor = DefaultGroupBackground,
            SortOrder = GetNextGroupSortOrder()
        };
        _groupMetadata[groupId] = metadata;
        return metadata;
    }

    private int GetNextGroupSortOrder()
    {
        if (_groupMetadata.Count == 0)
            return 0;

        return _groupMetadata.Values
            .Select(m => m.SortOrder ?? -1)
            .DefaultIfEmpty(-1)
            .Max() + 1;
    }

    private bool TryMoveGroup(NoteGroupViewModel group, bool moveUp)
    {
        var currentIndex = NoteGroups.IndexOf(group);
        if (currentIndex < 0)
            return false;

        var targetIndex = moveUp ? currentIndex - 1 : currentIndex + 1;
        if (targetIndex < 0 || targetIndex >= NoteGroups.Count)
            return false;

        var targetGroup = NoteGroups[targetIndex];
        var current = EnsureGroupMetadata(group.GroupId);
        var target = EnsureGroupMetadata(targetGroup.GroupId);

        (current.SortOrder, target.SortOrder) = (target.SortOrder, current.SortOrder);
        NoteGroups.Move(currentIndex, targetIndex);
        QueueSaveNotes();
        return true;
    }

    private void QueueSaveNotes()
    {
        if (_saveNotesQueued)
            return;

        _saveNotesQueued = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _saveNotesQueued = false;
            SaveNotes();
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            _saveNotesQueued = false;
            SaveNotes();
        }), DispatcherPriority.Background);
    }
}
