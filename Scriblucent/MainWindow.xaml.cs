using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Markup;

namespace Scriblucent
{
    public partial class MainWindow : Window
    {
        private const int WmSettingChange = 0x001A;

        private const double DragScrollEdgeThreshold = 64;
        private const double DragScrollStep = 18;
        private const double DashboardDragScrollEdgeThreshold = 96;
        private const double DashboardDragScrollMinStep = 16;
        private const double DashboardDragScrollMaxStep = 46;
        private const double TopSearchExpandedWidth = 320;
        private const double ClearSearchButtonWidth = 22;
        private const double ClearSearchButtonMargin = 4;
        private const double ClearSearchButtonHiddenScale = 0.84;
        private static readonly CubicEase ClearSearchShowEase = FreezeEase(EasingMode.EaseOut);
        private static readonly CubicEase ClearSearchHideEase = FreezeEase(EasingMode.EaseIn);
        private bool _clearSearchButtonShown;
        private const string DashboardNotes = "Notes";
        private const string DashboardFlashcards = "Flashcards";
        private const string DashboardQuizzes = "Quizzes";
        private const string DashboardMindMaps = "MindMaps";

        private MainViewModel? _observedViewModel;
        private string _lastActiveDashboard = string.Empty;
        private bool _dashboardTransitionActive;

        internal bool IsDashboardTransitionActive => _dashboardTransitionActive;

        private readonly HashSet<FrameworkElement> _emptyDashboardEnterScheduled = new();
        private bool _dashboardPresenceHandlersAttached;
        private bool _notesLayoutRefreshQueued;
        private bool _ungroupedLayoutRefreshQueued;

        private bool _dashboardLayoutRefreshQueued;
        private NoteEditorTabsWindow? _noteEditorTabsWindow;
        private Point _dashboardListingDragStart;
        private bool _suppressDashboardListingOpen;
        private const double UngroupedDropZoneCornerRadius = 16;

        private readonly Dictionary<Border, Brush?> _dropZoneOriginalBorderBrushes = new();
        private readonly Dictionary<Border, Thickness> _dropZoneOriginalBorderThicknesses = new();
        private readonly Dictionary<Border, CornerRadius> _dropZoneOriginalCornerRadius = new();
        private readonly Dictionary<Border, Brush?> _dropZoneOriginalBackgrounds = new();
        private WeakReference<FrameworkElement>? _listingDropGlowHost;
        private DispatcherTimer? _dashboardDragScrollTimer;
        private ScrollViewer? _dashboardDragScrollViewer;
        private Point _dashboardDragScrollPosition;


        private FrameworkElement? NotesDashboardPageElement => NotesDashboardView;
        private FrameworkElement? FlashcardsDashboardPageElement => FlashcardsDashboardView;
        private FrameworkElement? QuizzesDashboardPageElement => QuizzesDashboardView;
        private FrameworkElement? MindMapsDashboardPageElement => MindMapsDashboardView;

        private object? FindDashboardName(string name)
            => NotesDashboardView?.FindName(name)
               ?? FlashcardsDashboardView?.FindName(name)
               ?? QuizzesDashboardView?.FindName(name)
               ?? MindMapsDashboardView?.FindName(name)
               ?? FindName(name);

        private ScrollViewer? NotesScrollViewer
            => NotesDashboardView?.FindName("NotesScrollViewer") as ScrollViewer;

        private ScrollViewer? RecentNotesScrollViewer
            => NotesDashboardView?.FindName("RecentNotesScrollViewer") as ScrollViewer;

        private FrameworkElement? RecentSectionBodyElement => FindDashboardName("RecentSectionBody") as FrameworkElement;
        private FrameworkElement? FlashcardRecentSectionBodyElement => FindDashboardName("FlashcardRecentSectionBody") as FrameworkElement;
        private FrameworkElement? QuizRecentSectionBodyElement => FindDashboardName("QuizRecentSectionBody") as FrameworkElement;
        private FrameworkElement? MindMapRecentSectionBodyElement => FindDashboardName("MindMapRecentSectionBody") as FrameworkElement;
        private FrameworkElement? CalendarSectionBodyElement => FindDashboardName("CalendarSectionBody") as FrameworkElement;
        private FrameworkElement? FlashcardCalendarSectionBodyElement => FindDashboardName("FlashcardCalendarSectionBody") as FrameworkElement;
        private FrameworkElement? QuizCalendarSectionBodyElement => FindDashboardName("QuizCalendarSectionBody") as FrameworkElement;
        private FrameworkElement? MindMapCalendarSectionBodyElement => FindDashboardName("MindMapCalendarSectionBody") as FrameworkElement;
        private FrameworkElement? GroupsSectionBodyElement => FindDashboardName("GroupsSectionBody") as FrameworkElement;
        private ItemsControl? GroupsItemsControlElement => FindDashboardName("GroupsItemsControl") as ItemsControl;
        private FrameworkElement? UngroupedSectionBodyElement => FindDashboardName("UngroupedSectionBody") as FrameworkElement;
        private FrameworkElement? UngroupedNotesItemsControlElement => FindDashboardName("UngroupedNotesItemsControl") as FrameworkElement;
        private FrameworkElement? NotesDashboardSectionBodyElement => FindDashboardName("NotesDashboardSectionBody") as FrameworkElement;
        private FrameworkElement? FlashcardGroupsSectionBodyElement => FindDashboardName("FlashcardGroupsSectionBody") as FrameworkElement;
        private FrameworkElement? FlashcardDashboardGroupsSectionBodyElement => FindDashboardName("FlashcardDashboardGroupsSectionBody") as FrameworkElement;
        private FrameworkElement? FlashcardDashboardUngroupedSectionBodyElement => FindDashboardName("FlashcardDashboardUngroupedSectionBody") as FrameworkElement;
        private ItemsControl? FlashcardDashboardGroupsItemsControlElement => FindDashboardName("FlashcardDashboardGroupsItemsControl") as ItemsControl;
        private FrameworkElement? FlashcardDashboardUngroupedItemsControlElement => FindDashboardName("FlashcardDashboardUngroupedItemsControl") as FrameworkElement;
        private FrameworkElement? QuizGroupsSectionBodyElement => FindDashboardName("QuizGroupsSectionBody") as FrameworkElement;
        private FrameworkElement? QuizDashboardGroupsSectionBodyElement => FindDashboardName("QuizDashboardGroupsSectionBody") as FrameworkElement;
        private FrameworkElement? QuizDashboardUngroupedSectionBodyElement => FindDashboardName("QuizDashboardUngroupedSectionBody") as FrameworkElement;
        private ItemsControl? QuizDashboardGroupsItemsControlElement => FindDashboardName("QuizDashboardGroupsItemsControl") as ItemsControl;
        private FrameworkElement? QuizDashboardUngroupedItemsControlElement => FindDashboardName("QuizDashboardUngroupedItemsControl") as FrameworkElement;
        private FrameworkElement? MindMapGroupsSectionBodyElement => FindDashboardName("MindMapGroupsSectionBody") as FrameworkElement;
        private FrameworkElement? MindMapDashboardGroupsSectionBodyElement => FindDashboardName("MindMapDashboardGroupsSectionBody") as FrameworkElement;
        private FrameworkElement? MindMapDashboardUngroupedSectionBodyElement => FindDashboardName("MindMapDashboardUngroupedSectionBody") as FrameworkElement;
        private FrameworkElement? NotesDashboardViewElement => NotesDashboardView;
        private FrameworkElement? FlashcardsDashboardViewElement => FlashcardsDashboardView;
        private FrameworkElement? QuizzesDashboardViewElement => QuizzesDashboardView;
        private FrameworkElement? MindMapsDashboardViewElement => MindMapsDashboardView;
        private FrameworkElement? NotesEmptyDashboardPanelElement => FindDashboardName("NotesEmptyDashboardPanel") as FrameworkElement;
        private FrameworkElement? NotesDashboardContentHostElement => FindDashboardName("NotesDashboardContentHost") as FrameworkElement;
        private FrameworkElement? FlashcardsDashboardContentHostElement => FindDashboardName("FlashcardsDashboardContentHost") as FrameworkElement;
        private FrameworkElement? QuizzesDashboardContentHostElement => FindDashboardName("QuizzesDashboardContentHost") as FrameworkElement;
        private FrameworkElement? MindMapsDashboardContentHostElement => FindDashboardName("MindMapsDashboardContentHost") as FrameworkElement;
        private FrameworkElement? FlashcardsEmptyDashboardPanelElement => FindDashboardName("FlashcardsEmptyDashboardPanel") as FrameworkElement;
        private FrameworkElement? QuizzesEmptyDashboardPanelElement => FindDashboardName("QuizzesEmptyDashboardPanel") as FrameworkElement;
        private FrameworkElement? MindMapsEmptyDashboardPanelElement => FindDashboardName("MindMapsEmptyDashboardPanel") as FrameworkElement;
        private ItemsControl? MindMapDashboardGroupsItemsControlElement => FindDashboardName("MindMapDashboardGroupsItemsControl") as ItemsControl;
        private FrameworkElement? MindMapDashboardUngroupedItemsControlElement => FindDashboardName("MindMapDashboardUngroupedItemsControl") as FrameworkElement;
        private ScrollViewer? FlashcardsScrollViewerElement => FindDashboardName("FlashcardsScrollViewer") as ScrollViewer;
        private ScrollViewer? QuizzesScrollViewerElement => FindDashboardName("QuizzesScrollViewer") as ScrollViewer;
        private ScrollViewer? MindMapsScrollViewerElement => FindDashboardName("MindMapsScrollViewer") as ScrollViewer;
        private FrameworkElement? CalendarSectionContainerElement => FindDashboardName("CalendarSectionContainer") as FrameworkElement;
        private FrameworkElement? FlashcardCalendarSectionContainerElement => FindDashboardName("FlashcardCalendarSectionContainer") as FrameworkElement;
        private FrameworkElement? QuizCalendarSectionContainerElement => FindDashboardName("QuizCalendarSectionContainer") as FrameworkElement;
        private FrameworkElement? MindMapCalendarSectionContainerElement => FindDashboardName("MindMapCalendarSectionContainer") as FrameworkElement;
        private FrameworkElement? GroupsSectionContainerElement => FindDashboardName("GroupsSectionContainer") as FrameworkElement;
        private FrameworkElement? UngroupedSectionContainerElement => FindDashboardName("UngroupedSectionContainer") as FrameworkElement;
        private FrameworkElement? FlashcardDashboardUngroupedSectionContainerElement => FindDashboardName("FlashcardDashboardUngroupedSectionContainer") as FrameworkElement;
        private FrameworkElement? QuizDashboardUngroupedSectionContainerElement => FindDashboardName("QuizDashboardUngroupedSectionContainer") as FrameworkElement;
        private FrameworkElement? MindMapDashboardUngroupedSectionContainerElement => FindDashboardName("MindMapDashboardUngroupedSectionContainer") as FrameworkElement;
        private FrameworkElement? FlashcardDashboardGroupsSectionContainerElement => FindDashboardName("FlashcardDashboardGroupsSectionContainer") as FrameworkElement;
        private FrameworkElement? QuizDashboardGroupsSectionContainerElement => FindDashboardName("QuizDashboardGroupsSectionContainer") as FrameworkElement;
        private FrameworkElement? MindMapDashboardGroupsSectionContainerElement => FindDashboardName("MindMapDashboardGroupsSectionContainer") as FrameworkElement;
        private FrameworkElement? MassSelectOverlayToolbarElement => FindDashboardName("MassSelectOverlayToolbar") as FrameworkElement;
        private FrameworkElement? MassSelectMoreActionsPanelElement => FindDashboardName("MassSelectMoreActionsPanel") as FrameworkElement;
        private FrameworkElement? MassSelectMoreActionsSeparatorElement => FindDashboardName("MassSelectMoreActionsSeparator") as FrameworkElement;
        private Button? MassSelectMoreActionsToggleButtonElement => FindDashboardName("MassSelectMoreActionsToggleButton") as Button;
        private Popup? SortNotesPopupElement => FindDashboardName("SortNotesPopup") as Popup;
        private Popup? DashboardSectionsPopupElement => FindDashboardName("DashboardSectionsPopup") as Popup;
        private ColumnDefinition? SidebarColumnElement => FindDashboardName("SidebarColumn") as ColumnDefinition;
        private FrameworkElement? SidebarChromeElement => FindDashboardName("SidebarChrome") as FrameworkElement;
        private FrameworkElement? MainContentRootElement => FindDashboardName("MainContentRoot") as FrameworkElement;
        private bool _isMassSelectMoreActionsExpanded;

        internal static bool IsNoteDragInProgress { get; private set; }

        internal static void SetNoteDragInProgress(bool isInProgress)
        {
            IsNoteDragInProgress = isInProgress;
        }

        private bool _startupDataInitialized;
        private readonly Models.AppSettings? _startupSettings;

        public MainWindow(Models.AppSettings? startupSettings = null)
        {
            _startupSettings = startupSettings;
            Scriblucent.Services.StartupProbe.Mark("windowCtorStart");
            InitializeComponent();
            Scriblucent.Services.StartupProbe.Mark("windowInit");
            Scriblucent.Services.WindowThemeService.Register(this);
            ApplyCurrentLanguage();
            LocalizationService.CultureChanged += LocalizationService_CultureChanged;
            Loaded += MainWindow_Loaded;
            ContentRendered += MainWindow_ContentRendered;
            Unloaded += MainWindow_Unloaded;
            AttachDashboardPresenceHandlers();
            StateChanged += MainWindow_StateChanged;
            DataContextChanged += MainWindow_DataContextChanged;
            InitializeShellViewModel();
            Scriblucent.Services.StartupProbe.Mark("windowShellVm");

            // Startup probe: the first rendered frame marks the dashboard's first layout.
            CompositionTarget.Rendering += StartupProbeFirstFrame;
        }

        private static void StartupProbeFirstFrame(object? sender, EventArgs e)
        {
            CompositionTarget.Rendering -= StartupProbeFirstFrame;
            Scriblucent.Services.StartupProbe.Mark("firstLayout");
        }

        private void InitializeShellViewModel()
        {
            if (DataContext is MainViewModel)
                return;

            var viewModel = new MainViewModel(
                _startupSettings,
                loadPersistedContent: false,
                applyThemeAndCulture: false);
            DataContext = viewModel;
            SetDashboardViewImmediate(viewModel.ActiveDashboard);
            _lastActiveDashboard = viewModel.ActiveDashboard;
            ApplySectionStateImmediately();
        }

        private void MainWindow_ContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= MainWindow_ContentRendered;
            InitializeStartupData();
        }

        private void InitializeStartupData()
        {
            if (_startupDataInitialized)
                return;

            _startupDataInitialized = true;
            Dispatcher.BeginInvoke(() =>
            {
                Scriblucent.Services.ActivityTracker.Initialize();

                if (DataContext is MainViewModel viewModel)
                    viewModel.LoadPersistedContent();
            }, DispatcherPriority.Background);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WndProc);
        }

        // Owned windows (Note Editor, MindMap/Flashcard/Quiz editors) are destroyed by the OS as
        // a side effect of this window's own native destruction, which bypasses their managed
        // Closing event entirely - so their unsaved-changes prompts never get a chance to run
        // unless we close them ourselves first, through the normal Close() pipeline, before
        // letting our own close proceed.
        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!WindowCloseCascade.TryCloseOwnedWindows(this))
                e.Cancel = true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmSettingChange)
            {
                ReducedMotionPreferences.Refresh();
                if (IsLoadingDashboardActive)
                {
                    _dashboardLoadingClock.Restart();
                    DashboardLoadingShell.StartWave(UiMotion.PrefersReducedMotion);
                    if (UiMotion.PrefersReducedMotion)
                    {
                        _startupLoadingEntrancePending = false;
                        DashboardLoadingShell.BeginAnimation(UIElement.OpacityProperty, null);
                        DashboardLoadingShell.Opacity = 1;
                    }
                }
            }

            return IntPtr.Zero;
        }

        private void LocalizationService_CultureChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(ApplyCurrentLanguage);
        }

        private void ApplyCurrentLanguage()
        {
            Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
        }

        private static readonly object ListingShadowSuspendScope = new();
        private static readonly object SidebarAnimationShadowScope = new();
        private static readonly object DashboardTransitionShadowScope = new();

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                SuspendListingCardShadows();
                return;
            }

            if (WindowState is not (WindowState.Normal or WindowState.Maximized))
                return;

            DashboardShadowSuspender.TryResume(ListingShadowSuspendScope);
            Dispatcher.BeginInvoke(() =>
            {
                RefreshDashboardVirtualizationHosts();
                EnsureActiveListingDashboardInteractive();
            }, DispatcherPriority.Loaded);
        }

        private void SuspendListingCardShadows()
        {
            DashboardShadowSuspender.TrySuspend(ListingShadowSuspendScope, NotesScrollViewer);
            DashboardShadowSuspender.TrySuspend(ListingShadowSuspendScope, FlashcardsScrollViewerElement);
            DashboardShadowSuspender.TrySuspend(ListingShadowSuspendScope, QuizzesScrollViewerElement);
            DashboardShadowSuspender.TrySuspend(ListingShadowSuspendScope, MindMapsScrollViewerElement);
            DashboardShadowSuspender.TrySuspend(ListingShadowSuspendScope, RecentNotesScrollViewer);
        }

        internal void BeginChildWindowOwnerMotion(ref CacheMode? savedMainContentCacheMode)
        {
            SuspendListingCardShadows();

            if (MainContentRootElement is not { } root)
                return;

            savedMainContentCacheMode = root.CacheMode;
            root.CacheMode = DpiAwareBitmapCache.Create(root, enableClearType: true);
        }

        internal void EndChildWindowOwnerMotion(CacheMode? savedMainContentCacheMode)
        {
            if (MainContentRootElement is { } root)
                root.CacheMode = savedMainContentCacheMode;

            DashboardShadowSuspender.TryResume(ListingShadowSuspendScope);
        }

        private void SuspendDashboardShadowsForSidebarAnimation()
        {
            DashboardShadowSuspender.TryResume(SidebarAnimationShadowScope);
            DashboardShadowSuspender.TrySuspend(SidebarAnimationShadowScope, NotesScrollViewer);
            DashboardShadowSuspender.TrySuspend(SidebarAnimationShadowScope, FlashcardsScrollViewerElement);
            DashboardShadowSuspender.TrySuspend(SidebarAnimationShadowScope, QuizzesScrollViewerElement);
            DashboardShadowSuspender.TrySuspend(SidebarAnimationShadowScope, MindMapsScrollViewerElement);
            DashboardShadowSuspender.TrySuspend(SidebarAnimationShadowScope, RecentNotesScrollViewer);
        }

        private void CompleteSidebarAnimation()
            => DashboardShadowSuspender.TryResume(SidebarAnimationShadowScope);

        private void OnNotesDashboardReady(object? sender, EventArgs e)
        {
            if (sender is not MainViewModel vm)
                return;

            if (ResolveDashboardMotionScope(vm.ActiveDashboard) is { } scope)
                EnsureDashboardContentHostRestState(scope);

            if (IsLoaded)
                QueueActiveDashboardEmptyEnterIfNeeded(vm);

            QueueNotesLayoutRefresh();
            QueueDashboardLayoutRefresh();
            EnsureActiveListingDashboardInteractive();
            Dispatcher.BeginInvoke(EnsureAllListingDashboardsInteractive, DispatcherPriority.ApplicationIdle);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            ConfigurePopupAnimations();
            ApplySectionStateImmediately();
            EnsureAllDashboardContentHostsRestState();
            StartStartupLoadingEntrance();
            if (DataContext is MainViewModel vm && vm.IsNotesDashboardReady)
                OnNotesDashboardReady(vm, EventArgs.Empty);
            else
                Dispatcher.BeginInvoke(EnsureActiveListingDashboardInteractive, DispatcherPriority.Loaded);
        }

        private void MainWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            CancelDashboardLoading();
            LocalizationService.CultureChanged -= LocalizationService_CultureChanged;
            AttachViewModel(null);
        }

        private void MainWindow_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            AttachViewModel(e.NewValue as MainViewModel);
            if (e.NewValue is MainViewModel vm)
            {
                SetDashboardViewImmediate(vm.ActiveDashboard);
                _lastActiveDashboard = vm.ActiveDashboard;
                if (IsLoaded)
                    QueueActiveDashboardEmptyEnterIfNeeded(vm);
            }

            ApplySectionStateImmediately();
            EnsureAllDashboardContentHostsRestState();
            SyncDashboardLoadingState(e.NewValue as MainViewModel);
            Dispatcher.BeginInvoke(EnsureActiveListingDashboardInteractive, DispatcherPriority.Loaded);
        }

        private void AttachViewModel(MainViewModel? vm)
        {
            if (_observedViewModel != null)
            {
                _observedViewModel.NotesDashboardReady -= OnNotesDashboardReady;
                ReleaseDashboardMotionEngines();
                _observedViewModel.MotionHost = null;
                _observedViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _observedViewModel.Notes.CollectionChanged -= ViewModel_NotesCollectionChanged;
                _observedViewModel.NoteGroups.CollectionChanged -= ViewModel_NoteGroupsCollectionChanged;
                _observedViewModel.FlashcardSets.CollectionChanged -= ViewModel_DashboardCollectionChanged;
                _observedViewModel.FlashcardSetGroups.CollectionChanged -= ViewModel_DashboardCollectionChanged;
                _observedViewModel.MindMaps.CollectionChanged -= ViewModel_DashboardCollectionChanged;
                _observedViewModel.MindMapGroups.CollectionChanged -= ViewModel_DashboardCollectionChanged;
                _observedViewModel.Quizzes.CollectionChanged -= ViewModel_DashboardCollectionChanged;
                _observedViewModel.QuizGroups.CollectionChanged -= ViewModel_DashboardCollectionChanged;
            }

            _observedViewModel = vm;

            if (_observedViewModel != null)
            {
                _observedViewModel.MotionHost = this;
                _observedViewModel.NotesDashboardReady += OnNotesDashboardReady;
                if (_observedViewModel.IsNotesDashboardReady)
                    OnNotesDashboardReady(_observedViewModel, EventArgs.Empty);

                _observedViewModel.PropertyChanged += ViewModel_PropertyChanged;
                _observedViewModel.Notes.CollectionChanged += ViewModel_NotesCollectionChanged;
                _observedViewModel.NoteGroups.CollectionChanged += ViewModel_NoteGroupsCollectionChanged;
                _observedViewModel.FlashcardSets.CollectionChanged += ViewModel_DashboardCollectionChanged;
                _observedViewModel.FlashcardSetGroups.CollectionChanged += ViewModel_FlashcardSetGroupsCollectionChanged;
                _observedViewModel.MindMaps.CollectionChanged += ViewModel_DashboardCollectionChanged;
                _observedViewModel.MindMapGroups.CollectionChanged += ViewModel_MindMapGroupsCollectionChanged;
                _observedViewModel.Quizzes.CollectionChanged += ViewModel_DashboardCollectionChanged;
                _observedViewModel.QuizGroups.CollectionChanged += ViewModel_QuizGroupsCollectionChanged;
                _notesHadGroups = _observedViewModel.HasGroups;
                _flashcardsHadGroups = _observedViewModel.HasFlashcardSetGroups;
                _quizzesHadGroups = _observedViewModel.HasQuizGroups;
                _mindMapsHadGroups = _observedViewModel.HasMindMapGroups;
                SyncClearSearchButton(animate: false);
                Dispatcher.BeginInvoke(EnsureAllDashboardContentHostsRestState, DispatcherPriority.Loaded);
                QueueNotesLayoutRefresh();
                QueueDashboardLayoutRefresh();
            }
        }

        private void ViewModel_NotesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove)
                return;

            QueueNotesLayoutRefresh();
        }

        private bool _notesHadGroups;
        private bool _flashcardsHadGroups;
        private bool _quizzesHadGroups;
        private bool _mindMapsHadGroups;

        private void ViewModel_NoteGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Remove
                     && e.OldItems is not null
                     && !ShouldSkipGroupRemoveAnimation)
            {
                foreach (NoteGroupViewModel removed in e.OldItems)
                    AnimateRemovedGroupCard(GroupsItemsControlElement, removed.GroupId);
            }

            QueueNotesLayoutRefresh();
        }

        private void ViewModel_FlashcardSetGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Remove
                     && e.OldItems is not null
                     && !ShouldSkipGroupRemoveAnimation)
            {
                foreach (FlashcardSetGroupViewModel removed in e.OldItems)
                    AnimateRemovedGroupCard(FlashcardDashboardGroupsItemsControlElement, removed.GroupId);
            }

            QueueDashboardLayoutRefresh();
        }

        private void ViewModel_QuizGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Remove
                     && e.OldItems is not null
                     && !ShouldSkipGroupRemoveAnimation)
            {
                foreach (QuizGroupViewModel removed in e.OldItems)
                    AnimateRemovedGroupCard(QuizDashboardGroupsItemsControlElement, removed.GroupId);
            }

            QueueDashboardLayoutRefresh();
        }

        private void ViewModel_MindMapGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Remove
                     && e.OldItems is not null
                     && !ShouldSkipGroupRemoveAnimation)
            {
                foreach (MindMapGroupViewModel removed in e.OldItems)
                    AnimateRemovedGroupCard(MindMapDashboardGroupsItemsControlElement, removed.GroupId);
            }

            QueueDashboardLayoutRefresh();
        }

        private void AnimateRemovedGroupCard(ItemsControl? itemsControl, Guid groupId)
        {
            if (itemsControl is null)
                return;

            var border = FindGroupCardBorder(itemsControl, groupId);
            if (border is null)
                return;

            CardSurfaceMotionBehavior.PlayExit(border);
        }

        private static FrameworkElement? FindGroupCardBorder(DependencyObject root, Guid groupId)
        {
            if (root is Border { Tag: NoteGroupViewModel noteGroup } && noteGroup.GroupId == groupId)
                return (FrameworkElement)root;
            if (root is Border { Tag: FlashcardSetGroupViewModel flashcardGroup } && flashcardGroup.GroupId == groupId)
                return (FrameworkElement)root;
            if (root is Border { Tag: QuizGroupViewModel quizGroup } && quizGroup.GroupId == groupId)
                return (FrameworkElement)root;
            if (root is Border { Tag: MindMapGroupViewModel mindMapGroup } && mindMapGroup.GroupId == groupId)
                return (FrameworkElement)root;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = FindGroupCardBorder(VisualTreeHelper.GetChild(root, i), groupId);
                if (found is not null)
                    return found;
            }

            return null;
        }

        private void QueueUngroupedNotesLayoutRefresh()
        {
            if (_ungroupedLayoutRefreshQueued)
                return;

            _ungroupedLayoutRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _ungroupedLayoutRefreshQueued = false;
                UngroupedSectionBodyElement?.InvalidateMeasure();
                UngroupedSectionBodyElement?.InvalidateArrange();
            }), DispatcherPriority.Background);
        }

        private bool _flashcardUngroupedLayoutRefreshQueued;
        private bool _quizUngroupedLayoutRefreshQueued;
        private bool _mindMapUngroupedLayoutRefreshQueued;

        private void QueueUngroupedFlashcardsLayoutRefresh()
        {
            if (_dashboardTransitionActive
                || DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.Flashcards)
                || _flashcardUngroupedLayoutRefreshQueued)
                return;

            _flashcardUngroupedLayoutRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _flashcardUngroupedLayoutRefreshQueued = false;
                FlashcardDashboardUngroupedSectionBodyElement?.InvalidateMeasure();
                FlashcardDashboardUngroupedSectionBodyElement?.InvalidateArrange();
            }), DispatcherPriority.Background);
        }

        private void QueueUngroupedQuizzesLayoutRefresh()
        {
            if (_dashboardTransitionActive
                || DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.Quizzes)
                || _quizUngroupedLayoutRefreshQueued)
                return;

            _quizUngroupedLayoutRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _quizUngroupedLayoutRefreshQueued = false;
                QuizDashboardUngroupedSectionBodyElement?.InvalidateMeasure();
                QuizDashboardUngroupedSectionBodyElement?.InvalidateArrange();
            }), DispatcherPriority.Background);
        }

        private void QueueUngroupedMindMapsLayoutRefresh()
        {
            if (_dashboardTransitionActive
                || DashboardMotionEngine.ShouldDeferVirtualizationRefresh(DashboardMotionScope.MindMaps)
                || _mindMapUngroupedLayoutRefreshQueued)
                return;

            _mindMapUngroupedLayoutRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _mindMapUngroupedLayoutRefreshQueued = false;
                MindMapDashboardUngroupedSectionBodyElement?.InvalidateMeasure();
                MindMapDashboardUngroupedSectionBodyElement?.InvalidateArrange();
            }), DispatcherPriority.Background);
        }

        private void ViewModel_DashboardCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove)
                return;

            QueueDashboardLayoutRefresh();
        }

        private void QueueNotesLayoutRefresh()
        {
            if (_dashboardTransitionActive
                || DashboardMotionEngine.ShouldDeferVirtualizationRefresh()
                || _notesLayoutRefreshQueued
                || !string.Equals(_observedViewModel?.ActiveDashboard, DashboardNotes, StringComparison.Ordinal))
                return;

            _notesLayoutRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _notesLayoutRefreshQueued = false;
                RefreshNotesDashboardLayout();
            }), DispatcherPriority.Background);
        }

        private void RefreshNotesDashboardLayout()
        {
            if (_observedViewModel?.HasNotes == true)
                EnsureDashboardContentHostRestState(DashboardMotionScope.Notes);

            GroupsItemsControlElement?.InvalidateMeasure();
            GroupsItemsControlElement?.InvalidateArrange();
            NotesScrollViewer?.InvalidateMeasure();
            NotesScrollViewer?.InvalidateArrange();
            CalendarSectionContainerElement?.InvalidateMeasure();
            CalendarSectionBodyElement?.InvalidateMeasure();
            GroupsSectionContainerElement?.InvalidateMeasure();
            GroupsSectionContainerElement?.InvalidateArrange();
            GroupsSectionBodyElement?.InvalidateMeasure();
            GroupsSectionBodyElement?.InvalidateArrange();
            UngroupedSectionContainerElement?.InvalidateMeasure();
            UngroupedSectionContainerElement?.InvalidateArrange();
            UngroupedSectionBodyElement?.InvalidateMeasure();
            UngroupedSectionBodyElement?.InvalidateArrange();
            NotesDashboardSectionBodyElement?.InvalidateMeasure();
            NotesDashboardSectionBodyElement?.InvalidateArrange();

            RefreshDashboardVirtualizationHosts(DashboardMotionScope.Notes);
        }

        private void QueueDashboardLayoutRefresh()
        {
            if (_dashboardTransitionActive
                || DashboardMotionEngine.ShouldDeferVirtualizationRefresh()
                || _dashboardLayoutRefreshQueued)
                return;

            _dashboardLayoutRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _dashboardLayoutRefreshQueued = false;
                RefreshDashboardBlockLayouts();
            }), DispatcherPriority.Background);
        }

        private void RefreshDashboardBlockLayouts()
        {
            if (_observedViewModel != null)
                EnsureExpandedSectionsNotClipped(_observedViewModel);

            if (_observedViewModel is { } viewModel)
            {
                var restScope = viewModel.ActiveDashboard switch
                {
                    DashboardFlashcards when viewModel.HasFlashcardSets => DashboardMotionScope.Flashcards,
                    DashboardQuizzes when viewModel.HasQuizzes => DashboardMotionScope.Quizzes,
                    DashboardMindMaps when viewModel.HasMindMaps => DashboardMotionScope.MindMaps,
                    _ => (DashboardMotionScope?)null
                };

                if (restScope is { } scopeToRestore)
                    EnsureDashboardContentHostRestState(scopeToRestore);
            }

            DashboardMotionScope? scope = _observedViewModel?.ActiveDashboard switch
            {
                DashboardFlashcards => DashboardMotionScope.Flashcards,
                DashboardQuizzes => DashboardMotionScope.Quizzes,
                DashboardMindMaps => DashboardMotionScope.MindMaps,
                _ => null
            };

            switch (_observedViewModel?.ActiveDashboard)
            {
                case DashboardFlashcards:
                    InvalidateDashboardBlockLayout(
                        FlashcardDashboardGroupsItemsControlElement,
                        FlashcardDashboardUngroupedItemsControlElement,
                        FlashcardGroupsSectionBodyElement,
                        FlashcardDashboardGroupsSectionBodyElement,
                        FlashcardDashboardUngroupedSectionBodyElement,
                        FlashcardsScrollViewerElement);
                    break;
                case DashboardQuizzes:
                    InvalidateDashboardBlockLayout(
                        QuizDashboardGroupsItemsControlElement,
                        QuizDashboardUngroupedItemsControlElement,
                        QuizGroupsSectionBodyElement,
                        QuizDashboardGroupsSectionBodyElement,
                        QuizDashboardUngroupedSectionBodyElement,
                        QuizzesScrollViewerElement);
                    break;
                case DashboardMindMaps:
                    InvalidateDashboardBlockLayout(
                        MindMapDashboardGroupsItemsControlElement,
                        MindMapDashboardUngroupedItemsControlElement,
                        MindMapGroupsSectionBodyElement,
                        MindMapDashboardGroupsSectionBodyElement,
                        MindMapDashboardUngroupedSectionBodyElement,
                        MindMapsScrollViewerElement);
                    break;
            }

            if (scope is { } activeScope)
                RefreshDashboardVirtualizationHosts(activeScope);
        }

        private static void InvalidateDashboardBlockLayout(params FrameworkElement?[] elements)
        {
            foreach (var element in elements)
            {
                element?.InvalidateMeasure();
                element?.InvalidateArrange();
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not MainViewModel vm)
                return;

            Dispatcher.Invoke(() =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsRecentSectionExpanded))
                {
                    AnimateSharedDashboardSectionVisibility(
                        vm,
                        ResolveActiveRecentSectionBody(vm),
                        RecentSectionBodyElement,
                        FlashcardRecentSectionBodyElement,
                        QuizRecentSectionBodyElement,
                        MindMapRecentSectionBodyElement,
                        vm.IsRecentSectionExpanded,
                        ScheduleAllDashboardVirtualizationRefreshAfterSectionAnimation);
                }
                else if (e.PropertyName == nameof(MainViewModel.IsCalendarSectionExpanded))
                {
                    AnimateSharedDashboardSectionVisibility(
                        vm,
                        ResolveActiveCalendarSectionBody(vm),
                        CalendarSectionBodyElement,
                        FlashcardCalendarSectionBodyElement,
                        QuizCalendarSectionBodyElement,
                        MindMapCalendarSectionBodyElement,
                        vm.IsCalendarSectionExpanded,
                        ScheduleAllDashboardVirtualizationRefreshAfterSectionAnimation);
                }
                else if (e.PropertyName == nameof(MainViewModel.IsGroupsSectionExpanded))
                    AnimateSectionVisibility(GroupsSectionBodyElement, vm.IsGroupsSectionExpanded, QueueNotesLayoutRefresh);
                else if (e.PropertyName == nameof(MainViewModel.IsUngroupedSectionExpanded))
                    AnimateSectionVisibility(UngroupedSectionBodyElement, vm.IsUngroupedSectionExpanded, QueueNotesLayoutRefresh);
                else if (e.PropertyName == nameof(MainViewModel.IsNotesDashboardSectionExpanded))
                    AnimateSectionVisibility(NotesDashboardSectionBodyElement, vm.IsNotesDashboardSectionExpanded, QueueNotesLayoutRefresh);
                else if (e.PropertyName == nameof(MainViewModel.IsFlashcardGroupsSectionExpanded))
                    AnimateSectionVisibility(FlashcardGroupsSectionBodyElement, vm.IsFlashcardGroupsSectionExpanded, ScheduleFlashcardVirtualizationRefreshAfterSectionAnimation);
                else if (e.PropertyName == nameof(MainViewModel.IsFlashcardDashboardGroupsSectionExpanded))
                    AnimateSectionVisibility(FlashcardDashboardGroupsSectionBodyElement, vm.IsFlashcardDashboardGroupsSectionExpanded, ScheduleFlashcardVirtualizationRefreshAfterSectionAnimation);
                else if (e.PropertyName == nameof(MainViewModel.IsFlashcardDashboardUngroupedSectionExpanded))
                    AnimateSectionVisibility(FlashcardDashboardUngroupedSectionBodyElement, vm.IsFlashcardDashboardUngroupedSectionExpanded, ScheduleFlashcardVirtualizationRefreshAfterSectionAnimation);
                else if (e.PropertyName == nameof(MainViewModel.IsQuizGroupsSectionExpanded))
                    AnimateSectionVisibility(QuizGroupsSectionBodyElement, vm.IsQuizGroupsSectionExpanded, ScheduleQuizVirtualizationRefreshAfterSectionAnimation);
                else if (e.PropertyName == nameof(MainViewModel.IsQuizDashboardGroupsSectionExpanded))
                    AnimateSectionVisibility(QuizDashboardGroupsSectionBodyElement, vm.IsQuizDashboardGroupsSectionExpanded, ScheduleQuizVirtualizationRefreshAfterSectionAnimation);
                else if (e.PropertyName == nameof(MainViewModel.IsQuizDashboardUngroupedSectionExpanded))
                    AnimateSectionVisibility(QuizDashboardUngroupedSectionBodyElement, vm.IsQuizDashboardUngroupedSectionExpanded, ScheduleQuizVirtualizationRefreshAfterSectionAnimation);
                else if (e.PropertyName == nameof(MainViewModel.IsMindMapGroupsSectionExpanded))
                    AnimateSectionVisibility(MindMapGroupsSectionBodyElement, vm.IsMindMapGroupsSectionExpanded, ScheduleMindMapVirtualizationRefreshAfterSectionAnimation);
                else if (e.PropertyName == nameof(MainViewModel.IsMindMapDashboardGroupsSectionExpanded))
                    AnimateSectionVisibility(MindMapDashboardGroupsSectionBodyElement, vm.IsMindMapDashboardGroupsSectionExpanded, ScheduleMindMapVirtualizationRefreshAfterSectionAnimation);
                else if (e.PropertyName == nameof(MainViewModel.IsMindMapDashboardUngroupedSectionExpanded))
                    AnimateSectionVisibility(MindMapDashboardUngroupedSectionBodyElement, vm.IsMindMapDashboardUngroupedSectionExpanded, ScheduleMindMapVirtualizationRefreshAfterSectionAnimation);
                else if (e.PropertyName == nameof(MainViewModel.ActiveSearchQuery))
                    SyncClearSearchButton(animate: true);
                else if (e.PropertyName == nameof(MainViewModel.HasGroups))
                {
                    _notesHadGroups = vm.HasGroups;
                    EnsureExpandedSectionsNotClipped(vm);
                    QueueNotesLayoutRefresh();
                }
                else if (e.PropertyName == nameof(MainViewModel.HasNotes))
                {
                    if (vm.HasNotes)
                        QueueUngroupedNotesLayoutRefresh();
                    else
                    {
                        EnsureDashboardContentHostRestState(DashboardMotionScope.Notes);
                        ScheduleEmptyDashboardEnter(NotesEmptyDashboardPanelElement, DashboardNotes);
                    }
                }
                else if (e.PropertyName == nameof(MainViewModel.HasFlashcardSets))
                {
                    if (vm.HasFlashcardSets)
                        QueueDashboardLayoutRefresh();
                    else
                    {
                        EnsureDashboardContentHostRestState(DashboardMotionScope.Flashcards);
                        ScheduleEmptyDashboardEnter(FlashcardsEmptyDashboardPanelElement, DashboardFlashcards);
                    }
                }
                else if (e.PropertyName == nameof(MainViewModel.HasQuizzes))
                {
                    if (vm.HasQuizzes)
                        QueueDashboardLayoutRefresh();
                    else
                    {
                        EnsureDashboardContentHostRestState(DashboardMotionScope.Quizzes);
                        ScheduleEmptyDashboardEnter(QuizzesEmptyDashboardPanelElement, DashboardQuizzes);
                    }
                }
                else if (e.PropertyName == nameof(MainViewModel.HasMindMaps))
                {
                    if (vm.HasMindMaps)
                        QueueDashboardLayoutRefresh();
                    else
                    {
                        EnsureDashboardContentHostRestState(DashboardMotionScope.MindMaps);
                        ScheduleEmptyDashboardEnter(MindMapsEmptyDashboardPanelElement, DashboardMindMaps);
                    }
                }
                else if (e.PropertyName == nameof(MainViewModel.HasUngroupedNotes))
                {
                    QueueUngroupedNotesLayoutRefresh();
                }
                else if (e.PropertyName == nameof(MainViewModel.HasFlashcardSetGroups))
                {
                    _flashcardsHadGroups = vm.HasFlashcardSetGroups;
                    QueueDashboardLayoutRefresh();
                }
                else if (e.PropertyName == nameof(MainViewModel.HasQuizGroups))
                {
                    _quizzesHadGroups = vm.HasQuizGroups;
                    QueueDashboardLayoutRefresh();
                }
                else if (e.PropertyName == nameof(MainViewModel.HasMindMapGroups))
                {
                    _mindMapsHadGroups = vm.HasMindMapGroups;
                    QueueDashboardLayoutRefresh();
                }
                else if (e.PropertyName == nameof(MainViewModel.HasUngroupedFlashcardSets))
                {
                    QueueUngroupedFlashcardsLayoutRefresh();
                }
                else if (e.PropertyName == nameof(MainViewModel.HasUngroupedQuizzes))
                {
                    QueueUngroupedQuizzesLayoutRefresh();
                }
                else if (e.PropertyName == nameof(MainViewModel.HasUngroupedMindMaps))
                {
                    QueueUngroupedMindMapsLayoutRefresh();
                }
                else if (e.PropertyName == nameof(MainViewModel.SidebarWidth))
                {
                    AnimateSidebarWidth(vm.SidebarWidth);
                }
                else if (e.PropertyName == nameof(MainViewModel.IsMassSelectMode))
                {
                    AnimateMassSelectOverlay(vm.IsMassSelectMode);
                    MassSelectHighlightBehavior.ScheduleSyncAllDashboardGrids();
                }
                else if (e.PropertyName == nameof(MainViewModel.ActiveMassSelectedCount) && vm.IsMassSelectMode)
                {
                    MassSelectHighlightBehavior.ScheduleSyncAllDashboardGrids();
                }
                else if (e.PropertyName == nameof(MainViewModel.ActiveDashboard))
                {
                    SyncDashboardLoadingState(vm);
                    AnimateDashboardTransition(vm.ActiveDashboard);
                    CloseNotesDashboardChrome();
                }
                else if (e.PropertyName == nameof(MainViewModel.IsActiveDashboardReady))
                {
                    SyncDashboardLoadingState(vm);
                }

                if (IsLoadingDashboardActive
                    && !vm.IsActiveDashboardReady
                    && string.Equals(_loadingDashboard, vm.ActiveDashboard, StringComparison.Ordinal))
                    DashboardLoadingShell.SetLayout(BuildDashboardSkeletonLayout(vm));

            });
        }

        private void ApplySectionStateImmediately()
        {
            if (DataContext is not MainViewModel vm)
                return;

            SetSectionVisibilityImmediately(RecentSectionBodyElement, vm.IsRecentSectionExpanded);
            SetSectionVisibilityImmediately(FlashcardRecentSectionBodyElement, vm.IsRecentSectionExpanded);
            SetSectionVisibilityImmediately(QuizRecentSectionBodyElement, vm.IsRecentSectionExpanded);
            SetSectionVisibilityImmediately(MindMapRecentSectionBodyElement, vm.IsRecentSectionExpanded);
            SetSectionVisibilityImmediately(CalendarSectionBodyElement, vm.IsCalendarSectionExpanded);
            SetSectionVisibilityImmediately(FlashcardCalendarSectionBodyElement, vm.IsCalendarSectionExpanded);
            SetSectionVisibilityImmediately(QuizCalendarSectionBodyElement, vm.IsCalendarSectionExpanded);
            SetSectionVisibilityImmediately(MindMapCalendarSectionBodyElement, vm.IsCalendarSectionExpanded);
            SetSectionVisibilityImmediately(GroupsSectionBodyElement, vm.IsGroupsSectionExpanded);
            SetSectionVisibilityImmediately(UngroupedSectionBodyElement, vm.IsUngroupedSectionExpanded);
            SetSectionVisibilityImmediately(NotesDashboardSectionBodyElement, vm.IsNotesDashboardSectionExpanded);
            SetSectionVisibilityImmediately(FlashcardGroupsSectionBodyElement, vm.IsFlashcardGroupsSectionExpanded);
            SetSectionVisibilityImmediately(FlashcardDashboardGroupsSectionBodyElement, vm.IsFlashcardDashboardGroupsSectionExpanded);
            SetSectionVisibilityImmediately(FlashcardDashboardUngroupedSectionBodyElement, vm.IsFlashcardDashboardUngroupedSectionExpanded);
            SetSectionVisibilityImmediately(QuizGroupsSectionBodyElement, vm.IsQuizGroupsSectionExpanded);
            SetSectionVisibilityImmediately(QuizDashboardGroupsSectionBodyElement, vm.IsQuizDashboardGroupsSectionExpanded);
            SetSectionVisibilityImmediately(QuizDashboardUngroupedSectionBodyElement, vm.IsQuizDashboardUngroupedSectionExpanded);
            SetSectionVisibilityImmediately(MindMapGroupsSectionBodyElement, vm.IsMindMapGroupsSectionExpanded);
            SetSectionVisibilityImmediately(MindMapDashboardGroupsSectionBodyElement, vm.IsMindMapDashboardGroupsSectionExpanded);
            SetSectionVisibilityImmediately(MindMapDashboardUngroupedSectionBodyElement, vm.IsMindMapDashboardUngroupedSectionExpanded);
            SetSidebarWidthImmediately(vm.SidebarWidth);
            SetMassSelectOverlayStateImmediately(vm.IsMassSelectMode);
            EnsureExpandedSectionsNotClipped(vm);

        }

        private void SetMassSelectOverlayStateImmediately(bool isVisible)
        {
            var overlay = MassSelectOverlayToolbarElement;
            if (overlay is null)
                return;

            UiMotion.Stop(overlay);
            UiMotion.Clear(overlay, UIElement.OpacityProperty);
            var translate = UiMotion.EnsureTranslate(overlay);
            UiMotion.Clear(translate, TranslateTransform.YProperty);

            if (isVisible)
            {
                overlay.Visibility = Visibility.Visible;
                overlay.Opacity = 1;
                translate.Y = 0;
                return;
            }

            overlay.Visibility = Visibility.Collapsed;
            overlay.Opacity = 0;
            translate.Y = -UiMotion.PanelLiftY;
            SetMassSelectMoreActionsStateImmediately(false);
        }

        private void AnimateMassSelectOverlay(bool isVisible)
        {
            var overlay = MassSelectOverlayToolbarElement;
            if (overlay is null)
                return;

            UiMotion.SlideFadePanel(
                overlay,
                isVisible,
                durationMs: UiMotion.OpenMs,
                offsetY: UiMotion.PanelLiftY,
                onComplete: isVisible ? null : () => SetMassSelectMoreActionsStateImmediately(false));
        }

        private void SetMassSelectMoreActionsStateImmediately(bool isExpanded)
        {
            _isMassSelectMoreActionsExpanded = isExpanded;
            UpdateMassSelectMoreActionsToggleIcon();

            var panel = MassSelectMoreActionsPanelElement;
            if (panel is null)
                return;

            UiMotion.Stop(panel);
            UiMotion.Clear(panel, UIElement.OpacityProperty);
            var scale = UiMotion.EnsureScale(panel, UiMotion.CollapsedScale);
            UiMotion.Clear(scale, ScaleTransform.ScaleXProperty);
            UiMotion.Clear(scale, ScaleTransform.ScaleYProperty);

            if (MassSelectMoreActionsSeparatorElement is { } separator)
                separator.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;

            if (isExpanded)
            {
                panel.Visibility = Visibility.Visible;
                panel.Opacity = 1;
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                return;
            }

            panel.Visibility = Visibility.Collapsed;
            panel.Opacity = 0;
            scale.ScaleX = UiMotion.CollapsedScale;
            scale.ScaleY = UiMotion.CollapsedScale;
        }

        private void ToggleMassSelectMoreActionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel { IsMassSelectMode: true })
                return;

            AnimateMassSelectMoreActions(!_isMassSelectMoreActionsExpanded);
        }

        private void AnimateMassSelectMoreActions(bool expand)
        {
            _isMassSelectMoreActionsExpanded = expand;
            UpdateMassSelectMoreActionsToggleIcon();

            var panel = MassSelectMoreActionsPanelElement;
            if (panel is null)
                return;

            if (MassSelectMoreActionsSeparatorElement is { } separator)
                separator.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;

            UiMotion.ScaleFadePanel(panel, expand, durationMs: UiMotion.FastMs);
        }

        private void UpdateMassSelectMoreActionsToggleIcon()
        {
            if (MassSelectMoreActionsToggleButtonElement is not Button toggle)
                return;

            toggle.Content = _isMassSelectMoreActionsExpanded ? "‹" : "›";
        }

        private void SetSidebarWidthImmediately(double width)
        {
            if (SidebarColumnElement != null)
                SidebarColumnElement.Width = new GridLength(width, GridUnitType.Pixel);
        }

        private void AnimateSidebarWidth(double targetWidth)
        {
            if (SidebarColumnElement is null)
                return;

            var currentWidth = SidebarColumnElement.ActualWidth;
            if (currentWidth <= 0)
                currentWidth = SidebarColumnElement.Width.Value;

            if (Math.Abs(currentWidth - targetWidth) < 0.5)
            {
                SidebarColumnElement.Width = new GridLength(targetWidth, GridUnitType.Pixel);
                CompleteSidebarAnimation();
                return;
            }

            if (SidebarChromeElement is not null)
            {
                SuspendDashboardShadowsForSidebarAnimation();
                UiMotion.AnimateSidebarChrome(
                    SidebarChromeElement,
                    SidebarColumnElement,
                    currentWidth,
                    targetWidth,
                    UiMotion.SidebarResizeMs,
                    CompleteSidebarAnimation);
                return;
            }

            SidebarColumnElement.Width = new GridLength(targetWidth, GridUnitType.Pixel);
            CompleteSidebarAnimation();
        }

        private static void EnsureExpandedSectionsNotClipped(MainViewModel vm)
        {
            if (Application.Current?.MainWindow is not MainWindow window)
                return;

            if (vm.IsRecentSectionExpanded && window.RecentSectionBodyElement is FrameworkElement recent)
                AllowExpandedSectionContent(recent);
            if (vm.IsRecentSectionExpanded && window.FlashcardRecentSectionBodyElement is FrameworkElement fcRecent)
                AllowExpandedSectionContent(fcRecent);
            if (vm.IsRecentSectionExpanded && window.QuizRecentSectionBodyElement is FrameworkElement qzRecent)
                AllowExpandedSectionContent(qzRecent);
            if (vm.IsRecentSectionExpanded && window.MindMapRecentSectionBodyElement is FrameworkElement mmRecent)
                AllowExpandedSectionContent(mmRecent);

            if (vm.IsCalendarSectionExpanded && window.CalendarSectionBodyElement is FrameworkElement calendar)
                AllowExpandedSectionContent(calendar);
            if (vm.IsCalendarSectionExpanded && window.FlashcardCalendarSectionBodyElement is FrameworkElement fcCalendar)
                AllowExpandedSectionContent(fcCalendar);
            if (vm.IsCalendarSectionExpanded && window.QuizCalendarSectionBodyElement is FrameworkElement qzCalendar)
                AllowExpandedSectionContent(qzCalendar);
            if (vm.IsCalendarSectionExpanded && window.MindMapCalendarSectionBodyElement is FrameworkElement mmCalendar)
                AllowExpandedSectionContent(mmCalendar);

            if (vm.IsGroupsSectionExpanded && window.GroupsSectionBodyElement is FrameworkElement groups)
                AllowExpandedSectionContent(groups);

            if (vm.IsUngroupedSectionExpanded && window.UngroupedSectionBodyElement is FrameworkElement ungrouped)
                AllowExpandedSectionContent(ungrouped);
            if (vm.IsNotesDashboardSectionExpanded && window.NotesDashboardSectionBodyElement is FrameworkElement notesDashboard)
                AllowExpandedSectionContent(notesDashboard);
            if (vm.IsFlashcardDashboardGroupsSectionExpanded && window.FlashcardDashboardGroupsSectionBodyElement is FrameworkElement flashcardGroups)
                AllowExpandedSectionContent(flashcardGroups);
            if (vm.IsFlashcardDashboardUngroupedSectionExpanded && window.FlashcardDashboardUngroupedSectionBodyElement is FrameworkElement flashcardUngrouped)
                AllowExpandedSectionContent(flashcardUngrouped);
            if (vm.IsQuizDashboardGroupsSectionExpanded && window.QuizDashboardGroupsSectionBodyElement is FrameworkElement quizGroups)
                AllowExpandedSectionContent(quizGroups);
            if (vm.IsQuizDashboardUngroupedSectionExpanded && window.QuizDashboardUngroupedSectionBodyElement is FrameworkElement quizUngrouped)
                AllowExpandedSectionContent(quizUngrouped);
            if (vm.IsMindMapDashboardGroupsSectionExpanded && window.MindMapDashboardGroupsSectionBodyElement is FrameworkElement mindMapGroups)
                AllowExpandedSectionContent(mindMapGroups);
            if (vm.IsMindMapDashboardUngroupedSectionExpanded && window.MindMapDashboardUngroupedSectionBodyElement is FrameworkElement mindMapUngrouped)
                AllowExpandedSectionContent(mindMapUngrouped);
        }

        private static void AllowExpandedSectionContent(FrameworkElement section)
        {
            UiMotion.Clear(section, FrameworkElement.MaxHeightProperty);
            section.ClearValue(FrameworkElement.HeightProperty);
            section.Clip = null;
            section.MaxHeight = double.PositiveInfinity;
            section.ClipToBounds = false;
        }

        private static void SetSectionVisibilityImmediately(FrameworkElement? section, bool isExpanded)
        {
            if (section is null)
                return;

            section.RenderTransformOrigin = new Point(0.5, 0);
            var translate = UiMotion.EnsureTranslate(section);

            if (isExpanded)
            {
                section.Visibility = Visibility.Visible;
                section.Opacity = 1;
                AllowExpandedSectionContent(section);
                translate.Y = 0;
                return;
            }

            section.ClipToBounds = true;
            section.Visibility = Visibility.Collapsed;
            section.Opacity = 0;
            section.MaxHeight = 0;
            section.ClearValue(FrameworkElement.HeightProperty);
            section.Clip = null;
            translate.Y = -8;
        }

        private void AnimateSectionVisibility(FrameworkElement? section, bool isExpanded, Action? onComplete = null)
        {
            if (section is null)
            {
                onComplete?.Invoke();
                return;
            }

            UiMotion.AnimateClipReveal(section, isExpanded, UiMotion.SectionMs, onComplete: onComplete);
        }

        private void AnimateSharedDashboardSectionVisibility(
            MainViewModel vm,
            FrameworkElement? activeSection,
            FrameworkElement? notesSection,
            FrameworkElement? flashcardsSection,
            FrameworkElement? quizzesSection,
            FrameworkElement? mindMapsSection,
            bool isExpanded,
            Action? onComplete = null)
        {
            foreach (var section in new[] { notesSection, flashcardsSection, quizzesSection, mindMapsSection })
            {
                if (section is null || ReferenceEquals(section, activeSection))
                    continue;

                SetSectionVisibilityImmediately(section, isExpanded);
            }

            AnimateSectionVisibility(activeSection, isExpanded, onComplete);
        }

        private FrameworkElement? ResolveActiveRecentSectionBody(MainViewModel vm)
            => ResolveActiveDashboardSectionBody(
                vm,
                RecentSectionBodyElement,
                FlashcardRecentSectionBodyElement,
                QuizRecentSectionBodyElement,
                MindMapRecentSectionBodyElement);

        private FrameworkElement? ResolveActiveCalendarSectionBody(MainViewModel vm)
            => ResolveActiveDashboardSectionBody(
                vm,
                CalendarSectionBodyElement,
                FlashcardCalendarSectionBodyElement,
                QuizCalendarSectionBodyElement,
                MindMapCalendarSectionBodyElement);

        private static FrameworkElement? ResolveActiveDashboardSectionBody(
            MainViewModel vm,
            FrameworkElement? notesSection,
            FrameworkElement? flashcardsSection,
            FrameworkElement? quizzesSection,
            FrameworkElement? mindMapsSection)
            => vm.ActiveDashboard switch
            {
                DashboardFlashcards => flashcardsSection ?? notesSection,
                DashboardQuizzes => quizzesSection ?? notesSection,
                DashboardMindMaps => mindMapsSection ?? notesSection,
                _ => notesSection
            };

        private void ScheduleNotesLayoutRefreshAfterSectionAnimation()
            => ScheduleVirtualizationRefreshAfterSectionAnimation(QueueNotesLayoutRefresh);

        private void ScheduleAllDashboardVirtualizationRefreshAfterSectionAnimation()
            => ScheduleVirtualizationRefreshAfterSectionAnimation(() => RefreshDashboardVirtualizationHosts());

        private void ScheduleFlashcardVirtualizationRefreshAfterSectionAnimation()
            => ScheduleVirtualizationRefreshAfterSectionAnimation(() => RefreshDashboardVirtualizationHosts(DashboardMotionScope.Flashcards));

        private void ScheduleQuizVirtualizationRefreshAfterSectionAnimation()
            => ScheduleVirtualizationRefreshAfterSectionAnimation(() => RefreshDashboardVirtualizationHosts(DashboardMotionScope.Quizzes));

        private void ScheduleMindMapVirtualizationRefreshAfterSectionAnimation()
            => ScheduleVirtualizationRefreshAfterSectionAnimation(() => RefreshDashboardVirtualizationHosts(DashboardMotionScope.MindMaps));

        private void ScheduleVirtualizationRefreshAfterSectionAnimation(Action refresh)
            => ScheduleMotionRefreshAfterAnimation(refresh, UiMotion.SectionMs);

        private void ScheduleReflowVirtualizationRefresh(DashboardMotionScope scope)
            => ScheduleMotionRefreshAfterAnimation(() => RefreshDashboardVirtualizationHosts(scope), UiMotion.ReflowMs);

        private static void ScheduleMotionRefreshAfterAnimation(Action refresh, int durationMs)
        {
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(durationMs + 16)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                refresh();
            };
            timer.Start();
        }

        private void SwapNotesGroupsAndUngroupedSections(bool groupsFirst)
        {
            if (DataContext is not MainViewModel vm || vm.IsGroupsFirst == groupsFirst)
                return;

            AnimateDashboardSectionPairSwap(
                GroupsSectionContainerElement,
                UngroupedSectionContainerElement,
                () => vm.IsGroupsFirst = groupsFirst,
                DashboardMotionScope.Notes,
                () => ScheduleReflowVirtualizationRefresh(DashboardMotionScope.Notes));
        }

        private void SwapNotesCalendarAndDashboard(bool calendarFirst)
        {
            if (DataContext is not MainViewModel vm || vm.IsCalendarFirst == calendarFirst)
                return;

            AnimateDashboardSectionPairSwap(
                CalendarSectionContainerElement,
                NotesDashboardContentHostElement,
                () => vm.IsCalendarFirst = calendarFirst,
                DashboardMotionScope.Notes,
                () => ScheduleReflowVirtualizationRefresh(DashboardMotionScope.Notes));
        }

        private void SwapFlashcardDashboardGroupsAndUngrouped(bool groupsFirst)
        {
            if (DataContext is not MainViewModel vm || vm.IsFlashcardDashboardGroupsFirst == groupsFirst)
                return;

            AnimateDashboardSectionPairSwap(
                FlashcardDashboardGroupsSectionContainerElement,
                FlashcardDashboardUngroupedSectionContainerElement,
                () => vm.IsFlashcardDashboardGroupsFirst = groupsFirst,
                DashboardMotionScope.Flashcards,
                () => ScheduleReflowVirtualizationRefresh(DashboardMotionScope.Flashcards));
        }

        private void SwapFlashcardCalendarAndDashboard(bool calendarFirst)
        {
            if (DataContext is not MainViewModel vm || vm.IsCalendarFirst == calendarFirst)
                return;

            AnimateDashboardSectionPairSwap(
                FlashcardCalendarSectionContainerElement,
                FlashcardsDashboardContentHostElement,
                () => vm.IsCalendarFirst = calendarFirst,
                DashboardMotionScope.Flashcards,
                () => ScheduleReflowVirtualizationRefresh(DashboardMotionScope.Flashcards));
        }

        private void SwapQuizDashboardGroupsAndUngrouped(bool groupsFirst)
        {
            if (DataContext is not MainViewModel vm || vm.IsQuizDashboardGroupsFirst == groupsFirst)
                return;

            AnimateDashboardSectionPairSwap(
                QuizDashboardGroupsSectionContainerElement,
                QuizDashboardUngroupedSectionContainerElement,
                () => vm.IsQuizDashboardGroupsFirst = groupsFirst,
                DashboardMotionScope.Quizzes,
                () => ScheduleReflowVirtualizationRefresh(DashboardMotionScope.Quizzes));
        }

        private void SwapQuizCalendarAndDashboard(bool calendarFirst)
        {
            if (DataContext is not MainViewModel vm || vm.IsCalendarFirst == calendarFirst)
                return;

            AnimateDashboardSectionPairSwap(
                QuizCalendarSectionContainerElement,
                QuizzesDashboardContentHostElement,
                () => vm.IsCalendarFirst = calendarFirst,
                DashboardMotionScope.Quizzes,
                () => ScheduleReflowVirtualizationRefresh(DashboardMotionScope.Quizzes));
        }

        private void SwapMindMapDashboardGroupsAndUngrouped(bool groupsFirst)
        {
            if (DataContext is not MainViewModel vm || vm.IsMindMapDashboardGroupsFirst == groupsFirst)
                return;

            AnimateDashboardSectionPairSwap(
                MindMapDashboardGroupsSectionContainerElement,
                MindMapDashboardUngroupedSectionContainerElement,
                () => vm.IsMindMapDashboardGroupsFirst = groupsFirst,
                DashboardMotionScope.MindMaps,
                () => ScheduleReflowVirtualizationRefresh(DashboardMotionScope.MindMaps));
        }

        private void SwapMindMapCalendarAndDashboard(bool calendarFirst)
        {
            if (DataContext is not MainViewModel vm || vm.IsCalendarFirst == calendarFirst)
                return;

            AnimateDashboardSectionPairSwap(
                MindMapCalendarSectionContainerElement,
                MindMapsDashboardContentHostElement,
                () => vm.IsCalendarFirst = calendarFirst,
                DashboardMotionScope.MindMaps,
                () => ScheduleReflowVirtualizationRefresh(DashboardMotionScope.MindMaps));
        }

        private void AnimateDashboardSectionPairSwap(
            FrameworkElement? firstSection,
            FrameworkElement? secondSection,
            Action applySwap,
            DashboardMotionScope motionScope,
            Action? afterSwap = null)
        {
            if (ShouldApplyReorderMotionInstantly())
            {
                applySwap();
                afterSwap?.Invoke();
                return;
            }

            if (firstSection is null || secondSection is null)
            {
                applySwap();
                afterSwap?.Invoke();
                return;
            }

            if (firstSection.Visibility != Visibility.Visible || secondSection.Visibility != Visibility.Visible)
            {
                applySwap();
                afterSwap?.Invoke();
                return;
            }

            var parent = VisualTreeHelper.GetParent(firstSection) as UIElement;
            if (parent is null || !ReferenceEquals(VisualTreeHelper.GetParent(secondSection), parent))
            {
                applySwap();
                afterSwap?.Invoke();
                return;
            }

            var engine = EnsureDashboardMotionEngine(motionScope);
            engine.BeginLayoutAnimation();

            parent.UpdateLayout();
            var firstOldY = MeasureTopOffset(firstSection, parent);
            var secondOldY = MeasureTopOffset(secondSection, parent);

            applySwap();

            Dispatcher.BeginInvoke(() =>
            {
                RunFlipPairAnimation(
                    firstSection,
                    secondSection,
                    parent,
                    firstOldY,
                    secondOldY,
                    engine,
                    afterSwap);
            }, DispatcherPriority.Render);
        }

        private bool ShouldApplyReorderMotionInstantly()
            => _dashboardTransitionActive || UiMotion.PrefersReducedMotion;

        private static double MeasureTopOffset(FrameworkElement element, UIElement relativeTo)
        {
            try
            {
                return element.TransformToAncestor(relativeTo).Transform(new Point(0, 0)).Y;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        }

        private void RunFlipPairAnimation(
            FrameworkElement first,
            FrameworkElement second,
            UIElement parent,
            double firstOldY,
            double secondOldY,
            DashboardMotionEngine engine,
            Action? onAllComplete = null)
        {
            parent.UpdateLayout();
            var firstDelta = firstOldY - MeasureTopOffset(first, parent);
            var secondDelta = secondOldY - MeasureTopOffset(second, parent);

            var animations = 0;
            if (Math.Abs(firstDelta) >= 0.5)
                animations++;
            if (Math.Abs(secondDelta) >= 0.5)
                animations++;

            if (animations == 0)
            {
                engine.EndLayoutAnimation();
                onAllComplete?.Invoke();
                return;
            }

            var remaining = animations;
            void OnOneComplete()
            {
                remaining--;
                if (remaining > 0)
                    return;

                engine.EndLayoutAnimation();
                onAllComplete?.Invoke();
            }

            AnimateSectionFlipOffset(first, firstDelta, OnOneComplete);
            AnimateSectionFlipOffset(second, secondDelta, OnOneComplete);
        }

        private static void AnimateSectionFlipOffset(FrameworkElement element, double startOffset, Action? onComplete = null)
        {
            if (Math.Abs(startOffset) < 0.5)
            {
                onComplete?.Invoke();
                return;
            }

            element.RenderTransformOrigin = new Point(0.5, 0);
            var translate = UiMotion.EnsureTranslate(element);
            UiMotion.Stop(element);
            UiMotion.Clear(element, UIElement.OpacityProperty);
            translate.Y = startOffset;

            UiMotion.RunComposited(
                element,
                new[]
                {
                    UiMotion.Track(UiMotion.ReflowMs, UiMotion.OutQuad, startOffset, 0, value => translate.Y = value)
                },
                () =>
                {
                    translate.Y = 0;
                    onComplete?.Invoke();
                });
        }

        internal void DashboardCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || sender is not System.Windows.Controls.Calendar calendar)
                return;

            if (calendar.SelectedDate.HasValue)
                vm.CalendarSelectedDate = calendar.SelectedDate.Value.Date;
        }



        internal void NotesScrollViewer_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (!IsNoteDragInProgress || sender is not ScrollViewer scrollViewer)
                return;

            if (e.Data.GetData(typeof(NoteCardViewModel)) is not NoteCardViewModel draggedNote)
                return;

            var cursorPosition = e.GetPosition(scrollViewer);

            if (cursorPosition.Y < DragScrollEdgeThreshold)
            {
                scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - DragScrollStep));
            }
            else if (cursorPosition.Y > scrollViewer.ViewportHeight - DragScrollEdgeThreshold)
            {
                scrollViewer.ScrollToVerticalOffset(Math.Min(scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset + DragScrollStep));
            }
        }

        internal void NotesScrollViewer_PreviewDragLeave(object sender, DragEventArgs e)
        {
            if (DashboardDragGhost.IsNoteDropGlowSyncActive)
                NoteCardControl.ClearDropGlowOwner();
        }

        internal void SyncNoteDropGlowDuringDrag(
            NoteCardViewModel? draggedNote,
            NoteCardControl? dragSource)
        {
            if (draggedNote is null)
                return;

            if (NotesScrollViewer is null)
            {
                NoteCardControl.ClearDropGlowOwner();
                return;
            }

            if (!DashboardDragGhost.TryGetCursorScreenPoint(out var screenPoint))
            {
                NoteCardControl.ClearDropGlowOwner();
                return;
            }

            NoteCardControl.SyncDropGlowAtScreenPoint(
                NotesScrollViewer,
                screenPoint,
                draggedNote,
                dragSource);
        }

        internal void SyncDashboardListingDropVisualDuringDrag(
            object? draggedListing,
            FrameworkElement? dragSource)
        {
            if (draggedListing is null)
                return;

            if (DataContext is MainViewModel { IsMassSelectMode: true })
            {
                ClearDashboardListingDropVisuals();
                return;
            }

            var scrollViewer = ResolveActiveDashboardScrollViewer();
            if (scrollViewer is null)
            {
                ClearDashboardListingDropVisuals();
                return;
            }

            if (!DashboardDragGhost.TryGetCursorScreenPoint(out var screenPoint))
            {
                ClearDashboardListingDropVisuals();
                return;
            }

            FrameworkElement? dropTarget = null;
            ForEachDashboardListing(scrollViewer, candidate =>
            {
                if (ReferenceEquals(candidate, dragSource)
                    || candidate.Tag is not { } targetListing
                    || !CanDropListingOnTarget(draggedListing, targetListing)
                    || !IsScreenPointWithinElement(candidate, screenPoint))
                {
                    return;
                }

                dropTarget = candidate;
            });

            if (dropTarget is null)
                ClearDashboardListingDropVisuals();
            else
                SetDashboardListingDropVisual(dropTarget, true);
        }

        private ScrollViewer? ResolveActiveDashboardScrollViewer()
        {
            if (DataContext is not MainViewModel vm)
                return null;

            return vm.ActiveDashboard switch
            {
                DashboardNotes => NotesScrollViewer,
                DashboardFlashcards => FlashcardsScrollViewerElement,
                DashboardQuizzes => QuizzesScrollViewerElement,
                DashboardMindMaps => MindMapsScrollViewerElement,
                _ => null
            };
        }

        private void ClearDashboardListingDropVisuals()
        {
            ClearListingDropGlow();
        }

        private void ClearAllDropZoneBorderVisuals()
        {
            foreach (var border in _dropZoneOriginalBorderBrushes.Keys.ToList())
                RestoreDropZoneBorderVisual(border);
        }

        private static void ForEachDashboardListing(DependencyObject root, Action<FrameworkElement> visit)
        {
            if (root is FrameworkElement { Tag: FlashcardSetViewModel or MindMapViewModel or QuizViewModel } element)
                visit(element);

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
                ForEachDashboardListing(VisualTreeHelper.GetChild(root, i), visit);
        }

        private static bool CanDropListingOnTarget(object draggedListing, object targetListing)
        {
            return draggedListing switch
            {
                FlashcardSetViewModel dragged when targetListing is FlashcardSetViewModel target
                    => !ReferenceEquals(dragged, target),
                MindMapViewModel dragged when targetListing is MindMapViewModel target
                    => !ReferenceEquals(dragged, target),
                QuizViewModel dragged when targetListing is QuizViewModel target
                    => !ReferenceEquals(dragged, target),
                _ => false
            };
        }

        private static bool IsScreenPointWithinElement(FrameworkElement element, Point screenPoint)
        {
            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return false;

            if (PresentationSource.FromVisual(element) is null)
                return false;

            try
            {
                var local = element.PointFromScreen(screenPoint);
                var margin = element.Margin;
                return local.X >= -margin.Left
                    && local.Y >= -margin.Top
                    && local.X <= element.ActualWidth + margin.Right
                    && local.Y <= element.ActualHeight + margin.Bottom;
            }
            catch
            {
                return false;
            }
        }

        internal void DashboardScrollViewer_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer || !HasDashboardListingDragData(e))
                return;

            StartDashboardDragAutoScroll(scrollViewer, e);
        }

        internal void DashboardScrollViewer_PreviewDragLeave(object sender, DragEventArgs e)
        {
            if (ReferenceEquals(sender, _dashboardDragScrollViewer))
                StopDashboardDragAutoScroll();
        }

        internal void DashboardScrollViewer_Drop(object sender, DragEventArgs e)
        {
            if (ReferenceEquals(sender, _dashboardDragScrollViewer))
                StopDashboardDragAutoScroll();
        }

        private void StartDashboardDragAutoScroll(ScrollViewer scrollViewer, DragEventArgs e)
        {
            _dashboardDragScrollViewer = scrollViewer;
            _dashboardDragScrollPosition = e.GetPosition(scrollViewer);

            _dashboardDragScrollTimer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(16),
                DispatcherPriority.Render,
                (_, _) => TickDashboardDragAutoScroll(),
                Dispatcher);

            if (!_dashboardDragScrollTimer.IsEnabled)
                _dashboardDragScrollTimer.Start();
        }

        private void TickDashboardDragAutoScroll()
        {
            if (_dashboardDragScrollViewer is null)
            {
                StopDashboardDragAutoScroll();
                return;
            }

            AutoScrollDuringDrag(_dashboardDragScrollViewer, _dashboardDragScrollPosition);
        }

        private void StopDashboardDragAutoScroll()
        {
            _dashboardDragScrollTimer?.Stop();
            _dashboardDragScrollViewer = null;
        }

        private static void AutoScrollDuringDrag(ScrollViewer scrollViewer, Point cursorPosition)
        {
            if (cursorPosition.Y < DashboardDragScrollEdgeThreshold)
            {
                var step = GetDashboardDragScrollStep(DashboardDragScrollEdgeThreshold - cursorPosition.Y);
                scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - step));
            }
            else if (cursorPosition.Y > scrollViewer.ViewportHeight - DashboardDragScrollEdgeThreshold)
            {
                var step = GetDashboardDragScrollStep(cursorPosition.Y - (scrollViewer.ViewportHeight - DashboardDragScrollEdgeThreshold));
                scrollViewer.ScrollToVerticalOffset(Math.Min(scrollViewer.ScrollableHeight, scrollViewer.VerticalOffset + step));
            }
        }

        private static double GetDashboardDragScrollStep(double edgeDistance)
        {
            var factor = Math.Clamp(edgeDistance / DashboardDragScrollEdgeThreshold, 0, 1);
            return DashboardDragScrollMinStep + ((DashboardDragScrollMaxStep - DashboardDragScrollMinStep) * factor);
        }

        private static bool HasDashboardListingDragData(DragEventArgs e)
        {
            return e.Data.GetDataPresent(typeof(FlashcardSetViewModel))
                   || e.Data.GetDataPresent(typeof(MindMapViewModel))
                   || e.Data.GetDataPresent(typeof(QuizViewModel));
        }

        internal void NotesScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!IsNoteDragInProgress || sender is not ScrollViewer scrollViewer)
                return;

            var nextOffset = Math.Clamp(scrollViewer.VerticalOffset - (e.Delta / 3d), 0, scrollViewer.ScrollableHeight);
            scrollViewer.ScrollToVerticalOffset(nextOffset);
            e.Handled = true;
        }

        private void ConfigurePopupAnimations()
        {
            UiMotion.ConfigurePopupsForWindow(this);
        }

        private void AnimateDashboardTransition(string newDashboard)
        {
            if (!IsLoaded || string.IsNullOrWhiteSpace(newDashboard))
                return;

            if (IsLoadingDashboardActive)
            {
                EnterDashboardLoading(newDashboard);
                return;
            }

            if (string.IsNullOrEmpty(_lastActiveDashboard)
                || string.Equals(_lastActiveDashboard, newDashboard, StringComparison.Ordinal))
            {
                SetDashboardViewImmediate(newDashboard);
                _lastActiveDashboard = newDashboard;
                Dispatcher.BeginInvoke(EnsureAllListingDashboardsInteractive, DispatcherPriority.Loaded);
                return;
            }

            var previousDashboard = _lastActiveDashboard;
            var outgoing = ResolveDashboardView(previousDashboard);
            var incoming = ResolveDashboardView(newDashboard);
            if (outgoing is null || incoming is null)
            {
                _dashboardTransitionActive = false;
                SetDashboardViewImmediate(newDashboard);
                _lastActiveDashboard = newDashboard;
                return;
            }

            if (ResolveDashboardMotionScope(previousDashboard) is { } previousScope)
            {
                EnsureDashboardContentHostRestState(previousScope);
                EnsureRecentSectionRestState(previousScope);
            }

            _lastActiveDashboard = newDashboard;
            _dashboardTransitionActive = true;

            DashboardShadowSuspender.TrySuspend(DashboardTransitionShadowScope, outgoing);
            DashboardShadowSuspender.TrySuspend(DashboardTransitionShadowScope, incoming);

            incoming.Visibility = Visibility.Visible;
            incoming.Opacity = 0;
            incoming.IsHitTestVisible = false;
            outgoing.IsHitTestVisible = false;

            Dispatcher.BeginInvoke(() =>
            {
                incoming.UpdateLayout();
                outgoing.UpdateLayout();

                if (_observedViewModel is { IsRecentSectionVisible: true, IsRecentSectionExpanded: true })
                    RecentStripEnterMotionBehavior.TryPrepEnterHold(incoming);

                UiMotion.CrossFadeDashboardPanelsComposited(
                    outgoing,
                    incoming,
                    () =>
                    {
                        if (!string.Equals(_lastActiveDashboard, newDashboard, StringComparison.Ordinal)
                            || !string.Equals(_observedViewModel?.ActiveDashboard, newDashboard, StringComparison.Ordinal))
                        {
                            SetDashboardViewImmediate(_lastActiveDashboard);
                            outgoing.IsHitTestVisible = true;
                            incoming.IsHitTestVisible = true;
                            DashboardShadowSuspender.TryResume(DashboardTransitionShadowScope);
                            return;
                        }

                        incoming.IsHitTestVisible = true;
                        outgoing.IsHitTestVisible = true;
                        _dashboardTransitionActive = false;
                        DashboardShadowSuspender.TryResume(DashboardTransitionShadowScope);

                        if (ResolveDashboardMotionScope(newDashboard) is { } incomingScope)
                            PlayDashboardSwitchReveal(incomingScope);
                        else
                            QueueDashboardLayoutRefresh();

                        Dispatcher.BeginInvoke(EnsureActiveListingDashboardInteractive, DispatcherPriority.Loaded);
                    });
            }, DispatcherPriority.Render);
        }

        private void SetDashboardViewImmediate(string dashboard)
        {
            foreach (var view in AllDashboardViews())
            {
                if (view is null)
                    continue;

                var isActive = ReferenceEquals(view, ResolveDashboardView(dashboard));
                view.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
                view.Opacity = 1;
            }
        }

        private FrameworkElement? ResolveDashboardView(string? dashboard)
            => dashboard switch
            {
                DashboardNotes => NotesDashboardViewElement,
                DashboardFlashcards => FlashcardsDashboardViewElement,
                DashboardQuizzes => QuizzesDashboardViewElement,
                DashboardMindMaps => MindMapsDashboardViewElement,
                _ => null
            };

        private IEnumerable<FrameworkElement?> AllDashboardViews()
        {
            yield return NotesDashboardViewElement;
            yield return FlashcardsDashboardViewElement;
            yield return QuizzesDashboardViewElement;
            yield return MindMapsDashboardViewElement;
        }

        private void QueueActiveDashboardEmptyEnterIfNeeded(MainViewModel vm)
        {
            FrameworkElement? panel = vm.ActiveDashboard switch
            {
                DashboardNotes when vm.ShowNotesEmptyDashboard => NotesEmptyDashboardPanelElement,
                DashboardFlashcards when vm.ShowFlashcardsEmptyDashboard => FlashcardsEmptyDashboardPanelElement,
                DashboardQuizzes when vm.ShowQuizzesEmptyDashboard => QuizzesEmptyDashboardPanelElement,
                DashboardMindMaps when vm.ShowMindMapsEmptyDashboard => MindMapsEmptyDashboardPanelElement,
                _ => null
            };

            QueueEmptyDashboardEnterAnimation(panel);
        }

        private void AttachDashboardPresenceHandlers()
        {
            if (_dashboardPresenceHandlersAttached)
                return;

            _dashboardPresenceHandlersAttached = true;

            AttachEmptyDashboardPresenceHandler(NotesEmptyDashboardPanelElement, DashboardNotes, () => !(_observedViewModel?.HasNotes ?? true));
            AttachEmptyDashboardPresenceHandler(FlashcardsEmptyDashboardPanelElement, DashboardFlashcards, () => !(_observedViewModel?.HasFlashcardSets ?? true));
            AttachEmptyDashboardPresenceHandler(QuizzesEmptyDashboardPanelElement, DashboardQuizzes, () => !(_observedViewModel?.HasQuizzes ?? true));
            AttachEmptyDashboardPresenceHandler(MindMapsEmptyDashboardPanelElement, DashboardMindMaps, () => !(_observedViewModel?.HasMindMaps ?? true));

        }

        private void AttachEmptyDashboardPresenceHandler(
            FrameworkElement? panel,
            string dashboard,
            Func<bool> isStillEmpty)
        {
            if (panel is null)
                return;

            panel.IsVisibleChanged += (_, _) =>
            {
                if (panel.Visibility != Visibility.Visible || !IsLoaded)
                    return;

                ScheduleEmptyDashboardEnter(panel, dashboard, isStillEmpty);
            };
        }

        private void ScheduleEmptyDashboardEnter(
            FrameworkElement? panel,
            string dashboard,
            Func<bool>? isStillEmpty = null)
        {
            if (panel is null || !IsLoaded)
                return;

            if (_dashboardTransitionActive)
            {
                Dispatcher.BeginInvoke(
                    () => ScheduleEmptyDashboardEnter(panel, dashboard, isStillEmpty),
                    DispatcherPriority.Loaded);
                return;
            }

            if (!_emptyDashboardEnterScheduled.Add(panel))
                return;

            Dispatcher.BeginInvoke(() =>
            {
                _emptyDashboardEnterScheduled.Remove(panel);

                if (panel.Visibility != Visibility.Visible)
                    return;

                if (DataContext is not MainViewModel vm)
                    return;

                if (!string.Equals(vm.ActiveDashboard, dashboard, StringComparison.Ordinal))
                    return;

                if (isStillEmpty is not null && !isStillEmpty())
                    return;

                if (ResolveDashboardView(dashboard) is { Opacity: < 0.98 })
                    return;

                UiMotion.Stop(panel);
                if (UiMotion.ShouldSkipDashboardRevealMotion())
                {
                    panel.Opacity = 1;
                    if (panel is Panel container)
                    {
                        foreach (var child in container.Children.OfType<FrameworkElement>())
                            UiMotion.EnsureContentRevealRestState(child);
                    }

                    return;
                }

                UiMotion.AnimateEmptyDashboardEnter(panel);
            }, DispatcherPriority.Render);
        }

        private void QueueEmptyDashboardEnterAnimation(FrameworkElement? panel)
        {
            if (panel is null)
                return;

            var dashboard = ReferenceEquals(panel, NotesEmptyDashboardPanelElement) ? DashboardNotes
                : ReferenceEquals(panel, FlashcardsEmptyDashboardPanelElement) ? DashboardFlashcards
                : ReferenceEquals(panel, QuizzesEmptyDashboardPanelElement) ? DashboardQuizzes
                : ReferenceEquals(panel, MindMapsEmptyDashboardPanelElement) ? DashboardMindMaps
                : null;

            if (dashboard is null)
                return;

            Func<bool>? isStillEmpty = dashboard switch
            {
                DashboardNotes => () => !(_observedViewModel?.HasNotes ?? true),
                DashboardFlashcards => () => !(_observedViewModel?.HasFlashcardSets ?? true),
                DashboardQuizzes => () => !(_observedViewModel?.HasQuizzes ?? true),
                DashboardMindMaps => () => !(_observedViewModel?.HasMindMaps ?? true),
                _ => null
            };

            ScheduleEmptyDashboardEnter(panel, dashboard, isStillEmpty);
        }

        private void OpenFromFileMenuButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = LocalizationService.GetString("OpenFileDialogFilter");
            if (dlg.ShowDialog() != true)
                return;

            var path = dlg.FileName;
            try
            {
                var vm = this.DataContext as MainViewModel;
                if (vm == null) return;

                if (NoteFileService.IsNotePackagePath(path))
                {
                    OpenNoteEditor(vm.AddNoteFromDocument(NoteFileService.LoadNotePackage(path)));
                    return;
                }

                var content = NoteFileService.LoadEditorContentFromFile(path);

                // Try find existing note with identical content; otherwise create new
                NoteCardViewModel? existing = null;
                foreach (var n in vm.Notes)
                {
                    if (n.Document.Content == content)
                    {
                        existing = n; break;
                    }
                }

                NoteCardViewModel target;
                if (existing != null)
                {
                    target = existing;
                }
                else
                {
                    var settings = AppSettingsService.Load();
                    var preferredFontFamily = "Calibri";
                    var preferredFontSize = settings.PreferredFontSize > 0
                        ? settings.PreferredFontSize
                        : 14;

                    var doc = new Scriblucent.Models.NoteDocument
                    {
                        Title = System.IO.Path.GetFileNameWithoutExtension(path),
                        Content = content,
                        FontFamily = preferredFontFamily,
                        FontSize = preferredFontSize
                    };
                    target = vm.AddNoteFromDocument(doc);
                }

                OpenNoteEditor(target);
            }
            catch
            {
                ModernMessageBox.Show(LocalizationService.GetString("FailedToOpenFile"), LocalizationService.GetString("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SidebarSearchButton_Click(object sender, RoutedEventArgs e)
        {
            TopSearchButton_Click(sender, e);
        }

        private void SidebarSortButton_Click(object sender, RoutedEventArgs e)
        {
            SortNotesButton_Click(sender, e);
        }

        private void SidebarTagsFilterButton_Click(object sender, RoutedEventArgs e)
        {
            TagsFilterButton_Click(sender, e);
        }

        private void SidebarSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsMenuButton_Click(sender, e);
        }

        private void SidebarAddButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            if (vm.IsFlashcardsView)
            {
                OpenFlashcardSetEditor(vm, null);
                return;
            }

            if (vm.IsQuizzesView)
            {
                CreateQuizButton_Click(sender, e);
                return;
            }

            if (vm.IsMindMapsView)
            {
                CreateMindMapButton_Click(sender, e);
                return;
            }

            vm.AddNoteCommand.Execute(null);
        }

        private void CloseNotesDashboardChrome()
        {
            CollapseTopSearchPanel();

            UiMotion.ClosePopup(SortNotesPopupElement);
            UiMotion.ClosePopup(DashboardSectionsPopupElement);
            UiMotion.ClosePopup(TagsFilterPopup);
        }

        private void SortNotesButton_Click(object sender, RoutedEventArgs e)
        {
            CollapseTopSearchPanel();
            UiMotion.ClosePopup(TagsFilterPopup);
            UiMotion.ClosePopup(DashboardSectionsPopupElement);
            UiMotion.TogglePopup(SortNotesPopupElement);
        }

        private void TagsFilterButton_Click(object sender, RoutedEventArgs e)
        {
            CollapseTopSearchPanel();
            UiMotion.ClosePopup(SortNotesPopupElement);
            UiMotion.ClosePopup(DashboardSectionsPopupElement);
            UiMotion.TogglePopup(TagsFilterPopup);
        }

        private void DashboardSectionsButton_Click(object sender, RoutedEventArgs e)
        {
            CollapseTopSearchPanel();
            UiMotion.ClosePopup(SortNotesPopupElement);
            UiMotion.ClosePopup(TagsFilterPopup);
            UiMotion.TogglePopup(DashboardSectionsPopupElement);
        }

        private void TopSearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (TopSearchPanel.Visibility == Visibility.Visible)
            {
                CollapseTopSearchPanel();
                return;
            }

            ExpandTopSearchPanel();
        }

        private void TopSearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            CollapseTopSearchPanel();
            e.Handled = true;
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var aboutPanel = this.FindName("AboutPanelControl") as AboutPanel;
            if (aboutPanel != null)
            {
                aboutPanel.DataContext = this.DataContext;
                aboutPanel.ShowAnimated();
            }
        }

        private void SidebarTrashButton_Click(object sender, RoutedEventArgs e)
        {
            var trashPanel = this.FindName("TrashPanelControl") as TrashPanel;
            if (trashPanel != null)
            {
                trashPanel.DataContext = this.DataContext;
                trashPanel.ShowAnimated();
            }
        }

        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.DataContext is ViewModels.MainViewModel vm)
                vm.ActiveSearchQuery = string.Empty;

            e.Handled = true;
            Dispatcher.BeginInvoke(() =>
            {
                TopSearchTextBox.Focus();
                TopSearchTextBox.CaretIndex = TopSearchTextBox.Text.Length;
            }, DispatcherPriority.Input);
        }

        private static CubicEase FreezeEase(EasingMode mode)
        {
            var ease = new CubicEase { EasingMode = mode };
            ease.Freeze();
            return ease;
        }

        private void SyncClearSearchButton(bool animate)
        {
            var show = !string.IsNullOrEmpty(_observedViewModel?.ActiveSearchQuery);
            if (show == _clearSearchButtonShown)
                return;

            _clearSearchButtonShown = show;
            var button = ClearSearchButton;
            UiMotion.Stop(button);

            if (!animate || UiMotion.PrefersReducedMotion)
            {
                ApplyClearSearchButtonRestState(button, show);
                return;
            }

            button.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = UiMotion.EnsureScale(button, show ? ClearSearchButtonHiddenScale : 1);
            if (show)
            {
                button.Visibility = Visibility.Visible;
                button.IsHitTestVisible = true;
                button.Width = 0;
                button.Margin = new Thickness(0);
                button.Opacity = 0;
                scale.ScaleX = ClearSearchButtonHiddenScale;
                scale.ScaleY = ClearSearchButtonHiddenScale;
            }
            else
            {
                button.IsHitTestVisible = false;
            }

            var opacityFrom = button.Opacity;
            var widthFrom = double.IsNaN(button.Width) ? (show ? 0 : ClearSearchButtonWidth) : button.Width;
            var marginFrom = button.Margin.Left;
            var scaleFrom = scale.ScaleX;
            var duration = show ? UiMotion.HoverInMs : UiMotion.HoverOutMs;
            var easing = show ? ClearSearchShowEase : ClearSearchHideEase;

            UiMotion.Run(
                button,
                new[]
                {
                    UiMotion.Track(duration, easing, opacityFrom, show ? 1.0 : 0.0, value => button.Opacity = value),
                    UiMotion.Track(duration, easing, widthFrom, show ? ClearSearchButtonWidth : 0, value => button.Width = value),
                    UiMotion.Track(duration, easing, marginFrom, show ? ClearSearchButtonMargin : 0, value => button.Margin = new Thickness(value, 0, 0, 0)),
                    UiMotion.Track(duration, easing, scaleFrom, show ? 1.0 : ClearSearchButtonHiddenScale, value =>
                    {
                        scale.ScaleX = value;
                        scale.ScaleY = value;
                    })
                },
                () =>
                {
                    if (show != _clearSearchButtonShown)
                        return;

                    ApplyClearSearchButtonRestState(button, show);
                });
        }

        private static void ApplyClearSearchButtonRestState(Button button, bool show)
        {
            var scale = UiMotion.EnsureScale(button, 1);
            button.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            button.IsHitTestVisible = show;
            button.Opacity = show ? 1 : 0;
            button.Width = show ? ClearSearchButtonWidth : 0;
            button.Margin = show ? new Thickness(ClearSearchButtonMargin, 0, 0, 0) : new Thickness(0);
            scale.ScaleX = show ? 1 : ClearSearchButtonHiddenScale;
            scale.ScaleY = show ? 1 : ClearSearchButtonHiddenScale;
        }

        private void ExpandTopSearchPanel()
        {
            UiMotion.ClosePopup(TagsFilterPopup);
            UiMotion.ClosePopup(SortNotesPopupElement);
            UiMotion.ClosePopup(DashboardSectionsPopupElement);

            TopSearchPanel.IsHitTestVisible = true;
            UiMotion.ExpandHorizontalPanel(
                TopSearchPanel,
                show: true,
                TopSearchExpandedWidth,
                UiMotion.SearchMs);

            Dispatcher.BeginInvoke(() =>
            {
                TopSearchTextBox.Focus();
                TopSearchTextBox.SelectAll();
            }, DispatcherPriority.Input);
        }

        private void CollapseTopSearchPanel()
        {
            if (TopSearchPanel.Visibility != Visibility.Visible)
                return;

            UiMotion.ExpandHorizontalPanel(
                TopSearchPanel,
                show: false,
                TopSearchExpandedWidth,
                UiMotion.SearchMs,
                () => TopSearchPanel.IsHitTestVisible = false);
        }

        // Open editor for a specific note card
        public void OpenNoteEditor(NoteCardViewModel noteViewModel)
        {
            var openingNewWindow = _noteEditorTabsWindow == null || !_noteEditorTabsWindow.IsLoaded;
            if (openingNewWindow)
            {
                _noteEditorTabsWindow = new NoteEditorTabsWindow
                {
                    Owner = this
                };

                _noteEditorTabsWindow.Closed += (_, _) =>
                {
                    _noteEditorTabsWindow = null;
                    Activate();
                };

                _noteEditorTabsWindow.OpenOrFocusNote(noteViewModel, DataContext, animateContent: false);
                _noteEditorTabsWindow.Show();
                _noteEditorTabsWindow.Activate();
                return;
            }

            var editorWindow = _noteEditorTabsWindow!;
            editorWindow.OpenOrFocusNote(noteViewModel, DataContext);
            editorWindow.Activate();
        }

        public void OpenNoteSchedule(NoteCardViewModel noteViewModel)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var schedulePanel = FindName("NoteSchedulePanelControl") as NoteSchedulePanel;
            schedulePanel?.ShowAnimated(vm, noteViewModel);
        }

        internal void CreateFlashcardsButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel { IsMassSelectMode: true })
                return;

            if (DataContext is MainViewModel vm)
                OpenFlashcardSetEditor(vm, null);
        }

        internal void FlashcardDashboardActionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { ContextMenu: { } menu } button)
                UiMotion.OpenContextMenu(menu, button);
        }

        internal void OpenFlashcardCombineMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var window = new Scriblucent.Views.FlashcardCombineWindow(vm)
            {
                Owner = this
            };
            window.ShowDialog();
        }

        internal void OpenFlashcardHistoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var window = new Scriblucent.Views.FlashcardStudyHistoryWindow(vm)
            {
                Owner = this
            };
            window.ShowDialog();
        }

        internal void OpenFlashcardStatisticsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var window = new Scriblucent.Views.FlashcardStatisticsWindow(vm)
            {
                Owner = this
            };
            window.ShowDialog();
        }

        internal void ImportFlashcardsButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = LocalizationService.GetString("FlashcardImportDialogFilter"),
                Title = LocalizationService.GetString("FlashcardImportDialogTitle")
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            if (DataContext is not MainViewModel vm)
                return;

            // Ask once for the whole batch, and only if at least one selected file could actually
            // carry progress data - CSV/TXT can never represent it, so they never trigger this.
            var keepProgress = false;
            var progressCapableFiles = openFileDialog.FileNames
                .Where(FlashcardFileService.IsScribFlashPath)
                .ToList();

            if (progressCapableFiles.Count > 0)
            {
                var anyHasProgress = progressCapableFiles.Any(path =>
                {
                    try
                    {
                        return FlashcardFileService.HasProgressData(FlashcardFileService.LoadScribFlashDocument(path));
                    }
                    catch
                    {
                        return false;
                    }
                });

                if (anyHasProgress)
                {
                    var choice = ModernDialog.Show(
                        this,
                        LocalizationService.GetString("FlashcardImportKeepProgressTitle"),
                        LocalizationService.GetString("FlashcardImportKeepProgressPrompt"),
                        ModernDialogTone.Question,
                        LocalizationService.GetString("FlashcardImportKeepProgressKeep"),
                        LocalizationService.GetString("Cancel"),
                        LocalizationService.GetString("FlashcardImportKeepProgressDiscard"),
                        primaryStyle: ModernDialogButtonStyle.Secondary,
                        secondaryStyle: ModernDialogButtonStyle.Primary,
                        defaultChoice: ModernDialogResult.Secondary);

                    switch (choice)
                    {
                        case ModernDialogResult.Primary:
                            keepProgress = true;
                            break;
                        case ModernDialogResult.Secondary:
                            keepProgress = false;
                            break;
                        default:
                            return;
                    }
                }
            }

            var imported = new List<FlashcardSetDocument>();
            var warnings = new List<string>();
            foreach (var filePath in openFileDialog.FileNames)
            {
                try
                {
                    var document = FlashcardFileService.LoadAnyFlashcardDocument(
                        filePath,
                        Path.GetFileNameWithoutExtension(filePath),
                        keepProgress,
                        out var fileWarnings);
                    imported.Add(document);
                    warnings.AddRange(fileWarnings.Select(warning => $"{Path.GetFileName(filePath)}: {warning}"));
                }
                catch (Exception ex)
                {
                    ModernMessageBox.Show(
                        $"{LocalizationService.GetString("ImportError")}\n{Path.GetFileName(filePath)}\n\n{ex.Message}",
                        LocalizationService.GetString("Error"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            if (imported.Count == 0)
                return;

            // Single batch add so a multi-file import pays one save, not one per file - see
            // MainViewModel.AddImportedFlashcardSets for the same reasoning AddImportedQuizzes documents.
            vm.AddImportedFlashcardSets(imported);

            var summary = string.Format(LocalizationService.GetString("FlashcardImportSuccessCount"), imported.Count);
            if (warnings.Count > 0)
                summary += "\n\n" + string.Join("\n", warnings);

            ModernMessageBox.Show(
                summary,
                LocalizationService.GetString("FlashcardImport"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        internal void CreateQuizButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel { IsMassSelectMode: true })
                return;

            if (DataContext is MainViewModel vm)
                OpenQuizEditor(vm, null);
        }

        internal void CreateMindMapButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel { IsMassSelectMode: true })
                return;

            if (DataContext is not MainViewModel vm)
                return;

            // Create a blank root node with a default title
            var rootNode = new MindMapNode { Text = "Central Topic", NodeShape = "Rounded" };

            MindMapDocument? savedDocument = null;
            Action saveNow = null!;

            var editor = new MindMapPreviewWindow(
                rootNode,
                noteOptions: vm.Notes.Select(note => new MindMapPreviewWindow.MindMapNoteLinkOption(note.Document.Id, note.Document.Title)),
                modelDisplayName: null,
                title: null,
                tags: null,
                layoutMode: null,
                useManualPositions: false,
                openNoteAction: noteId => OpenNoteById(vm, noteId))
            {
                Owner = this
            };

            editor.AiReviewNoticeDismissedPersistRequested += (_, _) => PersistAiReviewNoticeDismissal(savedDocument, vm.SaveMindMaps);

            saveNow = () =>
            {
                var updatedDocument = editor.ToDocument(savedDocument);
                savedDocument = vm.AddOrUpdateMindMap(updatedDocument).Document;
                editor.MarkCurrentStateAutoSaved();
            };

            var autoSaveTimer = StartEditorAutoSave(editor.HasPendingAutoSaveChanges, saveNow);

            try
            {
                if (editor.ShowDialog() == true)
                {
                    var newDocument = editor.ToDocument(savedDocument);
                    vm.AddOrUpdateMindMap(newDocument);
                    vm.SaveMindMaps();
                }
            }
            finally
            {
                StopEditorAutoSave(autoSaveTimer);
            }
        }

        private void OpenFlashcardSetEditor(MainViewModel vm, FlashcardSetViewModel? set)
        {
            var document = set?.Document;
            FlashcardSetDocument? savedDocument = document;
            Action saveNow = null!;

            var editor = new FlashcardsPreviewWindow(
                document?.Cards ?? Enumerable.Empty<FlashcardItem>(),
                vm.Notes.Select(note => new FlashcardsPreviewWindow.FlashcardNoteLinkOption(note.Document.Id, note.Document.Title)),
                document?.AiModelDisplayName,
                document?.Title,
                document?.Tags,
                document?.SetNames,
                document?.StudySession,
                document?.SourceNoteId,
                noteId => OpenNoteById(vm, noteId),
                document?.FlipDelayMilliseconds ?? 0,
                document?.StudyDueCardsOnly ?? true,
                isTagsPanelOpen: document?.IsEditorTagsPanelOpen,
                aiReviewNoticeDismissed: document?.AiReviewNoticeDismissed ?? false)
            {
                Owner = this
            };

            editor.AiReviewNoticeDismissedPersistRequested += (_, _) =>
            {
                PersistAiReviewNoticeDismissal(savedDocument, vm.SaveFlashcardSets);
                set?.NotifyChanged();
            };

            saveNow = () =>
            {
                var updatedDocument = editor.ToDocument(savedDocument);
                savedDocument = vm.AddOrUpdateFlashcardSet(updatedDocument).Document;
                editor.MarkCurrentStateAutoSaved();
            };

            var autoSaveTimer = StartEditorAutoSave(editor.HasPendingAutoSaveChanges, saveNow);

            try
            {
                if (editor.ShowDialog() == true)
                    vm.AddOrUpdateFlashcardSet(editor.ToDocument(savedDocument));
            }
            finally
            {
                StopEditorAutoSave(autoSaveTimer);
            }
        }

        internal void OpenFlashcardSetMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is MenuItem { DataContext: FlashcardSetViewModel set })
                OpenFlashcardSetEditor(vm, set);
        }

        private void OpenQuizEditor(MainViewModel vm, QuizViewModel? quiz)
        {
            var document = quiz?.Document ?? new QuizDocument
            {
                Title = LocalizationService.GetString("QuizUntitled"),
                Questions = new List<QuizQuestion>
                {
                    new()
                    {
                        Type = QuizQuestionType.SingleChoice,
                        Question = LocalizationService.GetString("NewQuizQuestion"),
                        Options = new List<QuizOption>
                        {
                            new() { Text = LocalizationService.GetString("NewQuizCorrectAnswer"), IsCorrect = true },
                            new() { Text = LocalizationService.GetString("NewQuizWrongAnswer"), IsCorrect = false },
                            new() { Text = LocalizationService.GetString("NewQuizWrongAnswer"), IsCorrect = false }
                        }
                    }
                }
            };

            QuizDocument? savedDocument = quiz?.Document;
            Action saveNow = null!;

            var editor = new QuizPreviewWindow(
                document,
                vm.Notes.Select(note => new QuizPreviewWindow.QuizNoteLinkOption(note.Document.Id, note.Document.Title)),
                document.AiModelDisplayName,
                document.Title,
                noteId => OpenNoteById(vm, noteId),
                requestImmediateSave: () => saveNow())
            {
                Owner = this
            };

            editor.AiReviewNoticeDismissedPersistRequested += (_, _) =>
            {
                PersistAiReviewNoticeDismissal(savedDocument, vm.SaveQuizzes);
                quiz?.NotifyChanged();
            };

            // Finishing (or abandoning mid-way) a quiz attempt calls this directly instead of
            // waiting on the autosave timer below, so attempt history, per-question memory, and
            // in-progress resume state are never lost to a short editor session.
            saveNow = () =>
            {
                var updatedDocument = editor.ToDocument(savedDocument);
                savedDocument = vm.AddOrUpdateQuiz(updatedDocument).Document;
                editor.MarkCurrentStateAutoSaved();
            };

            var autoSaveTimer = StartEditorAutoSave(editor.HasPendingAutoSaveChanges, saveNow);

            try
            {
                if (editor.ShowDialog() == true)
                    vm.AddOrUpdateQuiz(editor.ToDocument(savedDocument));
            }
            finally
            {
                StopEditorAutoSave(autoSaveTimer);
            }
        }

        private void OpenQuizLibraryButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var window = new QuizLibraryWindow(vm)
            {
                Owner = this
            };

            window.ShowDialog();
        }

        internal void QuizDashboardActionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { ContextMenu: { } menu } button)
                UiMotion.OpenContextMenu(menu, button);
        }

        internal void OpenQuizLibraryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenQuizLibraryButton_Click(sender, e);
        }

        internal void OpenAttemptHistoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            OpenAttemptHistoryButton_Click(sender, e);
        }

        private void OpenAttemptHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var window = new Scriblucent.Views.QuizAttemptHistoryWindow(vm)
            {
                Owner = this
            };
            window.ShowDialog();
        }

        internal void OpenNoteById(MainViewModel vm, Guid noteId)
        {
            var note = vm.FindNoteById(noteId);
            if (note is null)
                return;

            OpenNoteEditor(note);
        }

        private QuizViewModel? GetQuizFromMenuSender(object sender)
        {
            if (sender is not MenuItem menuItem)
                return null;

            var contextMenu = menuItem.Parent as ContextMenu;
            var target = contextMenu?.PlacementTarget as FrameworkElement;
            return target?.DataContext as QuizViewModel;
        }

        internal void DeleteQuizMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var quiz = GetQuizFromMenuSender(sender);
            if (quiz is null)
                return;

            if (DataContext is not MainViewModel vm)
                return;

            var confirmed = ModernDialog.ConfirmDanger(
                this,
                LocalizationService.GetString("DeleteQuiz"),
                string.Format(LocalizationService.GetString("DeleteQuizConfirmationFormat"), quiz.Title),
                LocalizationService.GetString("Confirm"));

            if (confirmed)
                vm.RequestDeleteQuiz(quiz);
        }

        internal void OpenQuizMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm
                && sender is MenuItem menuItem
                && menuItem.DataContext is QuizViewModel quizVm)
            {
                OpenQuizEditor(vm, quizVm);
            }
        }

        internal void DuplicateQuizMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is QuizViewModel quizVm)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.DuplicateQuiz(quizVm);
                }
            }
        }

        internal void ToggleQuizPinMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is MenuItem { DataContext: QuizViewModel quizVm })
                vm.ToggleQuizPin(quizVm);
        }

        internal void RemoveQuizFromGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is MenuItem { DataContext: QuizViewModel quizVm })
                vm.RemoveQuizFromGroup(quizVm);
        }

        internal void QuizInfoMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var quiz = GetQuizFromMenuSender(sender)
                ?? (sender as MenuItem)?.DataContext as QuizViewModel;

            if (quiz != null)
                ShowQuizInfo(quiz);
        }

        private FlashcardSetViewModel? GetFlashcardSetFromMenuSender(object sender)
        {
            if (sender is not MenuItem menuItem)
                return null;

            var contextMenu = menuItem.Parent as ContextMenu;
            var target = contextMenu?.PlacementTarget as FrameworkElement;
            return target?.DataContext as FlashcardSetViewModel;
        }

        internal void DeleteFlashcardSetMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var set = GetFlashcardSetFromMenuSender(sender);
            if (set is null)
                return;

            if (DataContext is not MainViewModel vm)
                return;

            var confirmed = ModernDialog.ConfirmDanger(
                this,
                LocalizationService.GetString("DeleteFlashcardSet"),
                string.Format(LocalizationService.GetString("DeleteFlashcardSetConfirmationFormat"), set.Title),
                LocalizationService.GetString("Confirm"));

            if (confirmed)
                vm.RequestDeleteFlashcardSet(set);
        }

        internal void DuplicateFlashcardSetMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is MenuItem { DataContext: FlashcardSetViewModel set })
                vm.DuplicateFlashcardSet(set);
        }

        internal void ToggleFlashcardSetPinMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is MenuItem { DataContext: FlashcardSetViewModel set })
                vm.ToggleFlashcardSetPin(set);
        }

        internal void RemoveFlashcardSetFromGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is MenuItem { DataContext: FlashcardSetViewModel set })
                vm.RemoveFlashcardSetFromGroup(set);
        }

        internal void FlashcardSetInfoMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var set = GetFlashcardSetFromMenuSender(sender)
                ?? (sender as MenuItem)?.DataContext as FlashcardSetViewModel;

            if (set != null)
                ShowFlashcardSetInfo(set);
        }

        internal void OpenMindMapMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is MindMapViewModel mindMapVm)
            {
                OpenMindMapEditor(mindMapVm);
            }
        }

        internal async void DuplicateMindMapMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.DataContext is not MindMapViewModel mindMapVm)
                return;

            // Duplicate the mind map
            if (DataContext is MainViewModel vm)
            {
                var newMindMap = vm.DuplicateMindMap(mindMapVm);
                if (newMindMap != null)
                {
                    // Optionally open the editor for the duplicated mind map
                    // OpenMindMapEditor(newMindMap);
                }
            }
        }

        internal void ToggleMindMapPinMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is MenuItem { DataContext: MindMapViewModel mindMapVm })
                vm.ToggleMindMapPin(mindMapVm);
        }

        internal async void DeleteMindMapMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.DataContext is not MindMapViewModel mindMapVm)
                return;

            var confirmed = ModernDialog.ConfirmDanger(
                this,
                LocalizationService.GetString("DeleteMindMap"),
                string.Format(
                    LocalizationService.GetString("DeleteMindMapConfirmationFormat"),
                    mindMapVm.Title),
                LocalizationService.GetString("Confirm"));

            if (!confirmed)
                return;

            // Delete the mind map
            if (DataContext is MainViewModel vm)
                vm.RequestDeleteMindMap(mindMapVm);
        }

        internal void RemoveMindMapFromGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is MenuItem { DataContext: MindMapViewModel mindMapVm })
                vm.RemoveMindMapFromGroup(mindMapVm);
        }

        internal void MindMapInfoMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: MindMapViewModel mindMapVm })
                ShowMindMapInfo(mindMapVm);
        }

        private void OpenMindMapEditor(MindMapViewModel mindMapVm)
        {
            if (DataContext is not MainViewModel vm)
                return;

            MindMapDocument? savedDocument = mindMapVm.Document;
            Action saveNow = null!;

            var editor = new MindMapPreviewWindow(
                mindMapVm.Document.Root,
                vm.Notes.Select(note => new MindMapPreviewWindow.MindMapNoteLinkOption(note.Document.Id, note.Document.Title)),
                mindMapVm.Document.AiModelDisplayName,
                mindMapVm.Document.Title,
                mindMapVm.Document.Tags,
                mindMapVm.Document.LayoutMode,
                mindMapVm.Document.UseManualPositions,
                mindMapVm.Document.SourceNoteId,
                noteId => OpenNoteById(vm, noteId),
                isTagsPanelOpen: mindMapVm.Document.IsEditorTagsPanelOpen,
                aiReviewNoticeDismissed: mindMapVm.Document.AiReviewNoticeDismissed)
            {
                Owner = this
            };

            editor.AiReviewNoticeDismissedPersistRequested += (_, _) =>
            {
                PersistAiReviewNoticeDismissal(savedDocument, vm.SaveMindMaps);
                mindMapVm.NotifyChanged();
            };

            saveNow = () =>
            {
                var updatedDocument = editor.ToDocument(savedDocument);
                savedDocument = vm.AddOrUpdateMindMap(updatedDocument).Document;
                editor.MarkCurrentStateAutoSaved();
            };

            var autoSaveTimer = StartEditorAutoSave(editor.HasPendingAutoSaveChanges, saveNow);

            try
            {
                if (editor.ShowDialog() == true)
                    vm.AddOrUpdateMindMap(editor.ToDocument(savedDocument));
            }
            finally
            {
                StopEditorAutoSave(autoSaveTimer);
            }
        }

        private static void PersistAiReviewNoticeDismissal(FlashcardSetDocument? document, Action save)
        {
            if (document is null)
                return;

            document.AiReviewNoticeDismissed = true;
            save();
        }

        private static void PersistAiReviewNoticeDismissal(MindMapDocument? document, Action save)
        {
            if (document is null)
                return;

            document.AiReviewNoticeDismissed = true;
            save();
        }

        private static void PersistAiReviewNoticeDismissal(QuizDocument? document, Action save)
        {
            if (document is null)
                return;

            document.AiReviewNoticeDismissed = true;
            save();
        }

        private DispatcherTimer? StartEditorAutoSave(Func<bool> hasChanges, Action saveAction)
        {
            var settings = AppSettingsService.Load();
            if (!settings.EnableAutoSave)
                return null;

            var intervalSeconds = Math.Clamp(settings.AutoSaveIntervalSeconds, 5, 86400);
            var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(intervalSeconds)
            };

            timer.Tick += (_, _) =>
            {
                if (!AppSettingsService.Load().EnableAutoSave)
                {
                    timer.Stop();
                    return;
                }

                if (!hasChanges())
                    return;

                try
                {
                    saveAction();
                }
                catch
                {
                    // Auto-save should never interrupt editing.
                }
            };
            timer.Start();
            return timer;
        }

        private static void StopEditorAutoSave(DispatcherTimer? timer)
        {
            timer?.Stop();
        }

        private static bool IsWithinCalendarScheduleGearButton(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Button button && string.Equals(button.Name, "CalendarScheduleGearButton", StringComparison.Ordinal))
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        // Settings menu button click handler
        private void SettingsMenuButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsPanel = this.FindName("SettingsPanelControl") as SettingsPanel;
            if (settingsPanel != null)
            {
                settingsPanel.DataContext = this.DataContext;
                settingsPanel.ShowAnimated();
            }
        }
        internal void RecentNoteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is NoteCardViewModel noteVm)
                OpenNoteEditor(noteVm);
        }

        internal void RecentFlashcardSetButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is Button { Tag: FlashcardSetViewModel set })
                OpenFlashcardSetEditor(vm, set);
        }

        internal void RecentMindMapButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: MindMapViewModel mindMap })
                OpenMindMapEditor(mindMap);
        }

        internal void RecentQuizButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm && sender is Button { Tag: QuizViewModel quiz })
                OpenQuizEditor(vm, quiz);
        }

        internal void CalendarScheduledItemCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && IsWithinCalendarScheduleGearButton(source))
                return;

            if (sender is not Border { Tag: CalendarScheduledItemViewModel item })
                return;

            if (DataContext is not MainViewModel vm)
                return;

            switch (item.ItemType)
            {
                case ScheduledItemType.Note when item.Note != null:
                    OpenNoteEditor(item.Note);
                    break;
                case ScheduledItemType.Flashcard when item.FlashcardSet != null:
                    OpenFlashcardSetEditor(vm, item.FlashcardSet);
                    break;
                case ScheduledItemType.MindMap when item.MindMap != null:
                    OpenMindMapEditor(item.MindMap);
                    break;
                case ScheduledItemType.Quiz when item.Quiz != null:
                    OpenQuizEditor(vm, item.Quiz);
                    break;
            }
        }

        internal void CalendarScheduleGearButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: CalendarScheduledItemViewModel item })
            {
                switch (item.ItemType)
                {
                    case ScheduledItemType.Note when item.Note != null:
                        OpenNoteSchedule(item.Note);
                        break;
                    case ScheduledItemType.Flashcard when item.FlashcardSet != null:
                        OpenFlashcardSetSchedule(item.FlashcardSet);
                        break;
                    case ScheduledItemType.MindMap when item.MindMap != null:
                        OpenMindMapSchedule(item.MindMap);
                        break;
                    case ScheduledItemType.Quiz when item.Quiz != null:
                        OpenQuizSchedule(item.Quiz);
                        break;
                }
            }

            e.Handled = true;
        }

        private void OpenFlashcardSetSchedule(FlashcardSetViewModel flashcardSet)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var schedulePanel = FindName("NoteSchedulePanelControl") as NoteSchedulePanel;
            var subtitle = $"{LocalizationService.GetString("FlashcardsDashboard")} · {flashcardSet.Document.Title}";
            schedulePanel?.ShowAnimated(vm, flashcardSet.Document.Schedules ?? Enumerable.Empty<NoteScheduleEntry>(),
                entries => vm.SetFlashcardSetSchedules(flashcardSet, entries),
                LocalizationService.GetString("CalendarAssignMenu"), subtitle);
        }

        private void OpenMindMapSchedule(MindMapViewModel mindMap)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var schedulePanel = FindName("NoteSchedulePanelControl") as NoteSchedulePanel;
            var subtitle = $"{LocalizationService.GetString("MindMapsDashboard")} · {mindMap.Document.Title}";
            schedulePanel?.ShowAnimated(vm, mindMap.Document.Schedules ?? Enumerable.Empty<NoteScheduleEntry>(),
                entries => vm.SetMindMapSchedules(mindMap, entries),
                LocalizationService.GetString("CalendarAssignMenu"), subtitle);
        }

        private void OpenQuizSchedule(QuizViewModel quiz)
        {
            if (DataContext is not MainViewModel vm)
                return;

            var schedulePanel = FindName("NoteSchedulePanelControl") as NoteSchedulePanel;
            var subtitle = $"{LocalizationService.GetString("QuizzesDashboard")} · {quiz.Document.Title}";
            schedulePanel?.ShowAnimated(vm, quiz.Document.Schedules ?? Enumerable.Empty<NoteScheduleEntry>(),
                entries => vm.SetQuizSchedules(quiz, entries),
                LocalizationService.GetString("CalendarAssignMenu"), subtitle);
        }

        internal void ScheduleFlashcardSetMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var set = GetFlashcardSetFromMenuSender(sender);
            if (set != null)
                OpenFlashcardSetSchedule(set);
        }

        internal void ScheduleMindMapMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is MindMapViewModel mindMapVm)
                OpenMindMapSchedule(mindMapVm);
        }

        internal void ScheduleQuizMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is QuizViewModel quizVm)
                OpenQuizSchedule(quizVm);
        }

        public void ShowNoteInfo(NoteCardViewModel note)
        {
            var schedules = GetNoteSchedules(note.Document);
            var content = note.Content ?? string.Empty;
            var rows = new List<(string Label, string Value)>
            {
                (InfoText("InfoLabelType"), InfoText("InfoTypeNote")),
                (InfoText("InfoLabelTitle"), string.IsNullOrWhiteSpace(note.Title) ? LocalizationService.GetString("NewNoteTitle") : note.Title),
                (InfoText("InfoLabelCreated"), FormatInfoDate(note.Document.CreatedAt)),
                (InfoText("InfoLabelModified"), FormatInfoDate(note.Document.LastModified)),
                (InfoText("InfoLabelGroup"), GetNoteInfoGroupName(note.Document.GroupId)),
                (InfoText("InfoLabelPinned"), FormatInfoBool(note.Document.IsPinned)),
                (InfoText("InfoLabelScheduled"), FormatScheduleInfo(schedules)),
                (InfoText("InfoLabelTags"), FormatInfoTags(note.Document.Tags)),
                (InfoText("InfoLabelWords"), CountWords(content).ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelCharacters"), content.Length.ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelEditHistory"), (note.Document.EditHistory?.Count ?? 0).ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelFont"), $"{note.Document.FontFamily}, {note.Document.FontSize.ToString("0.#", CultureInfo.CurrentCulture)}"),
                (InfoText("InfoLabelId"), note.Document.Id.ToString())
            };

            ShowItemInfoDialog(note.Title, rows.Take(8), rows.Skip(8));
        }

        private void ShowFlashcardSetInfo(FlashcardSetViewModel set)
        {
            var cards = set.Document.Cards ?? new List<FlashcardItem>();
            var rows = new List<(string Label, string Value)>
            {
                (InfoText("InfoLabelType"), InfoText("InfoTypeFlashcards")),
                (InfoText("InfoLabelTitle"), set.Title),
                (InfoText("InfoLabelCreated"), FormatInfoDate(set.Document.CreatedAt)),
                (InfoText("InfoLabelModified"), FormatInfoDate(set.Document.LastModified)),
                (InfoText("InfoLabelGroup"), GetFlashcardSetInfoGroupName(set.Document.GroupId)),
                (InfoText("InfoLabelPinned"), FormatInfoBool(set.Document.IsPinned)),
                (InfoText("InfoLabelScheduled"), FormatScheduleInfo(set.Document.Schedules)),
                (InfoText("InfoLabelTags"), FormatInfoTags(set.Document.Tags)),
                (InfoText("InfoLabelCards"), set.CardCount.ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelSets"), set.SetCount.ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelKnownCards"), cards.Count(card => card.IsKnown).ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelUnknownCards"), cards.Count(card => card.IsUnknown).ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelCategories"), cards.Select(card => card.Category?.Trim()).Where(category => !string.IsNullOrWhiteSpace(category)).Distinct(StringComparer.CurrentCultureIgnoreCase).Count().ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelSourceNote"), GetSourceNoteInfoTitle(set.Document.SourceNoteId)),
                (InfoText("InfoLabelId"), set.Document.Id.ToString())
            };
            MaybeAddGeneratedWithInfo(rows, set.Document.AiModelDisplayName);

            ShowItemInfoDialog(set.Title, rows.Take(8), rows.Skip(8));
        }

        private void ShowMindMapInfo(MindMapViewModel mindMap)
        {
            var rows = new List<(string Label, string Value)>
            {
                (InfoText("InfoLabelType"), InfoText("InfoTypeMindMap")),
                (InfoText("InfoLabelTitle"), mindMap.Title),
                (InfoText("InfoLabelCreated"), FormatInfoDate(mindMap.Document.CreatedAt)),
                (InfoText("InfoLabelModified"), FormatInfoDate(mindMap.Document.LastModified)),
                (InfoText("InfoLabelGroup"), GetMindMapInfoGroupName(mindMap.Document.GroupId)),
                (InfoText("InfoLabelPinned"), FormatInfoBool(mindMap.Document.IsPinned)),
                (InfoText("InfoLabelScheduled"), FormatScheduleInfo(mindMap.Document.Schedules)),
                (InfoText("InfoLabelTags"), FormatInfoTags(mindMap.Document.Tags)),
                (InfoText("InfoLabelNodes"), mindMap.NodeCount.ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelBranches"), mindMap.BranchCount.ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelLayout"), FormatMindMapLayout(mindMap.Document.LayoutMode)),
                (InfoText("InfoLabelManualPositioning"), FormatInfoBool(mindMap.Document.UseManualPositions)),
                (InfoText("InfoLabelSourceNote"), GetSourceNoteInfoTitle(mindMap.Document.SourceNoteId)),
                (InfoText("InfoLabelId"), mindMap.Document.Id.ToString())
            };
            MaybeAddGeneratedWithInfo(rows, mindMap.Document.AiModelDisplayName);

            ShowItemInfoDialog(mindMap.Title, rows.Take(8), rows.Skip(8));
        }

        private void ShowQuizInfo(QuizViewModel quiz)
        {
            var attempts = quiz.Document.Attempts ?? new List<QuizAttempt>();
            var rows = new List<(string Label, string Value)>
            {
                (InfoText("InfoLabelType"), InfoText("InfoTypeQuiz")),
                (InfoText("InfoLabelTitle"), quiz.Title),
                (InfoText("InfoLabelCreated"), FormatInfoDate(quiz.Document.CreatedAt)),
                (InfoText("InfoLabelModified"), FormatInfoDate(quiz.Document.LastModified)),
                (InfoText("InfoLabelGroup"), GetQuizInfoGroupName(quiz.Document.GroupId)),
                (InfoText("InfoLabelPinned"), FormatInfoBool(quiz.Document.IsPinned)),
                (InfoText("InfoLabelScheduled"), FormatScheduleInfo(quiz.Document.Schedules)),
                (InfoText("InfoLabelTags"), FormatInfoTags(quiz.Document.Tags)),
                (InfoText("InfoLabelQuestions"), quiz.QuestionCount.ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelPassingScore"), $"{quiz.Document.PassingScorePercent.ToString(CultureInfo.CurrentCulture)}%"),
                (InfoText("InfoLabelTimeLimit"), FormatTimeLimit(quiz.Document.TimeLimitSeconds)),
                (InfoText("InfoLabelAttempts"), attempts.Count.ToString(CultureInfo.CurrentCulture)),
                (InfoText("InfoLabelBestScore"), FormatBestQuizScore(attempts)),
                (InfoText("InfoLabelSourceNote"), GetSourceNoteInfoTitle(quiz.Document.SourceNoteId)),
                (InfoText("InfoLabelId"), quiz.Document.Id.ToString())
            };
            MaybeAddGeneratedWithInfo(rows, quiz.Document.AiModelDisplayName);

            ShowItemInfoDialog(quiz.Title, rows.Take(8), rows.Skip(8));
        }

        private void ShowItemInfoDialog(
            string itemTitle,
            IEnumerable<(string Label, string Value)> primaryRows,
            IEnumerable<(string Label, string Value)> advancedRows)
        {
            var title = string.IsNullOrWhiteSpace(itemTitle)
                ? LocalizationService.GetString("Info")
                : $"{itemTitle.Trim()} - {LocalizationService.GetString("Info")}";

            var dialog = new ItemInfoDialog(title, primaryRows, advancedRows)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }

        private static string FormatInfoDate(DateTime value)
        {
            if (value == default)
                return InfoText("InfoUnknown");

            var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
            return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        }

        private static string FormatInfoBool(bool value) => value ? InfoText("InfoYes") : InfoText("InfoNo");

        private static string FormatOptional(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? InfoText("InfoNone") : value.Trim();
        }

        private static void MaybeAddGeneratedWithInfo(List<(string Label, string Value)> rows, string? modelDisplayName)
        {
            if (!AiFeatureGate.IsEnabled)
                return;

            rows.Insert(
                Math.Max(0, rows.Count - 1),
                (InfoText("InfoLabelGeneratedWith"), FormatOptional(modelDisplayName)));
        }

        private static string FormatInfoTags(IEnumerable<string>? tags)
        {
            var cleaned = tags?
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList() ?? new List<string>();

            return cleaned.Count == 0 ? InfoText("InfoNone") : string.Join(", ", cleaned);
        }

        private static string FormatScheduleInfo(IEnumerable<NoteScheduleEntry>? schedules)
        {
            var ordered = schedules?
                .OrderBy(schedule => schedule.ScheduledAt)
                .ToList() ?? new List<NoteScheduleEntry>();

            if (ordered.Count == 0)
                return InfoText("InfoNo");

            var now = DateTime.Now;
            var upcoming = ordered.FirstOrDefault(schedule => schedule.ScheduledAt >= now);
            var selected = upcoming ?? ordered[^1];
            var formatKey = upcoming is null ? "InfoScheduleLatestFormat" : "InfoScheduleNextFormat";
            return string.Format(
                CultureInfo.CurrentCulture,
                InfoText(formatKey),
                ordered.Count.ToString(CultureInfo.CurrentCulture),
                FormatInfoDate(selected.ScheduledAt));
        }

        private static IReadOnlyList<NoteScheduleEntry> GetNoteSchedules(NoteDocument document)
        {
            var schedules = document.Schedules?.ToList() ?? new List<NoteScheduleEntry>();
            if (document.ScheduledAt.HasValue
                && !schedules.Any(schedule => schedule.ScheduledAt == document.ScheduledAt.Value))
            {
                schedules.Add(new NoteScheduleEntry
                {
                    ScheduledAt = document.ScheduledAt.Value,
                    Note = document.ScheduleNote
                });
            }

            return schedules.OrderBy(schedule => schedule.ScheduledAt).ToList();
        }

        private string GetNoteInfoGroupName(Guid? groupId)
        {
            if (!groupId.HasValue)
                return InfoText("InfoUngrouped");

            if (DataContext is MainViewModel vm)
            {
                var group = vm.NoteGroups.FirstOrDefault(candidate => candidate.GroupId == groupId.Value);
                if (group != null && !string.IsNullOrWhiteSpace(group.Name))
                    return group.Name;
            }

            return FormatGroupFallback(groupId.Value);
        }

        private string GetFlashcardSetInfoGroupName(Guid? groupId)
        {
            if (!groupId.HasValue)
                return InfoText("InfoUngrouped");

            if (DataContext is MainViewModel vm)
            {
                var group = vm.FlashcardSetGroups.FirstOrDefault(candidate => candidate.GroupId == groupId.Value);
                if (group != null && !string.IsNullOrWhiteSpace(group.Name))
                    return group.Name;
            }

            return FormatGroupFallback(groupId.Value);
        }

        private string GetMindMapInfoGroupName(Guid? groupId)
        {
            if (!groupId.HasValue)
                return InfoText("InfoUngrouped");

            if (DataContext is MainViewModel vm)
            {
                var group = vm.MindMapGroups.FirstOrDefault(candidate => candidate.GroupId == groupId.Value);
                if (group != null && !string.IsNullOrWhiteSpace(group.Name))
                    return group.Name;
            }

            return FormatGroupFallback(groupId.Value);
        }

        private string GetQuizInfoGroupName(Guid? groupId)
        {
            if (!groupId.HasValue)
                return InfoText("InfoUngrouped");

            if (DataContext is MainViewModel vm)
            {
                var group = vm.QuizGroups.FirstOrDefault(candidate => candidate.GroupId == groupId.Value);
                if (group != null && !string.IsNullOrWhiteSpace(group.Name))
                    return group.Name;
            }

            return FormatGroupFallback(groupId.Value);
        }

        private string GetSourceNoteInfoTitle(Guid? sourceNoteId)
        {
            if (!sourceNoteId.HasValue)
                return InfoText("InfoNone");

            if (DataContext is MainViewModel vm)
            {
                var note = vm.FindNoteById(sourceNoteId.Value);
                if (note != null && !string.IsNullOrWhiteSpace(note.Title))
                    return note.Title;
            }

            return sourceNoteId.Value.ToString();
        }

        private static string FormatGroupFallback(Guid groupId)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                InfoText("InfoGroupFallbackFormat"),
                groupId.ToString()[..4].ToUpperInvariant());
        }

        private static string FormatMindMapLayout(string? layoutMode)
        {
            return layoutMode switch
            {
                "BalancedTree" => LocalizationService.GetString("MindMapLayoutBalancedTree"),
                "RightTree" => LocalizationService.GetString("MindMapLayoutRightTree"),
                "LeftTree" => LocalizationService.GetString("MindMapLayoutLeftTree"),
                "TopDown" => LocalizationService.GetString("MindMapLayoutTopDown"),
                "Radial" => LocalizationService.GetString("MindMapLayoutRadial"),
                _ => FormatOptional(layoutMode)
            };
        }

        private static string FormatTimeLimit(int? seconds)
        {
            if (!seconds.HasValue || seconds.Value <= 0)
                return InfoText("InfoNoLimit");

            var duration = TimeSpan.FromSeconds(seconds.Value);
            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture)
                : duration.ToString(@"m\:ss", CultureInfo.CurrentCulture);
        }

        private static string FormatBestQuizScore(IReadOnlyCollection<QuizAttempt> attempts)
        {
            if (attempts.Count == 0)
                return InfoText("InfoNone");

            var best = attempts.Max(attempt => attempt.Percentage);
            return $"{best.ToString("0.#", CultureInfo.CurrentCulture)}%";
        }

        private static int CountWords(string text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? 0
                : text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static string InfoText(string key) => LocalizationService.GetString(key);

        internal void ToggleRecentSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsRecentSectionExpanded = !vm.IsRecentSectionExpanded;
        }

        internal void ToggleGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsGroupsSectionExpanded = !vm.IsGroupsSectionExpanded;
        }

        internal void ToggleCalendarSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsCalendarSectionExpanded = !vm.IsCalendarSectionExpanded;
        }

        internal void ToggleUngroupedSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsUngroupedSectionExpanded = !vm.IsUngroupedSectionExpanded;
        }

        internal void ToggleNotesDashboardSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsNotesDashboardSectionExpanded = !vm.IsNotesDashboardSectionExpanded;
        }

        internal void ToggleFlashcardGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsFlashcardGroupsSectionExpanded = !vm.IsFlashcardGroupsSectionExpanded;
        }

        internal void ToggleQuizGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsQuizGroupsSectionExpanded = !vm.IsQuizGroupsSectionExpanded;
        }

        internal void ToggleMindMapGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsMindMapGroupsSectionExpanded = !vm.IsMindMapGroupsSectionExpanded;
        }

        internal void ToggleFlashcardDashboardGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsFlashcardDashboardGroupsSectionExpanded = !vm.IsFlashcardDashboardGroupsSectionExpanded;
        }

        internal void ToggleFlashcardDashboardUngroupedSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsFlashcardDashboardUngroupedSectionExpanded = !vm.IsFlashcardDashboardUngroupedSectionExpanded;
        }

        internal void MoveFlashcardDashboardGroupsUpButton_Click(object sender, RoutedEventArgs e)
            => SwapFlashcardDashboardGroupsAndUngrouped(groupsFirst: true);

        internal void MoveFlashcardDashboardGroupsDownButton_Click(object sender, RoutedEventArgs e)
            => SwapFlashcardDashboardGroupsAndUngrouped(groupsFirst: false);

        internal void MoveFlashcardDashboardUngroupedUpButton_Click(object sender, RoutedEventArgs e)
            => SwapFlashcardDashboardGroupsAndUngrouped(groupsFirst: false);

        internal void MoveFlashcardDashboardUngroupedDownButton_Click(object sender, RoutedEventArgs e)
            => SwapFlashcardDashboardGroupsAndUngrouped(groupsFirst: true);

        internal void ToggleQuizDashboardGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsQuizDashboardGroupsSectionExpanded = !vm.IsQuizDashboardGroupsSectionExpanded;
        }

        internal void ToggleQuizDashboardUngroupedSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsQuizDashboardUngroupedSectionExpanded = !vm.IsQuizDashboardUngroupedSectionExpanded;
        }

        internal void MoveQuizDashboardGroupsUpButton_Click(object sender, RoutedEventArgs e)
            => SwapQuizDashboardGroupsAndUngrouped(groupsFirst: true);

        internal void MoveQuizDashboardGroupsDownButton_Click(object sender, RoutedEventArgs e)
            => SwapQuizDashboardGroupsAndUngrouped(groupsFirst: false);

        internal void MoveQuizDashboardUngroupedUpButton_Click(object sender, RoutedEventArgs e)
            => SwapQuizDashboardGroupsAndUngrouped(groupsFirst: false);

        internal void MoveQuizDashboardUngroupedDownButton_Click(object sender, RoutedEventArgs e)
            => SwapQuizDashboardGroupsAndUngrouped(groupsFirst: true);

        internal void ToggleMindMapDashboardGroupsSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsMindMapDashboardGroupsSectionExpanded = !vm.IsMindMapDashboardGroupsSectionExpanded;
        }

        internal void ToggleMindMapDashboardUngroupedSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.IsMindMapDashboardUngroupedSectionExpanded = !vm.IsMindMapDashboardUngroupedSectionExpanded;
        }

        internal void MoveMindMapDashboardGroupsUpButton_Click(object sender, RoutedEventArgs e)
            => SwapMindMapDashboardGroupsAndUngrouped(groupsFirst: true);

        internal void MoveMindMapDashboardGroupsDownButton_Click(object sender, RoutedEventArgs e)
            => SwapMindMapDashboardGroupsAndUngrouped(groupsFirst: false);

        internal void MoveMindMapDashboardUngroupedUpButton_Click(object sender, RoutedEventArgs e)
            => SwapMindMapDashboardGroupsAndUngrouped(groupsFirst: false);

        internal void MoveMindMapDashboardUngroupedDownButton_Click(object sender, RoutedEventArgs e)
            => SwapMindMapDashboardGroupsAndUngrouped(groupsFirst: true);

        private void ExitMassSelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.ExitMassSelect();
        }

        private void AddTagsToSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || !vm.IsMassSelectMode || vm.ActiveMassSelectedCount <= 0)
                return;

            var dialog = new SimpleInputDialog(
                LocalizationService.GetString("MassSelectAddTagsTitle"),
                LocalizationService.GetString("MassSelectAddTagsPrompt"))
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
                return;

            vm.AddTagsToSelected(dialog.InputText);
        }

        private void DeleteSelectedMassButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || !vm.IsMassSelectMode || vm.ActiveMassSelectedCount <= 0)
                return;

            var message = string.Format(
                LocalizationService.GetString("DeleteSelectedNotesConfirmation"),
                vm.ActiveMassSelectedCount);

            var confirmed = ModernDialog.ConfirmDanger(
                this,
                LocalizationService.GetString("DeleteNotesTitle"),
                message,
                LocalizationService.GetString("Confirm"));

            if (!confirmed)
                return;

            if (vm.DeleteSelectedNotesCommand.CanExecute(null))
                vm.DeleteSelectedNotesCommand.Execute(null);
        }

        internal void StartDashboardMassSelectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var listing = sender is MenuItem menuItem
                ? menuItem.DataContext ?? ResolveDashboardGroupFromMenuItem(menuItem)
                : null;

            if (DataContext is not MainViewModel vm)
                return;

            switch (listing)
            {
                case FlashcardSetViewModel set:
                    vm.EnterMassSelect(set);
                    break;
                case MindMapViewModel map:
                    vm.EnterMassSelect(map);
                    break;
                case QuizViewModel quiz:
                    vm.EnterMassSelect(quiz);
                    break;
            }
        }

        internal void MoveGroupsUpButton_Click(object sender, RoutedEventArgs e)
            => SwapNotesGroupsAndUngroupedSections(groupsFirst: true);

        internal void MoveGroupsDownButton_Click(object sender, RoutedEventArgs e)
            => SwapNotesGroupsAndUngroupedSections(groupsFirst: false);

        internal void MoveUngroupedUpButton_Click(object sender, RoutedEventArgs e)
            => SwapNotesGroupsAndUngroupedSections(groupsFirst: false);

        internal void MoveUngroupedDownButton_Click(object sender, RoutedEventArgs e)
            => SwapNotesGroupsAndUngroupedSections(groupsFirst: true);

        internal void MoveCalendarUpButton_Click(object sender, RoutedEventArgs e)
            => SwapActiveDashboardCalendarAndDashboard(calendarFirst: true);

        internal void MoveCalendarDownButton_Click(object sender, RoutedEventArgs e)
            => SwapActiveDashboardCalendarAndDashboard(calendarFirst: false);

        internal void MoveDashboardContentUpButton_Click(object sender, RoutedEventArgs e)
            => SwapActiveDashboardCalendarAndDashboard(calendarFirst: false);

        internal void MoveDashboardContentDownButton_Click(object sender, RoutedEventArgs e)
            => SwapActiveDashboardCalendarAndDashboard(calendarFirst: true);

        private void SwapActiveDashboardCalendarAndDashboard(bool calendarFirst)
        {
            if (DataContext is not MainViewModel vm)
                return;

            switch (vm.ActiveDashboard)
            {
                case DashboardFlashcards:
                    SwapFlashcardCalendarAndDashboard(calendarFirst);
                    break;
                case DashboardQuizzes:
                    SwapQuizCalendarAndDashboard(calendarFirst);
                    break;
                case DashboardMindMaps:
                    SwapMindMapCalendarAndDashboard(calendarFirst);
                    break;
                default:
                    SwapNotesCalendarAndDashboard(calendarFirst);
                    break;
            }
        }

        internal void MoveSingleGroupUpButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: NoteGroupViewModel group })
                return;

            if (DataContext is MainViewModel vm)
                TryAnimateGroupMove(
                    GroupsItemsControlElement,
                    vm.NoteGroups,
                    group,
                    moveUp: true,
                    DashboardMotionScope.Notes,
                    () => vm.MoveGroupUp(group));
        }

        internal void MoveSingleGroupDownButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: NoteGroupViewModel group })
                return;

            if (DataContext is MainViewModel vm)
                TryAnimateGroupMove(
                    GroupsItemsControlElement,
                    vm.NoteGroups,
                    group,
                    moveUp: false,
                    DashboardMotionScope.Notes,
                    () => vm.MoveGroupDown(group));
        }

        private void TryAnimateGroupMove<T>(
            ItemsControl? groupsList,
            IList<T> groups,
            T group,
            bool moveUp,
            DashboardMotionScope motionScope,
            Func<bool> applyMove) where T : class
        {
            if (groupsList is null)
            {
                applyMove();
                return;
            }

            var currentIndex = groups.IndexOf(group);
            var swapIndex = moveUp ? currentIndex - 1 : currentIndex + 1;
            if (currentIndex < 0 || swapIndex < 0 || swapIndex >= groups.Count)
                return;

            AnimateGroupPairSwap(
                groupsList,
                group,
                groups[swapIndex],
                motionScope,
                applyMove);
        }

        private void AnimateGroupPairSwap(
            ItemsControl groupsList,
            object primaryItem,
            object secondaryItem,
            DashboardMotionScope motionScope,
            Func<bool> applyMove)
        {
            if (ShouldApplyReorderMotionInstantly())
            {
                applyMove();
                return;
            }

            groupsList.UpdateLayout();

            var capturedBefore = TryResolveGroupContainers(
                groupsList,
                primaryItem,
                secondaryItem,
                out var primaryContainer,
                out var secondaryContainer,
                out var parent);

            if (!capturedBefore || parent is null)
            {
                applyMove();
                return;
            }

            var engine = EnsureDashboardMotionEngine(motionScope);
            engine.BeginLayoutAnimation();

            parent.UpdateLayout();
            var primaryOldY = MeasureTopOffset(primaryContainer, parent);
            var secondaryOldY = MeasureTopOffset(secondaryContainer, parent);

            if (!applyMove())
            {
                engine.EndLayoutAnimation();
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                groupsList.UpdateLayout();
                if (!TryResolveGroupContainers(groupsList, primaryItem, secondaryItem, out var primary, out var secondary, out var panel)
                    || panel is null)
                {
                    engine.EndLayoutAnimation();
                    return;
                }

                RunFlipPairAnimation(
                    primary,
                    secondary,
                    panel,
                    primaryOldY,
                    secondaryOldY,
                    engine,
                    () => ScheduleReflowVirtualizationRefresh(motionScope));
            }, DispatcherPriority.Render);
        }

        private static bool TryResolveGroupContainers(
            ItemsControl groupsList,
            object primaryItem,
            object secondaryItem,
            out FrameworkElement primaryContainer,
            out FrameworkElement secondaryContainer,
            out UIElement? parent)
        {
            primaryContainer = null!;
            secondaryContainer = null!;
            parent = null;

            if (groupsList.ItemContainerGenerator.ContainerFromItem(primaryItem) is not FrameworkElement primary)
                return false;

            if (groupsList.ItemContainerGenerator.ContainerFromItem(secondaryItem) is not FrameworkElement secondary)
                return false;

            parent = VisualTreeHelper.GetParent(primary) as UIElement;
            if (parent is null || !ReferenceEquals(VisualTreeHelper.GetParent(secondary), parent))
                return false;

            primaryContainer = primary;
            secondaryContainer = secondary;
            return true;
        }

        internal void GroupBorder_DragOver(object sender, DragEventArgs e)
        {
            var canDrop = e.Data.GetData(typeof(NoteCardViewModel)) is NoteCardViewModel;
            SetDropZoneBorderVisual(ResolveGroupDropZoneBorder(sender as DependencyObject), canDrop);
            e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        internal void GroupBorder_DragLeave(object sender, DragEventArgs e)
        {
            SetDropZoneBorderVisual(ResolveGroupDropZoneBorder(sender as DependencyObject), false);
            e.Handled = true;
        }

        internal void GroupBorder_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (sender is not Border border || border.Tag is not NoteGroupViewModel targetGroup)
                    return;

                var draggedNote = e.Data.GetData(typeof(NoteCardViewModel)) as NoteCardViewModel;
                if (draggedNote is null)
                    return;

                if (DataContext is MainViewModel vm && vm.TryMoveNoteToGroup(draggedNote, targetGroup))
                    QueueNotesLayoutRefresh();

                e.Handled = true;
            }
            finally
            {
                SetDropZoneBorderVisual(ResolveGroupDropZoneBorder(sender as DependencyObject), false);
            }
        }

        internal void UngroupedDropZone_DragOver(object sender, DragEventArgs e)
        {
            var canDrop = e.Data.GetData(typeof(NoteCardViewModel)) is NoteCardViewModel { IsGrouped: true };
            SetDropZoneBorderVisual(ResolveUngroupedDropZoneBorder(sender as DependencyObject), canDrop);
            e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        internal void UngroupedDropZone_DragLeave(object sender, DragEventArgs e)
        {
            SetDropZoneBorderVisual(ResolveUngroupedDropZoneBorder(sender as DependencyObject), false);
            e.Handled = true;
        }

        internal void UngroupedDropZone_Drop(object sender, DragEventArgs e)
        {
            try
            {
                var draggedNote = e.Data.GetData(typeof(NoteCardViewModel)) as NoteCardViewModel;
                if (draggedNote is null)
                    return;

                if (DataContext is MainViewModel vm)
                    vm.TryDropToUngrouped(draggedNote);

                e.Handled = true;
            }
            finally
            {
                SetDropZoneBorderVisual(ResolveUngroupedDropZoneBorder(sender as DependencyObject), false);
            }
        }

        internal void GroupMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { ContextMenu: { } menu } button)
                UiMotion.OpenContextMenu(menu, button);
        }

        internal void ListingContextMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button menuButton)
                return;

            var cardElement = FindAncestorDashboardListingWithContextMenu(menuButton);
            if (cardElement?.ContextMenu is null)
                return;

            UiMotion.OpenContextMenu(cardElement.ContextMenu, menuButton, PlacementMode.Bottom);
            e.Handled = true;
        }

        internal void DashboardListing_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dashboardListingDragStart = sender is IInputElement inputElement
                ? e.GetPosition(inputElement)
                : e.GetPosition(this);
        }

        internal void DashboardListing_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (DataContext is MainViewModel { IsMassSelectMode: true })
                return;

            if (sender is not FrameworkElement element)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
                return;

            if (IsWithinListingContextMenuButton(e.OriginalSource as DependencyObject))
                return;

            var listing = ResolveDashboardListingFromElement(element);
            if (listing is null)
                return;

            var current = e.GetPosition(element);
            var delta = current - _dashboardListingDragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _suppressDashboardListingOpen = true;
            try
            {
                DashboardDragGhost.Execute(
                    element,
                    CreateDashboardListingDataObject(listing),
                    DragDropEffects.Move,
                    _dashboardListingDragStart);
                e.Handled = true;
            }
            finally
            {
                StopDashboardDragAutoScroll();
            }
        }

        internal void DashboardListing_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_suppressDashboardListingOpen)
            {
                _suppressDashboardListingOpen = false;
                e.Handled = true;
                return;
            }

            if (IsWithinListingContextMenuButton(e.OriginalSource as DependencyObject))
                return;

            if (DashboardMassSelectCheckBoxBehavior.IsMassSelectCheckBox(e.OriginalSource as DependencyObject))
                return;

            var listing = ResolveDashboardListingFromElement(sender as FrameworkElement);
            if (DataContext is MainViewModel { IsMassSelectMode: true } massSelectVm)
            {
                massSelectVm.ToggleMassSelectForDashboardItem(listing);
                if (sender is DependencyObject source)
                    MassSelectHighlightBehavior.RequestVisualSync(source);
                e.Handled = true;
                return;
            }

            switch (listing)
            {
                case FlashcardSetViewModel flashcardSet when DataContext is MainViewModel vm:
                    OpenFlashcardSetEditor(vm, flashcardSet);
                    e.Handled = true;
                    break;
                case MindMapViewModel mindMap:
                    OpenMindMapEditor(mindMap);
                    e.Handled = true;
                    break;
                case QuizViewModel quiz when DataContext is MainViewModel vm:
                    OpenQuizEditor(vm, quiz);
                    e.Handled = true;
                    break;
            }
        }

        internal void DashboardListing_PreviewDragEnter(object sender, DragEventArgs e)
        {
            if (DataContext is MainViewModel { IsMassSelectMode: true })
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var canDrop = CanDropDashboardListing(sender, e);
            if (!DashboardDragGhost.IsListingDropVisualSyncActive)
                SetDashboardListingDropVisual(sender as DependencyObject, canDrop);
            e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        internal void DashboardListing_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (DataContext is MainViewModel { IsMassSelectMode: true })
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var canDrop = CanDropDashboardListing(sender, e);
            if (!DashboardDragGhost.IsListingDropVisualSyncActive)
                SetDashboardListingDropVisual(sender as DependencyObject, canDrop);
            e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        internal void DashboardListing_PreviewDragLeave(object sender, DragEventArgs e)
        {
            if (!DashboardDragGhost.IsListingDropVisualSyncActive)
                SetDashboardListingDropVisual(sender as DependencyObject, false);
            e.Handled = true;
        }

        internal void DashboardListing_PreviewDrop(object sender, DragEventArgs e)
        {
            if (DataContext is MainViewModel { IsMassSelectMode: true })
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            try
            {
                if (sender is not FrameworkElement targetElement)
                    return;

                TryHandleDashboardListingDrop(targetElement.Tag, e, targetElement);
            }
            finally
            {
                SetDashboardListingDropVisual(sender as DependencyObject, false);
            }
        }

        internal void DashboardGroupBorder_PreviewDragOver(object sender, DragEventArgs e)
        {
            var canDrop = CanDropDashboardListingOnGroup(sender, e);
            if (canDrop)
            {
                SetDropZoneBorderVisual(ResolveGroupDropZoneBorder(sender as DependencyObject), true);
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            if (FindDashboardListingElementFromDragEvent(e) is FrameworkElement listingElement
                && CanDropDashboardListing(listingElement, e))
            {
                SetDropZoneBorderVisual(ResolveGroupDropZoneBorder(sender as DependencyObject), false);
                e.Effects = DragDropEffects.Move;
                return;
            }

            SetDropZoneBorderVisual(ResolveGroupDropZoneBorder(sender as DependencyObject), false);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        internal void DashboardGroupBorder_PreviewDragLeave(object sender, DragEventArgs e)
        {
            SetDropZoneBorderVisual(ResolveGroupDropZoneBorder(sender as DependencyObject), false);
            e.Handled = true;
        }

        internal void DashboardGroupBorder_PreviewDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (DataContext is not MainViewModel vm || sender is not FrameworkElement targetElement)
                    return;

                if (FindDashboardListingElementFromDragEvent(e) is FrameworkElement listingElement
                    && CanDropDashboardListing(listingElement, e))
                    return;

                var changed = targetElement.Tag switch
                {
                    FlashcardSetGroupViewModel target when e.Data.GetData(typeof(FlashcardSetViewModel)) is FlashcardSetViewModel dragged
                        => vm.TryMoveFlashcardSetToGroup(dragged, target),
                    MindMapGroupViewModel target when e.Data.GetData(typeof(MindMapViewModel)) is MindMapViewModel dragged
                        => vm.TryMoveMindMapToGroup(dragged, target),
                    QuizGroupViewModel target when e.Data.GetData(typeof(QuizViewModel)) is QuizViewModel dragged
                        => vm.TryMoveQuizToGroup(dragged, target),
                    _ => false
                };

                if (changed)
                {
                    QueueDashboardLayoutRefresh();
                    e.Handled = true;
                }
            }
            finally
            {
                SetDropZoneBorderVisual(ResolveGroupDropZoneBorder(sender as DependencyObject), false);
            }
        }

        internal void DashboardUngroupedDropZone_DragOver(object sender, DragEventArgs e)
        {
            if (DataContext is MainViewModel { IsMassSelectMode: true })
            {
                SetDropZoneBorderVisual(ResolveUngroupedDropZoneBorder(sender as DependencyObject), false);
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var canDrop = CanDropDashboardListingToUngrouped(e);
            if (canDrop)
            {
                SetDropZoneBorderVisual(ResolveUngroupedDropZoneBorder(sender as DependencyObject), true);
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            if (FindDashboardListingElementFromDragEvent(e) is FrameworkElement listingElement
                && CanDropDashboardListing(listingElement, e))
            {
                SetDropZoneBorderVisual(ResolveUngroupedDropZoneBorder(sender as DependencyObject), false);
                e.Effects = DragDropEffects.Move;
                return;
            }

            SetDropZoneBorderVisual(ResolveUngroupedDropZoneBorder(sender as DependencyObject), false);
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        internal void DashboardUngroupedDropZone_DragLeave(object sender, DragEventArgs e)
        {
            SetDropZoneBorderVisual(ResolveUngroupedDropZoneBorder(sender as DependencyObject), false);
            e.Handled = true;
        }

        internal void DashboardUngroupedDropZone_Drop(object sender, DragEventArgs e)
        {
            try
            {
                if (DataContext is MainViewModel { IsMassSelectMode: true })
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }

                if (DataContext is not MainViewModel vm)
                    return;

                if (FindDashboardListingElementFromDragEvent(e) is FrameworkElement listingElement
                    && TryHandleDashboardListingDrop(listingElement.Tag, e, listingElement))
                    return;

                var changed = false;
                switch (e.Data)
                {
                    case IDataObject data when data.GetData(typeof(FlashcardSetViewModel)) is FlashcardSetViewModel { Document.GroupId: not null } flashcardSet:
                        vm.RemoveFlashcardSetFromGroup(flashcardSet);
                        changed = true;
                        e.Handled = true;
                        break;
                    case IDataObject data when data.GetData(typeof(MindMapViewModel)) is MindMapViewModel { Document.GroupId: not null } mindMap:
                        vm.RemoveMindMapFromGroup(mindMap);
                        changed = true;
                        e.Handled = true;
                        break;
                    case IDataObject data when data.GetData(typeof(QuizViewModel)) is QuizViewModel { Document.GroupId: not null } quiz:
                        vm.RemoveQuizFromGroup(quiz);
                        changed = true;
                        e.Handled = true;
                        break;
                }

                if (changed)
                    QueueDashboardLayoutRefresh();
            }
            finally
            {
                SetDropZoneBorderVisual(ResolveUngroupedDropZoneBorder(sender as DependencyObject), false);
            }
        }

        private static bool CanDropDashboardListingToUngrouped(DragEventArgs e)
        {
            return e.Data.GetData(typeof(FlashcardSetViewModel)) is FlashcardSetViewModel { Document.GroupId: not null }
                   || e.Data.GetData(typeof(MindMapViewModel)) is MindMapViewModel { Document.GroupId: not null }
                   || e.Data.GetData(typeof(QuizViewModel)) is QuizViewModel { Document.GroupId: not null };
        }

        private static DataObject CreateDashboardListingDataObject(object listing)
        {
            var data = new DataObject();
            switch (listing)
            {
                case FlashcardSetViewModel flashcardSet:
                    data.SetData(typeof(FlashcardSetViewModel), flashcardSet);
                    break;
                case MindMapViewModel mindMap:
                    data.SetData(typeof(MindMapViewModel), mindMap);
                    break;
                case QuizViewModel quiz:
                    data.SetData(typeof(QuizViewModel), quiz);
                    break;
                default:
                    data.SetData(listing.GetType(), listing);
                    break;
            }

            return data;
        }

        private static object? ResolveDashboardListingFromElement(FrameworkElement? element)
        {
            if (element?.Tag is FlashcardSetViewModel or MindMapViewModel or QuizViewModel)
                return element.Tag;

            return element?.DataContext switch
            {
                FlashcardSetViewModel flashcardSet => flashcardSet,
                MindMapViewModel mindMap => mindMap,
                QuizViewModel quiz => quiz,
                _ => null
            };
        }

        private bool TryHandleDashboardListingDrop(object? target, DragEventArgs e, FrameworkElement? targetElement = null)
        {
            var draggedListing = ResolveDashboardDraggedListing(e.Data);
            var placeAfter = targetElement is not null && e.GetPosition(targetElement).X >= targetElement.ActualWidth / 2;
            var handled = TryApplyDashboardListingDrop(draggedListing, target, placeAfter);

            if (handled)
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }

            return handled;
        }

        private bool TryApplyDashboardListingDrop(object? draggedListing, object? target, bool placeAfter)
        {
            if (DataContext is not MainViewModel vm)
                return false;

            return target switch
            {
                FlashcardSetViewModel flashcardTarget
                    when draggedListing is FlashcardSetViewModel dragged
                         && !ReferenceEquals(dragged, flashcardTarget)
                    => (dragged.Document.GroupId.HasValue && dragged.Document.GroupId == flashcardTarget.Document.GroupId
                        ? vm.TryReorderFlashcardSetsWithinGroup(dragged, flashcardTarget, placeAfter)
                        : vm.TryGroupFlashcardSets(dragged, flashcardTarget)) || true,
                MindMapViewModel mindMapTarget
                    when draggedListing is MindMapViewModel dragged
                         && !ReferenceEquals(dragged, mindMapTarget)
                    => (dragged.Document.GroupId.HasValue && dragged.Document.GroupId == mindMapTarget.Document.GroupId
                        ? vm.TryReorderMindMapsWithinGroup(dragged, mindMapTarget, placeAfter)
                        : vm.TryGroupMindMaps(dragged, mindMapTarget)) || true,
                QuizViewModel quizTarget
                    when draggedListing is QuizViewModel dragged
                         && !ReferenceEquals(dragged, quizTarget)
                    => (dragged.Document.GroupId.HasValue && dragged.Document.GroupId == quizTarget.Document.GroupId
                        ? vm.TryReorderQuizzesWithinGroup(dragged, quizTarget, placeAfter)
                        : vm.TryGroupQuizzes(dragged, quizTarget)) || true,
                _ => false
            };
        }

        private static object? ResolveDashboardDraggedListing(IDataObject data)
        {
            return data.GetData(typeof(FlashcardSetViewModel))
                   ?? data.GetData(typeof(MindMapViewModel))
                   ?? data.GetData(typeof(QuizViewModel));
        }

        private FrameworkElement? FindDashboardListingElementFromDragEvent(DragEventArgs e)
        {
            var hit = VisualTreeHelper.HitTest(this, e.GetPosition(this))?.VisualHit;
            return FindDashboardListingElement(hit)
                   ?? FindDashboardListingElement(e.OriginalSource as DependencyObject);
        }

        private static FrameworkElement? FindDashboardListingElement(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is FrameworkElement { Tag: FlashcardSetViewModel or MindMapViewModel or QuizViewModel } element)
                    return element;

                source = GetElementParent(source);
            }

            return null;
        }

        private static bool CanDropDashboardListingOnGroup(object sender, DragEventArgs e)
        {
            return sender switch
            {
                FrameworkElement { Tag: FlashcardSetGroupViewModel target }
                    => e.Data.GetData(typeof(FlashcardSetViewModel)) is FlashcardSetViewModel dragged
                       && dragged.Document.GroupId != target.GroupId,
                FrameworkElement { Tag: MindMapGroupViewModel target }
                    => e.Data.GetData(typeof(MindMapViewModel)) is MindMapViewModel dragged
                       && dragged.Document.GroupId != target.GroupId,
                FrameworkElement { Tag: QuizGroupViewModel target }
                    => e.Data.GetData(typeof(QuizViewModel)) is QuizViewModel dragged
                       && dragged.Document.GroupId != target.GroupId,
                _ => false
            };
        }

        internal void MoveDashboardGroupUpButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || sender is not Button { Tag: object group })
                return;

            switch (group)
            {
                case FlashcardSetGroupViewModel flashcardGroup:
                    TryAnimateGroupMove(
                        FlashcardDashboardGroupsItemsControlElement,
                        vm.FlashcardSetGroups,
                        flashcardGroup,
                        moveUp: true,
                        DashboardMotionScope.Flashcards,
                        () => vm.MoveFlashcardSetGroupUp(flashcardGroup));
                    break;
                case MindMapGroupViewModel mindMapGroup:
                    TryAnimateGroupMove(
                        MindMapDashboardGroupsItemsControlElement,
                        vm.MindMapGroups,
                        mindMapGroup,
                        moveUp: true,
                        DashboardMotionScope.MindMaps,
                        () => vm.MoveMindMapGroupUp(mindMapGroup));
                    break;
                case QuizGroupViewModel quizGroup:
                    TryAnimateGroupMove(
                        QuizDashboardGroupsItemsControlElement,
                        vm.QuizGroups,
                        quizGroup,
                        moveUp: true,
                        DashboardMotionScope.Quizzes,
                        () => vm.MoveQuizGroupUp(quizGroup));
                    break;
            }
        }

        internal void MoveDashboardGroupDownButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MainViewModel vm || sender is not Button { Tag: object group })
                return;

            switch (group)
            {
                case FlashcardSetGroupViewModel flashcardGroup:
                    TryAnimateGroupMove(
                        FlashcardDashboardGroupsItemsControlElement,
                        vm.FlashcardSetGroups,
                        flashcardGroup,
                        moveUp: false,
                        DashboardMotionScope.Flashcards,
                        () => vm.MoveFlashcardSetGroupDown(flashcardGroup));
                    break;
                case MindMapGroupViewModel mindMapGroup:
                    TryAnimateGroupMove(
                        MindMapDashboardGroupsItemsControlElement,
                        vm.MindMapGroups,
                        mindMapGroup,
                        moveUp: false,
                        DashboardMotionScope.MindMaps,
                        () => vm.MoveMindMapGroupDown(mindMapGroup));
                    break;
                case QuizGroupViewModel quizGroup:
                    TryAnimateGroupMove(
                        QuizDashboardGroupsItemsControlElement,
                        vm.QuizGroups,
                        quizGroup,
                        moveUp: false,
                        DashboardMotionScope.Quizzes,
                        () => vm.MoveQuizGroupDown(quizGroup));
                    break;
            }
        }

        internal void DashboardGroupColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { ContextMenu: { } menu } button)
                UiMotion.OpenContextMenu(menu, button);
        }

        internal void DashboardGroupMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { ContextMenu: { } menu } button)
                UiMotion.OpenContextMenu(menu, button);
        }

        internal void DashboardGroupColorMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string colorHex } menuItem || DataContext is not MainViewModel vm)
                return;

            var group = ResolveDashboardGroupFromMenuItem(menuItem);
            switch (group)
            {
                case FlashcardSetGroupViewModel flashcardGroup:
                    vm.SetFlashcardSetGroupBackgroundColor(flashcardGroup, colorHex);
                    break;
                case MindMapGroupViewModel mindMapGroup:
                    vm.SetMindMapGroupBackgroundColor(mindMapGroup, colorHex);
                    break;
                case QuizGroupViewModel quizGroup:
                    vm.SetQuizGroupBackgroundColor(quizGroup, colorHex);
                    break;
            }
        }

        internal void RenameDashboardGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || DataContext is not MainViewModel vm)
                return;

            var group = ResolveDashboardGroupFromMenuItem(menuItem);
            var currentName = group switch
            {
                FlashcardSetGroupViewModel flashcardGroup => flashcardGroup.Name,
                MindMapGroupViewModel mindMapGroup => mindMapGroup.Name,
                QuizGroupViewModel quizGroup => quizGroup.Name,
                _ => null
            };

            if (currentName is null)
                return;

            var dialog = new SimpleInputDialog(
                LocalizationService.GetString("RenameGroup"),
                LocalizationService.GetString("RenameGroupPrompt"),
                currentName)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
                return;

            switch (group)
            {
                case FlashcardSetGroupViewModel flashcardGroup:
                    vm.RenameFlashcardSetGroup(flashcardGroup, dialog.InputText);
                    break;
                case MindMapGroupViewModel mindMapGroup:
                    vm.RenameMindMapGroup(mindMapGroup, dialog.InputText);
                    break;
                case QuizGroupViewModel quizGroup:
                    vm.RenameQuizGroup(quizGroup, dialog.InputText);
                    break;
            }
        }

        internal void DisbandDashboardGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || DataContext is not MainViewModel vm)
                return;

            var group = ResolveDashboardGroupFromMenuItem(menuItem);
            if (group is null)
                return;

            var result = ModernDialog.Show(
                this,
                LocalizationService.GetString("DisbandGroup"),
                LocalizationService.GetString("DisbandGroupPrompt"),
                ModernDialogTone.Warning,
                LocalizationService.GetString("DeleteGroupItems"),
                LocalizationService.GetString("Cancel"),
                LocalizationService.GetString("KeepItemsUngrouped"),
                primaryStyle: ModernDialogButtonStyle.Danger,
                secondaryStyle: ModernDialogButtonStyle.Warning);

            if (result == ModernDialogResult.Cancel || result == ModernDialogResult.None)
                return;

            var deleteItems = result == ModernDialogResult.Primary;
            switch (group)
            {
                case FlashcardSetGroupViewModel flashcardGroup:
                    vm.DisbandFlashcardSetGroup(flashcardGroup, deleteItems);
                    break;
                case MindMapGroupViewModel mindMapGroup:
                    vm.DisbandMindMapGroup(mindMapGroup, deleteItems);
                    break;
                case QuizGroupViewModel quizGroup:
                    vm.DisbandQuizGroup(quizGroup, deleteItems);
                    break;
            }
        }

        private static object? ResolveDashboardGroupFromMenuItem(MenuItem menuItem)
        {
            DependencyObject? current = menuItem;
            ContextMenu? contextMenu = null;

            while (current != null)
            {
                if (current is ContextMenu cm)
                {
                    contextMenu = cm;
                    break;
                }

                current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
            }

            return (contextMenu?.PlacementTarget as FrameworkElement)?.DataContext
                ?? (contextMenu?.PlacementTarget as Button)?.Tag;
        }

        private static bool CanDropDashboardListing(object sender, DragEventArgs e)
        {
            return sender switch
            {
                FrameworkElement { Tag: FlashcardSetViewModel target }
                    => e.Data.GetData(typeof(FlashcardSetViewModel)) is FlashcardSetViewModel dragged && !ReferenceEquals(dragged, target),
                FrameworkElement { Tag: MindMapViewModel target }
                    => e.Data.GetData(typeof(MindMapViewModel)) is MindMapViewModel dragged && !ReferenceEquals(dragged, target),
                FrameworkElement { Tag: QuizViewModel target }
                    => e.Data.GetData(typeof(QuizViewModel)) is QuizViewModel dragged && !ReferenceEquals(dragged, target),
                _ => false
            };
        }

        private void SetDashboardListingDropVisual(DependencyObject? source, bool isActive)
        {
            if (source is not FrameworkElement listingElement)
                return;

            if (!isActive)
            {
                if (_listingDropGlowHost?.TryGetTarget(out var host) == true
                    && ReferenceEquals(host, listingElement))
                {
                    ClearListingDropGlow();
                }

                return;
            }

            if (_listingDropGlowHost?.TryGetTarget(out var current) == true
                && ReferenceEquals(current, listingElement))
            {
                return;
            }

            ClearListingDropGlow();

            if (EnsureListingDropGlowOverlay(listingElement) is { } glow)
            {
                DashboardDropGlowVisual.AnimateToActive(glow);
                _listingDropGlowHost = new WeakReference<FrameworkElement>(listingElement);
            }
        }

        private void ClearListingDropGlow()
        {
            if (_listingDropGlowHost?.TryGetTarget(out var host) == true
                && FindListingDropGlowOverlay(host) is { } glow)
            {
                DashboardDropGlowVisual.Reset(glow);
            }

            _listingDropGlowHost = null;
        }

        private static Border? EnsureListingDropGlowOverlay(FrameworkElement listingElement)
        {
            UnwrapLegacyListingGlowShell(listingElement);

            var existing = FindListingDropGlowOverlay(listingElement);
            if (existing is not null && IsListingGlowOverlayHosted(existing))
                return existing;

            if (existing is not null)
            {
                DashboardDropGlowVisual.Reset(existing);
                if (existing.Parent is Panel panel)
                    panel.Children.Remove(existing);
            }

            var cardBorder = FindDashboardListingCardBorder(listingElement);
            if (cardBorder is null)
                return null;

            return TryCreateCardBorderListingGlow(cardBorder);
        }

        private static Border? TryCreateCardBorderListingGlow(Border cardBorder)
        {
            if (cardBorder.Child is Grid { Tag: var hostTag } existingHost
                && string.Equals(hostTag as string, DashboardDropGlowVisual.ListingGlowOverlayHostTag, StringComparison.Ordinal)
                && FindListingDropGlowInTree(existingHost) is { } hostedGlow)
            {
                return hostedGlow;
            }

            var content = cardBorder.Child;
            if (content is null)
                return null;

            var host = new Grid
            {
                Tag = DashboardDropGlowVisual.ListingGlowOverlayHostTag
            };

            cardBorder.Child = null;
            host.Children.Add(content);

            var padding = cardBorder.Padding;
            var glow = CreateListingDropGlowBorder(cardBorder);
            glow.Margin = new Thickness(-padding.Left, -padding.Top, -padding.Right, -padding.Bottom);
            glow.HorizontalAlignment = HorizontalAlignment.Stretch;
            glow.VerticalAlignment = VerticalAlignment.Stretch;
            host.Children.Add(glow);

            cardBorder.Child = host;
            return glow;
        }

        private static void UnwrapLegacyListingGlowShell(FrameworkElement listingElement)
        {
            if (listingElement is not Border outerBorder
                || outerBorder.Child is not Grid { Tag: var tag } host
                || !string.Equals(tag as string, "DashboardListingDropGlowHost", StringComparison.Ordinal))
            {
                return;
            }

            Border? innerCard = null;
            foreach (var child in host.Children)
            {
                if (child is Border { Tag: var childTag } border
                    && !string.Equals(childTag as string, DashboardDropGlowVisual.ListingGlowTag, StringComparison.Ordinal))
                {
                    innerCard = border;
                    break;
                }
            }

            if (innerCard is null)
                return;

            if (FindListingDropGlowInTree(host) is { } glow)
            {
                DashboardDropGlowVisual.Reset(glow);
                host.Children.Remove(glow);
            }

            var margin = innerCard.Margin;
            outerBorder.Padding = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom);
            innerCard.Margin = new Thickness(0);
            outerBorder.Child = innerCard;
        }

        private static Border CreateListingDropGlowBorder(Border cardBorder)
        {
            var glow = new Border
            {
                Tag = DashboardDropGlowVisual.ListingGlowTag,
                Background = Brushes.Transparent,
                BorderBrush = DashboardDropGlowVisual.DropBorderBrush,
                BorderThickness = new Thickness(0),
                CornerRadius = cardBorder.CornerRadius,
                Opacity = 0,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(glow, 50);
            return glow;
        }

        private static Border? FindListingDropGlowOverlay(FrameworkElement listingElement)
            => FindListingDropGlowInTree(listingElement);

        private static Border? FindListingDropGlowInTree(DependencyObject root)
        {
            if (root is Border { Tag: var tag } border
                && string.Equals(tag as string, DashboardDropGlowVisual.ListingGlowTag, StringComparison.Ordinal))
            {
                return border;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                if (FindListingDropGlowInTree(VisualTreeHelper.GetChild(root, i)) is { } glow)
                    return glow;
            }

            return null;
        }

        private static bool IsListingGlowOverlayHosted(Border glow)
        {
            return VisualTreeHelper.GetParent(glow) is Grid { Tag: var tag }
                && string.Equals(tag as string, DashboardDropGlowVisual.ListingGlowOverlayHostTag, StringComparison.Ordinal);
        }

        internal void ClearAllDragDropVisuals()
        {
            ClearDashboardListingDropVisuals();
            ClearAllDropZoneBorderVisuals();
            NoteCardControl.ClearDropGlowOwner();
        }

        private void SetDropZoneBorderVisual(Border? border, bool isActive)
        {
            if (border is null)
                return;

            if (isActive)
            {
                if (_dropZoneOriginalBorderBrushes.ContainsKey(border))
                    return;

                foreach (var activeBorder in _dropZoneOriginalBorderBrushes.Keys.ToList())
                {
                    if (!ReferenceEquals(activeBorder, border))
                        RestoreDropZoneBorderVisual(activeBorder);
                }

                if (!_dropZoneOriginalBorderBrushes.ContainsKey(border))
                {
                    _dropZoneOriginalBorderBrushes[border] = border.BorderBrush;
                    _dropZoneOriginalBorderThicknesses[border] = border.BorderThickness;
                    _dropZoneOriginalCornerRadius[border] = border.CornerRadius;
                    _dropZoneOriginalBackgrounds[border] = border.Background;
                }

                border.BorderBrush = GetDashboardDropBrush();
                if (IsUngroupedDropZoneBorder(border))
                {
                    border.CornerRadius = new CornerRadius(UngroupedDropZoneCornerRadius);
                    border.Background = GetUngroupedDropFillBrush();
                }
                else if (border.BorderThickness.Left < 0.5)
                    border.CornerRadius = new CornerRadius(12);

                AnimateBorderThickness(border, new Thickness(2), 150);
                return;
            }

            RestoreDropZoneBorderVisual(border, animate: true);
        }

        private void RestoreDropZoneBorderVisual(Border border, bool animate = false)
        {
            if (!_dropZoneOriginalBorderBrushes.TryGetValue(border, out var originalBrush)
                || !_dropZoneOriginalBorderThicknesses.TryGetValue(border, out var originalThickness))
                return;

            UiMotion.Stop(border);
            border.BorderBrush = originalBrush;
            border.CornerRadius = _dropZoneOriginalCornerRadius.TryGetValue(border, out var originalRadius)
                ? originalRadius
                : border.CornerRadius;
            border.Background = _dropZoneOriginalBackgrounds.TryGetValue(border, out var originalBackground)
                ? originalBackground
                : border.Background;

            if (animate)
                AnimateBorderThickness(border, originalThickness, 150);
            else
                border.BorderThickness = originalThickness;

            _dropZoneOriginalBorderBrushes.Remove(border);
            _dropZoneOriginalBorderThicknesses.Remove(border);
            _dropZoneOriginalCornerRadius.Remove(border);
            _dropZoneOriginalBackgrounds.Remove(border);
        }

        private static bool IsUngroupedDropZoneBorder(Border border)
            => border.Name is "UngroupedSectionContainer"
                or "FlashcardDashboardUngroupedSectionContainer"
                or "QuizDashboardUngroupedSectionContainer"
                or "MindMapDashboardUngroupedSectionContainer";

        private Brush GetUngroupedDropFillBrush()
        {
            if (TryFindResource("SelectionBackground") is SolidColorBrush selection)
            {
                var fill = Color.FromArgb(48, selection.Color.R, selection.Color.G, selection.Color.B);
                return new SolidColorBrush(fill);
            }

            return new SolidColorBrush(Color.FromArgb(48, 63, 111, 232));
        }

        private static Border? ResolveGroupDropZoneBorder(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Border border
                    && border.Tag is NoteGroupViewModel or FlashcardSetGroupViewModel or MindMapGroupViewModel or QuizGroupViewModel)
                    return border;

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private static Border? ResolveUngroupedDropZoneBorder(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Border { Name: var name } border
                    && name is "UngroupedSectionContainer"
                        or "FlashcardDashboardUngroupedSectionContainer"
                        or "QuizDashboardUngroupedSectionContainer"
                        or "MindMapDashboardUngroupedSectionContainer")
                    return border;

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private Brush GetDashboardDropBrush()
        {
            return TryFindResource("NoteCardSelectionBorder") as Brush
                ?? TryFindResource("MindMapSelectedBorderBrush") as Brush
                ?? new SolidColorBrush(Color.FromRgb(74, 110, 224));
        }

        private static Border? FindDashboardListingCardBorder(DependencyObject source)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
            {
                var child = VisualTreeHelper.GetChild(source, i);
                if (child is Border border
                    && border.BorderThickness.Left > 0
                    && border.CornerRadius.TopLeft >= 10
                    && border.Padding.Left >= 8)
                    return border;

                var nested = FindDashboardListingCardBorder(child);
                if (nested is not null)
                    return nested;
            }

            return null;
        }

        private static void AnimateBorderThickness(Border border, Thickness to, int durationMs)
        {
            UiMotion.AnimateThickness(border, border.BorderThickness, to, durationMs);
        }

        private static bool IsWithinListingContextMenuButton(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Button button && string.Equals(button.Name, "ListingContextMenuButton", StringComparison.Ordinal))
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private static FrameworkElement? FindAncestorDashboardListingWithContextMenu(DependencyObject source)
        {
            var current = GetElementParent(source);
            while (current != null)
            {
                if (current is FrameworkElement { ContextMenu: not null, Tag: FlashcardSetViewModel or MindMapViewModel or QuizViewModel } element)
                    return element;

                current = GetElementParent(current);
            }

            return null;
        }

        private static DependencyObject? GetElementParent(DependencyObject source)
        {
            return source is FrameworkElement frameworkElement && frameworkElement.Parent is not null
                ? frameworkElement.Parent
                : VisualTreeHelper.GetParent(source);
        }

        internal void GroupColorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { ContextMenu: { } menu } button)
                UiMotion.OpenContextMenu(menu, button);
        }

        internal void RenameGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var group = ResolveGroupFromMenuSender(sender);
            if (group is null || DataContext is not MainViewModel vm)
                return;

            var dialog = new SimpleInputDialog(
                LocalizationService.GetString("RenameGroup"),
                LocalizationService.GetString("RenameGroupPrompt"),
                group.Name)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true)
                vm.RenameGroup(group, dialog.InputText);
        }

        internal void GroupColorMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.Tag is not string colorHex)
                return;

            var group = ResolveGroupFromMenuSender(sender);
            if (group is null || DataContext is not MainViewModel vm)
                return;

            vm.SetGroupBackgroundColor(group, colorHex);
        }

        internal void DisbandGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var group = ResolveGroupFromMenuSender(sender);
            if (group is null || DataContext is not MainViewModel vm)
                return;

            var result = ModernDialog.Show(
                this,
                LocalizationService.GetString("DisbandGroup"),
                LocalizationService.GetString("DisbandGroupPrompt"),
                ModernDialogTone.Warning,
                LocalizationService.GetString("DeleteGroupNotes"),
                LocalizationService.GetString("Cancel"),
                LocalizationService.GetString("KeepNotesUngrouped"),
                primaryStyle: ModernDialogButtonStyle.Danger,
                secondaryStyle: ModernDialogButtonStyle.Warning);

            if (result == ModernDialogResult.Cancel || result == ModernDialogResult.None)
                return;

            vm.DisbandGroup(group, deleteNotes: result == ModernDialogResult.Primary);
        }

        private static NoteGroupViewModel? ResolveGroupFromMenuSender(object sender)
        {
            if (sender is not MenuItem menuItem)
                return null;

            DependencyObject? current = menuItem;
            ContextMenu? contextMenu = null;

            while (current != null)
            {
                if (current is ContextMenu cm)
                {
                    contextMenu = cm;
                    break;
                }

                current = LogicalTreeHelper.GetParent(current) ?? VisualTreeHelper.GetParent(current);
            }

            if (contextMenu?.PlacementTarget is not FrameworkElement placementTarget)
                return null;

            return placementTarget.DataContext as NoteGroupViewModel
                ?? (placementTarget as Button)?.Tag as NoteGroupViewModel;
        }

        internal void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            CollapseTopSearchPanel();
            UiMotion.ClosePopup(SortNotesPopupElement);
            UiMotion.ClosePopup(TagsFilterPopup);

            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = LocalizationService.GetString("SupportedFormats"),
                Title = LocalizationService.GetString("SelectFilesToImport")
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            try
            {
                int importedCount = 0;

                if (DataContext is MainViewModel vm)
                {
                    foreach (var filePath in openFileDialog.FileNames)
                    {
                        try
                        {
                            NoteDocument newDocument;
                            if (NoteFileService.IsNotePackagePath(filePath))
                            {
                                newDocument = NoteFileService.LoadNotePackage(filePath);
                            }
                            else
                            {
                                string content = NoteFileService.LoadEditorContentFromFile(filePath);
                                if (string.IsNullOrWhiteSpace(content))
                                    continue;

                                var settings = AppSettingsService.Load();
                                var preferredFontFamily = "Calibri";
                                var preferredFontSize = settings.PreferredFontSize > 0
                                    ? settings.PreferredFontSize
                                    : 14;

                                newDocument = new NoteDocument
                                {
                                    Title = Path.GetFileNameWithoutExtension(filePath),
                                    Content = content,
                                    Tags = new List<string>(),
                                    FontFamily = preferredFontFamily,
                                    FontSize = preferredFontSize
                                };
                            }

                            // Add to view model
                            vm.AddNoteFromDocument(newDocument);
                            importedCount++;
                        }
                        catch (Exception ex)
                        {
                            ModernMessageBox.Show(
                                $"{LocalizationService.GetString("ImportError")}\n{Path.GetFileName(filePath)}\n\n{ex.Message}",
                                LocalizationService.GetString("Error"),
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        }
                    }

                    if (importedCount > 0)
                    {
                        string message = string.Format(
                            LocalizationService.GetString("ImportSuccess"),
                            importedCount);
                        ModernMessageBox.Show(
                            message,
                            LocalizationService.GetString("ImportNotes"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show(
                    $"{LocalizationService.GetString("ImportError")}\n\n{ex.Message}",
                    LocalizationService.GetString("Error"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        internal void ImportMindMapButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = LocalizationService.GetString("MindMapImportDialogFilter"),
                Title = LocalizationService.GetString("MindMapImportDialogTitle")
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            if (DataContext is not MainViewModel vm)
                return;

            var imported = new List<MindMapDocument>();
            foreach (var filePath in openFileDialog.FileNames)
            {
                try
                {
                    var document = MindMapFileService.LoadAnyDocument(filePath, Path.GetFileNameWithoutExtension(filePath));
                    imported.Add(document);
                }
                catch (Exception ex)
                {
                    ModernMessageBox.Show(
                        $"{LocalizationService.GetString("ImportError")}\n{Path.GetFileName(filePath)}\n\n{ex.Message}",
                        LocalizationService.GetString("Error"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            if (imported.Count == 0)
                return;

            // Single batch add so a multi-file import pays one save, not one per file - see
            // MainViewModel.AddImportedMindMaps for why looping AddOrUpdateMindMap here would not
            // get the same protection Notes' import gets from QueueSaveNotes' debounce.
            vm.AddImportedMindMaps(imported);

            ModernMessageBox.Show(
                string.Format(LocalizationService.GetString("MindMapImportSuccessCount"), imported.Count),
                LocalizationService.GetString("MindMapImport"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        internal void ImportQuizButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = LocalizationService.GetString("QuizImportDialogFilter"),
                Title = LocalizationService.GetString("QuizImportDialogTitle")
            };

            if (openFileDialog.ShowDialog() != true)
                return;

            if (DataContext is not MainViewModel vm)
                return;

            // Ask once for the whole batch, and only if at least one selected file could actually
            // carry progress data - GIFT/CSV can never represent it, so they never trigger this.
            var keepProgress = false;
            var progressCapableFiles = openFileDialog.FileNames
                .Where(path => QuizFileService.IsScribQuizPath(path) || QuizFileService.IsLegacyJsonPath(path))
                .ToList();

            if (progressCapableFiles.Count > 0)
            {
                var anyHasProgress = progressCapableFiles.Any(path =>
                {
                    try
                    {
                        return QuizFileService.HasProgressData(QuizFileService.LoadScribQuizDocument(path));
                    }
                    catch
                    {
                        return false;
                    }
                });

                if (anyHasProgress)
                {
                    var choice = ModernDialog.Show(
                        this,
                        LocalizationService.GetString("QuizImportKeepProgressTitle"),
                        LocalizationService.GetString("QuizImportKeepProgressPrompt"),
                        ModernDialogTone.Question,
                        LocalizationService.GetString("QuizImportKeepProgressKeep"),
                        LocalizationService.GetString("Cancel"),
                        LocalizationService.GetString("QuizImportKeepProgressDiscard"),
                        primaryStyle: ModernDialogButtonStyle.Secondary,
                        secondaryStyle: ModernDialogButtonStyle.Primary,
                        defaultChoice: ModernDialogResult.Secondary);

                    switch (choice)
                    {
                        case ModernDialogResult.Primary:
                            keepProgress = true;
                            break;
                        case ModernDialogResult.Secondary:
                            keepProgress = false;
                            break;
                        default:
                            return;
                    }
                }
            }

            var imported = new List<QuizDocument>();
            var warnings = new List<string>();
            foreach (var filePath in openFileDialog.FileNames)
            {
                try
                {
                    var document = QuizFileService.LoadAnyQuizDocument(
                        filePath,
                        Path.GetFileNameWithoutExtension(filePath),
                        keepProgress,
                        out var fileWarnings);
                    imported.Add(document);
                    warnings.AddRange(fileWarnings.Select(warning => $"{Path.GetFileName(filePath)}: {warning}"));
                }
                catch (Exception ex)
                {
                    ModernMessageBox.Show(
                        $"{LocalizationService.GetString("ImportError")}\n{Path.GetFileName(filePath)}\n\n{ex.Message}",
                        LocalizationService.GetString("Error"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            if (imported.Count == 0)
                return;

            // Single batch add so a multi-file import pays one save, not one per file - see
            // MainViewModel.AddImportedQuizzes for the same reasoning AddImportedMindMaps documents.
            vm.AddImportedQuizzes(imported);

            var summary = string.Format(LocalizationService.GetString("QuizImportSuccessCount"), imported.Count);
            if (warnings.Count > 0)
                summary += "\n\n" + string.Join("\n", warnings);

            ModernMessageBox.Show(
                summary,
                LocalizationService.GetString("QuizImport"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private MindMapViewModel? GetMindMapFromMenuSender(object sender)
        {
            if (sender is not MenuItem menuItem) return null;
            var contextMenu = menuItem.Parent as ContextMenu;
            var button = contextMenu?.PlacementTarget as Button;
            return button?.Tag as MindMapViewModel;
        }

        private MindMapGroupViewModel? GetMindMapGroupFromMenuSender(object sender)
        {
            if (sender is not MenuItem menuItem) return null;
            var contextMenu = menuItem.Parent as ContextMenu;
            var button = contextMenu?.PlacementTarget as Button;
            return button?.Tag as MindMapGroupViewModel;
        }

        private void RenameMindMapGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var group = GetMindMapGroupFromMenuSender(sender);
            if (group is null) return;
            if (DataContext is not MainViewModel vm) return;

            var dialog = new SimpleInputDialog("Rename set", "Enter a new name:", group.Name) { Owner = this };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
                vm.RenameMindMapGroup(group, dialog.InputText!);
        }

        private void DeleteMindMapGroupMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var group = GetMindMapGroupFromMenuSender(sender);
            if (group is null) return;
            if (DataContext is not MainViewModel vm) return;

            var confirmed = ModernDialog.ConfirmDanger(
                this,
                "Delete set",
                $"Delete the set \"{group.Name}\"? Mind maps inside will not be deleted.",
                LocalizationService.GetString("Confirm"));

            if (confirmed)
                vm.DeleteMindMapGroup(group);
        }

    }
}
