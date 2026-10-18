using Scriblucent.Animations;
using Scriblucent.Icons;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using Scriblucent.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Scriblucent;

public partial class EditorWorkspaceWindow : Window
{
    private sealed class EditorTabState
    {
        public required EditorSession Session { get; init; }
        public EditorDocumentKey DocumentId => Session.Key;
        public required TabItem TabItem { get; init; }
        public UIElement HostedContent => Session.Content;
        public required TextBlock TitleTextBlock { get; init; }
        public required Button CloseButton { get; init; }
        public required Action StateHandler { get; init; }
        public required RoutedEventHandler CloseClickHandler { get; init; }
    }
    private readonly Dictionary<EditorDocumentKey, EditorTabState> _tabsByDocumentId = new();
    internal IReadOnlyList<EditorSession> Sessions => _tabsByDocumentId.Values.Select(state => state.Session).ToArray();
    private readonly System.Windows.Threading.DispatcherTimer _autoSaveTimer;
    private DateTime _nextAutoSave = DateTime.UtcNow;
    private int _autoSaveInterval;
    private bool _autoSaveEnabled;
    private bool _saving;
    private bool _autoSaving;
    private bool _preflighting;
    private EditorSession? _activeSession;
    private readonly HashSet<EditorSession> _configuredPopups = new();
    public bool IsCloseApproved { get; private set; }
    public event Action<EditorDocumentKind>? CreateRequested;
    private bool _isWindowClosing;
    private bool _skipCloseConfirmation;
    private TabItem? _dragTab;
    private bool _isDraggingTab;
    private bool _reorderingTabs;
    private double _grabOffsetInTab;
    private TranslateTransform? _dragTransform;
    private int _dragRestoreZIndex;
    private int _dragFromIndex;
    private int _dragToIndex;
    private double _dragTabWidth;

    public EditorWorkspaceWindow()
    {
        InitializeComponent();
        WindowThemeService.Register(this);
        PreviewKeyDown += Workspace_PreviewKeyDown;
        EditorTabs.SelectionChanged += (_, _) => { if (!_isDraggingTab && !_reorderingTabs) ShowSelectedEditor(IsLoaded); };
        Loaded += (_, _) => UiMotion.ConfigurePopupsForWindow(this);
        Closed += (_, _) =>
        {
            _autoSaveTimer?.Stop();
            foreach (var state in _tabsByDocumentId.Values.ToArray()) state.Session.Dispose();
            _tabsByDocumentId.Clear();
        };
        Closing += Window_Closing;
        WindowContentEnterHelper.AnimateOnLoaded(this, "EditorTabs", FinalizeWindowClose);
        _autoSaveTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            { Interval = TimeSpan.FromSeconds(5) };
        _autoSaveTimer.Tick += AutoSaveTick;
        var settings = AppSettingsService.Load();
        _autoSaveInterval = Math.Clamp(settings.AutoSaveIntervalSeconds, 5, 86400);
        _autoSaveEnabled = settings.EnableAutoSave;
        _nextAutoSave = DateTime.UtcNow.AddSeconds(_autoSaveInterval);
        _autoSaveTimer.Start();
    }

    public bool FocusDocument(EditorDocumentKey key)
    {
        if (!_tabsByDocumentId.TryGetValue(key, out var state)) return false;
        EditorTabs.SelectedItem = state.TabItem;
        ShowHostedContent(state, false);
        return true;
    }

    public void OpenOrFocusNote(NoteCardViewModel note, object? sharedDataContext, bool animateContent = true)
    {
        var key = new EditorDocumentKey(EditorDocumentKind.Note, note.Document.Id);
        if (FocusDocument(key)) return;
        var editor = new NoteEditorWindow { DataContext = sharedDataContext };
        editor.EnableTabMode();
        var working = EditorHosting.Clone(note.Document);
        editor.LoadFromDocument(working);
        editor.SetCurrentDocument(working);
        EditorSession? session = null;
        var content = editor.AttachToWorkspace(this, () => session is not null && SaveSession(session, true), () => CloseDocument(key));
        session = new EditorSession
        {
            Key = key, Editor = editor, Content = content,
            Save = () =>
            {
                if (sharedDataContext is not MainViewModel vm || !vm.CommitEditorNote(note, editor.CaptureWorkspaceDocument())) return false;
                editor.MarkWorkspaceSaved();
                return true;
            }
        };
        AddSession(session, animateContent);
    }

    internal void AddSession(EditorSession session, bool animate = true)
    {
        var icon = new AppIcon { Kind = session.Icon, Style = (Style)FindResource("EditorTabIconStyle") };
        var title = new TextBlock { Style = (Style)FindResource("EditorTabTitleStyle") };
        var close = new Button { Style = (Style)FindResource("EditorTabCloseButtonStyle"),
            Content = new AppIcon { Kind = AppIconKind.Close, Width = 10, Height = 10 } };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(icon); panel.Children.Add(title); panel.Children.Add(close);
        var tab = new TabItem { Header = panel };
        tab.PreviewMouseLeftButtonDown += TabItem_PreviewMouseLeftButtonDown;
        tab.PreviewMouseMove += TabItem_PreviewMouseMove;
        tab.PreviewMouseLeftButtonUp += TabItem_PreviewMouseLeftButtonUp;
        tab.LostMouseCapture += TabItem_LostMouseCapture;
        void Update()
        {
            var text = string.IsNullOrWhiteSpace(session.Editor.WorkspaceTitle) ? Localization.LocalizationService.GetString("Untitled") : session.Editor.WorkspaceTitle;
            title.Text = text + (session.IsDirty ? "  •" : "");
            tab.ToolTip = text;
            System.Windows.Automation.AutomationProperties.SetName(tab, text);
        }
        Action update = Update;
        session.Editor.WorkspaceStateChanged += update;
        RoutedEventHandler closeHandler = (_, e) => { e.Handled = true; CloseTab(session.Key); };
        close.Click += closeHandler;
        var state = new EditorTabState { Session = session, TabItem = tab, TitleTextBlock = title, CloseButton = close,
            StateHandler = update, CloseClickHandler = closeHandler };
        _tabsByDocumentId.Add(session.Key, state);
        Update();
        void Insert()
        {
            _reorderingTabs = true;
            try { EditorTabs.Items.Add(tab); EditorTabs.SelectedItem = tab; }
            finally { _reorderingTabs = false; }
            ShowHostedContent(state, animate && IsLoaded);
        }
        if (IsLoaded) Insert();
        else { RoutedEventHandler? loaded = null; loaded = (_, _) => { Loaded -= loaded; Insert(); }; Loaded += loaded; }
    }

    internal bool SaveSession(EditorSession session, bool manual)
    {
        if (_saving || session.IsDisposed || IsCloseApproved) return false;
        _saving = true;
        var wasHitTestVisible = EditorTabs.IsHitTestVisible;
        EditorTabs.IsHitTestVisible = false;
        bool saved;
        string? failure = null;
        try { session.Editor.CommitWorkspaceEdits(); saved = session.Save(); }
        catch (Exception ex)
        {
            saved = false;
            failure = ex.Message;
        }
        finally { _saving = false; EditorTabs.IsHitTestVisible = wasHitTestVisible; }
        if (saved) session.IsDraft = false;
        else session.Editor.ReportWorkspaceSaveFailure();
        if (_tabsByDocumentId.TryGetValue(session.Key, out var state)) state.StateHandler();
        if (!saved && manual) ModernDialog.ShowError(this, Localization.LocalizationService.GetString("Error"),
            failure ?? Localization.LocalizationService.GetString("WorkspaceSaveFailed"));
        return saved;
    }

    private async void AutoSaveTick(object? sender, EventArgs e)
    {
        if (_saving || _autoSaving || _preflighting || IsCloseApproved) return;
        var settings = AppSettingsService.Load();
        var interval = Math.Clamp(settings.AutoSaveIntervalSeconds, 5, 86400);
        if (_autoSaveInterval != interval || _autoSaveEnabled != settings.EnableAutoSave)
        {
            _autoSaveInterval = interval; _autoSaveEnabled = settings.EnableAutoSave;
            _nextAutoSave = DateTime.UtcNow.AddSeconds(interval);
        }
        if (!settings.EnableAutoSave) return;
        if (DateTime.UtcNow < _nextAutoSave) return;
        _nextAutoSave = DateTime.UtcNow.AddSeconds(interval);
        _autoSaving = true;
        try
        {
            foreach (var state in _tabsByDocumentId.Values.ToArray())
            {
                if (IsCloseApproved || _preflighting || _saving) break;
                if (state.Session.IsDisposed) continue;
                if (state.Session.IsDraft || state.Session.Editor.WorkspaceAutoSaveEligible) SaveSession(state.Session, false);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        finally { _autoSaving = false; }
    }

    private bool ConfirmSessionClose(EditorSession session)
    {
        var wasPreflighting = _preflighting;
        _preflighting = true;
        try
        {
            session.Editor.CommitWorkspaceEdits();
            if (!session.IsDirty) return true;
            var result = ModernDialog.Show(this, Localization.LocalizationService.GetString("UnsavedChanges"),
                string.Format(Localization.LocalizationService.GetString("UnsavedChangesConfirmationFormat"), session.Editor.WorkspaceTitle),
                ModernDialogTone.Warning, Localization.LocalizationService.GetString("LeaveWithoutSaving"),
                Localization.LocalizationService.GetString("Cancel"), Localization.LocalizationService.GetString("WorkspaceSaveAndClose"),
                primaryStyle: ModernDialogButtonStyle.Danger, secondaryStyle: ModernDialogButtonStyle.Primary);
            return result switch { ModernDialogResult.Primary => true, ModernDialogResult.Secondary => SaveSession(session, true), _ => false };
        }
        finally { _preflighting = wasPreflighting; }
    }

    public bool PreflightWorkspaceClose()
    {
        if (IsCloseApproved) return true;
        if (_saving || _preflighting) return false;
        return _tabsByDocumentId.Values.ToArray().All(state => ConfirmSessionClose(state.Session));
    }
    public void CloseAfterPreflight() { _skipCloseConfirmation = true; Close(); }

    public bool PrepareDocumentRemoval(IReadOnlyList<EditorDocumentKey> keys)
    {
        if (_saving || _preflighting || IsCloseApproved) return false;
        var affected = keys.Where(_tabsByDocumentId.ContainsKey).Select(key => _tabsByDocumentId[key]).ToArray();
        foreach (var state in affected) if (!ConfirmSessionClose(state.Session)) return false;
        foreach (var state in affected) FinishCloseTab(state);
        return true;
    }
    public void CloseDocument(EditorDocumentKey key) => CloseTab(key);
    private bool CloseTab(EditorDocumentKey key, bool askConfirmation = true)
    {
        if (_saving || _preflighting) return false;
        if (!_tabsByDocumentId.TryGetValue(key, out var state)) return true;
        if (askConfirmation && !ConfirmSessionClose(state.Session)) return false;
        if (ReferenceEquals(_dragTab, state.TabItem)) EndTabDrag();
        if (!_isWindowClosing && _tabsByDocumentId.Count == 1)
        {
            _skipCloseConfirmation = true;
            Close();
            return true;
        }
        FinishCloseTab(state);
        return true;
    }

    private void FinishCloseTab(EditorTabState state)
    {
        state.Session.Editor.WorkspaceStateChanged -= state.StateHandler;
        state.CloseButton.Click -= state.CloseClickHandler;
        state.TabItem.PreviewMouseLeftButtonDown -= TabItem_PreviewMouseLeftButtonDown;
        state.TabItem.PreviewMouseMove -= TabItem_PreviewMouseMove;
        state.TabItem.PreviewMouseLeftButtonUp -= TabItem_PreviewMouseLeftButtonUp;
        state.TabItem.LostMouseCapture -= TabItem_LostMouseCapture;
        if (ReferenceEquals(_activeSession, state.Session)) _activeSession = null;
        var host = GetEditorHost();
        if (host is not null && ReferenceEquals(host.Content, state.HostedContent)) host.Content = null;
        state.Session.Dispose();
        _configuredPopups.Remove(state.Session);
        _tabsByDocumentId.Remove(state.DocumentId);
        EditorTabs.Items.Remove(state.TabItem);
        if (!_isWindowClosing && EditorTabs.Items.Count == 0) { _skipCloseConfirmation = true; Close(); return; }
        ShowSelectedEditor(false);
    }

    private void ShowSelectedEditor(bool animate)
    {
        if (EditorTabs.SelectedItem is not TabItem tab) return;
        var state = _tabsByDocumentId.Values.FirstOrDefault(s => ReferenceEquals(s.TabItem, tab));
        if (state is not null) ShowHostedContent(state, animate);
    }
    private void ShowHostedContent(EditorTabState state, bool animate)
    {
        var host = GetEditorHost();
        if (host is null || ReferenceEquals(host.Content, state.HostedContent)) return;
        if (_activeSession is { } previous && Keyboard.FocusedElement is UIElement focused
            && previous.Content.IsAncestorOf(focused)) previous.FocusTarget = focused;
        _activeSession?.Editor.CommitWorkspaceEdits();
        _activeSession?.Editor.SetWorkspaceActive(false);
        if (_activeSession is { } leaving)
        {
            HostedEditorMotion.Settle(leaving.Content);
            // A rapid tab change can interrupt the first entrance. Settle before unmounting so
            // returning never retains a half-scaled stage or a partially visible heading.
            ArrivalGroups.Stop(leaving.Content, settle: true);
            UiMotion.Cleanup(leaving.Content);
            var (scale, move) = UiMotion.EnsureScaleTranslateGroup(leaving.Content);
            scale.ScaleX = scale.ScaleY = 1;
            move.X = move.Y = 0;
            leaving.Content.Opacity = 1;
        }
        var firstEntrance = !state.Session.HasEntered;
        var animateBody = animate && !WindowContentEnterHelper.IsEnterAnimating(this);
        if (animateBody) HostedEditorMotion.Prepare(state.Session.Content, firstEntrance);
        host.Content = state.HostedContent;
        _activeSession = state.Session;
        state.Session.Editor.SetWorkspaceActive(true);
        if (_configuredPopups.Add(state.Session))
            Dispatcher.BeginInvoke(() => { if (!state.Session.IsDisposed) UiMotion.ConfigurePopupsForWindow(this); },
                System.Windows.Threading.DispatcherPriority.Loaded);
        if (state.Session.FocusTarget is UIElement focusTarget)
            Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_activeSession, state.Session) || !focusTarget.IsVisible || !focusTarget.IsEnabled) return;
                RequestBringIntoViewEventHandler preserveViewport = (_, e) => e.Handled = true;
                state.Session.Content.AddHandler(FrameworkElement.RequestBringIntoViewEvent, preserveViewport, true);
                try { Keyboard.Focus(focusTarget); }
                finally { state.Session.Content.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, preserveViewport); }
            }, System.Windows.Threading.DispatcherPriority.Input);
        state.Session.HasEntered = true;
        if (animateBody)
            Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_activeSession, state.Session) && !state.Session.IsDisposed)
                    HostedEditorMotion.Enter(state.Session.Content, firstEntrance);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void ApplyMeasurementUnitSystemPreference(string units) { foreach (var editor in NotesEditors()) editor.ApplyMeasurementUnitSystemPreference(units); }
    public void ApplySpellCheckPreference(bool enabled) { foreach (var editor in NotesEditors()) editor.ApplySpellCheckPreference(enabled); }
    public void RefreshSpellCheckLanguages() { foreach (var editor in NotesEditors()) editor.RefreshSpellCheckLanguages(); }
    private IEnumerable<NoteEditorWindow> NotesEditors() => _tabsByDocumentId.Values.Select(s => s.Session.Editor).OfType<NoteEditorWindow>();

    private void Workspace_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_saving) { e.Handled = true; return; }
        if (e.Handled) return;
        var modifiers = Keyboard.Modifiers;
        // AltGr reports Ctrl+Alt on many keyboards; keep normal language input intact.
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == ModifierKeys.Control)
        {
            if (e.Key == Key.S && modifiers == ModifierKeys.Control && _activeSession is not null) { SaveSession(_activeSession, true); e.Handled = true; return; }
            if (e.Key == Key.W && modifiers == ModifierKeys.Control && _activeSession is not null) { CloseTab(_activeSession.Key); e.Handled = true; return; }
            if (e.Key == Key.Tab && EditorTabs.Items.Count > 0)
            {
                var delta = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
                EditorTabs.SelectedIndex = (EditorTabs.SelectedIndex + delta + EditorTabs.Items.Count) % EditorTabs.Items.Count;
                e.Handled = true; return;
            }
        }
        if (_activeSession?.Editor is NoteEditorWindow note)
        {
            if (!note.TryHandleFloatingImageKey(e)) note.TryHandleEditorTabKey(e);
        }
    }

    private void NewDocument_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var kind in Enum.GetValues<EditorDocumentKind>())
        {
            var label = kind switch { EditorDocumentKind.Note => "Notes", EditorDocumentKind.Flashcards => "Flashcards", EditorDocumentKind.Quiz => "Quizzes", _ => "MindMaps" };
            var icon = kind switch { EditorDocumentKind.Note => AppIconKind.Notes, EditorDocumentKind.Flashcards => AppIconKind.Flashcards,
                EditorDocumentKind.Quiz => AppIconKind.Quizzes, _ => AppIconKind.MindMaps };
            var item = new MenuItem { Header = Localization.LocalizationService.GetString(label), Icon = new AppIcon { Kind = icon, Width = 16, Height = 16 } };
            item.Click += (_, _) => CreateRequested?.Invoke(kind);
            menu.Items.Add(item);
        }
        UiMotion.OpenContextMenu(menu, (Button)sender);
    }
    private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TabItem tab)
            return;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;

        _dragTab = tab;
        _grabOffsetInTab = e.GetPosition(tab).X;
        _isDraggingTab = false;
    }

    private void TabItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var header = GetHeaderPanel();
        if (header is null)
            return;

        var pointer = e.GetPosition(header);
        if (!_isDraggingTab)
        {
            var tabOrigin = _dragTab.TranslatePoint(new Point(0, 0), header);
            var deltaX = pointer.X - (tabOrigin.X + _grabOffsetInTab);
            if (Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance)
                return;

            BeginTabDrag(_dragTab);
        }

        UpdateTabDrag(pointer.X);
        e.Handled = true;
    }

    private void TabItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => EndTabDrag();

    private void TabItem_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_reorderingTabs)
            return;
        if (_isDraggingTab)
            EndTabDrag();
    }

    private void BeginTabDrag(TabItem tab)
    {
        _isDraggingTab = true;
        _dragTab = tab;
        _dragFromIndex = EditorTabs.Items.IndexOf(tab);
        _dragToIndex = _dragFromIndex;
        _dragTabWidth = Math.Max(tab.ActualWidth, 1);
        _dragRestoreZIndex = Panel.GetZIndex(tab);
        Panel.SetZIndex(tab, 1000);
        Controls.EditorChromeContext.SetIsTabDragging(tab, true);
        SetPlateDragging(true);
        EditorTabs.SelectedItem = tab;
        ShowSelectedEditor(animate: false);

        _dragTransform = new TranslateTransform();
        tab.RenderTransform = _dragTransform;
        tab.CaptureMouse();
        Mouse.OverrideCursor = Cursors.SizeAll;
    }

    private void UpdateTabDrag(double pointerXInHeader)
    {
        if (_dragTab is null || _dragTransform is null)
            return;

        var header = GetHeaderPanel();
        if (header is null)
            return;

        var layoutLeft = _dragTab.TranslatePoint(new Point(0, 0), header).X - _dragTransform.X;
        _dragTransform.X = pointerXInHeader - _grabOffsetInTab - layoutLeft;
        Behaviors.TabSelectionPlateBehavior.SettleAfterDrag(EditorTabs);

        var target = ComputeDragTargetIndex(pointerXInHeader);
        if (target == _dragToIndex)
            return;

        _dragToIndex = target;
        ApplySiblingGapTransforms();
    }

    private int ComputeDragTargetIndex(double pointerXInHeader)
    {
        var header = GetHeaderPanel();
        if (header is null)
            return _dragFromIndex;

        for (var i = 0; i < EditorTabs.Items.Count; i++)
        {
            if (i == _dragFromIndex)
                continue;
            if (EditorTabs.Items[i] is not TabItem tab)
                continue;

            var mid = GetTabLayoutLeft(tab, header) + tab.ActualWidth * 0.5;
            if (pointerXInHeader < mid)
                return i > _dragFromIndex ? i - 1 : i;
        }

        return EditorTabs.Items.Count - 1;
    }

    private void ApplySiblingGapTransforms()
    {
        if (_dragTab is null)
            return;

        var shift = _dragTabWidth + 8;
        for (var i = 0; i < EditorTabs.Items.Count; i++)
        {
            if (EditorTabs.Items[i] is not TabItem tab || ReferenceEquals(tab, _dragTab))
                continue;

            var transform = tab.RenderTransform as TranslateTransform ?? new TranslateTransform();
            tab.RenderTransform = transform;

            double x = 0;
            if (_dragFromIndex < _dragToIndex && i > _dragFromIndex && i <= _dragToIndex)
                x = -shift;
            else if (_dragFromIndex > _dragToIndex && i >= _dragToIndex && i < _dragFromIndex)
                x = shift;

            transform.X = x;
        }
    }

    private void ClearSiblingGapTransforms()
    {
        foreach (var item in EditorTabs.Items)
        {
            if (item is not TabItem tab || ReferenceEquals(tab, _dragTab))
                continue;
            tab.RenderTransform = Transform.Identity;
        }
    }

    private void EndTabDrag()
    {
        if (_dragTab is null)
        {
            _isDraggingTab = false;
            Mouse.OverrideCursor = null;
            return;
        }

        var tab = _dragTab;
        var toIndex = _dragToIndex;
        var wasDragging = _isDraggingTab;

        if (wasDragging)
            ClearSiblingGapTransforms();

        if (tab.IsMouseCaptured)
        {
            _reorderingTabs = true;
            try { tab.ReleaseMouseCapture(); }
            finally { _reorderingTabs = false; }
        }

        if (wasDragging)
        {
            tab.RenderTransform = Transform.Identity;
            Panel.SetZIndex(tab, _dragRestoreZIndex);
        }

        _dragTab = null;
        _dragTransform = null;
        _isDraggingTab = false;
        Mouse.OverrideCursor = null;

        // Clicks set _dragTab on mouse-down but never BeginTabDrag — do not commit
        // with a stale _dragToIndex (that was sending tabs to the end).
        if (wasDragging)
            CommitTabReorder(tab, toIndex);
        Controls.EditorChromeContext.SetIsTabDragging(tab, false);
        SetPlateDragging(false);
        Behaviors.TabSelectionPlateBehavior.SettleAfterDrag(EditorTabs);
    }

    private void SetPlateDragging(bool dragging)
    {
        if (EditorTabs.Template?.FindName("TabSelectionPlate", EditorTabs) is DependencyObject plate)
            Controls.EditorChromeContext.SetIsTabDragging(plate, dragging);
    }

    private void CommitTabReorder(TabItem tab, int toIndex)
    {
        var fromIndex = EditorTabs.Items.IndexOf(tab);
        if (fromIndex < 0)
            return;

        toIndex = Math.Clamp(toIndex, 0, EditorTabs.Items.Count - 1);
        if (fromIndex == toIndex)
        {
            EditorTabs.SelectedItem = tab;
            ShowSelectedEditor(animate: false);
            return;
        }

        _reorderingTabs = true;
        try
        {
            // Header-only TabItems — Remove/Insert no longer tears down editor visuals.
            EditorTabs.Items.RemoveAt(fromIndex);
            EditorTabs.Items.Insert(toIndex, tab);
            EditorTabs.SelectedItem = tab;
        }
        finally
        {
            _reorderingTabs = false;
        }

        ShowSelectedEditor(animate: false);
    }

    private ContentControl? GetEditorHost()
    {
        EditorTabs.ApplyTemplate();
        return EditorTabs.Template?.FindName("EditorHost", EditorTabs) as ContentControl;
    }

    private Panel? GetHeaderPanel()
    {
        EditorTabs.ApplyTemplate();
        return EditorTabs.Template?.FindName("HeaderPanel", EditorTabs) as Panel;
    }

    private static double GetTabLayoutLeft(TabItem tab, UIElement relativeTo)
    {
        var visualLeft = tab.TranslatePoint(new Point(0, 0), relativeTo).X;
        return tab.RenderTransform is TranslateTransform transform
            ? visualLeft - transform.X
            : visualLeft;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_saving || _preflighting) { e.Cancel = true; return; }
        EndTabDrag();
        if (WindowContentEnterHelper.ShouldBypassCloseHandling(this))
            return;

        if (_skipCloseConfirmation) { IsCloseApproved = true; _autoSaveTimer.Stop(); return; }

        foreach (var state in _tabsByDocumentId.Values.ToList())
        {
            if (ConfirmSessionClose(state.Session))
                continue;

            e.Cancel = true;
            return;
        }
        IsCloseApproved = true;
        _autoSaveTimer.Stop();
    }

    private void FinalizeWindowClose()
    {
        _isWindowClosing = true;
        _skipCloseConfirmation = false;

        // Claim foreground now, while this window is still alive, rather than leaving it to the
        // reactive MainWindow.Activate() in MainWindow's Closed handler. The real native close
        // happens a dispatcher tick later (WindowContentEnterHelper.FinishExit) - without this,
        // the gap between this window's HWND being destroyed and that later Activate() call left
        // whatever the OS handed focus to next (e.g. another app behind Scriblucent) briefly
        // visible before the owner reclaimed foreground.
        Owner?.Activate();

        foreach (var state in _tabsByDocumentId.Values.ToList())
            FinishCloseTab(state);
    }
}
