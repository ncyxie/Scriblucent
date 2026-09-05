using Scriblucent.Animations;
using Scriblucent.Icons;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Scriblucent;

public partial class NoteEditorTabsWindow : Window
{
    private sealed class EditorTabState
    {
        public required Guid DocumentId { get; init; }
        public required NoteEditorWindow Editor { get; init; }
        public required NoteCardViewModel NoteViewModel { get; init; }
        public required TabItem TabItem { get; init; }
        public required UIElement HostedContent { get; init; }
        public required TextBlock TitleTextBlock { get; init; }
        public required Button CloseButton { get; init; }
        public required Action<NoteDocument> AutoSaveHandler { get; init; }
        public required RoutedEventHandler CloseClickHandler { get; init; }
    }

    private readonly Dictionary<Guid, EditorTabState> _tabsByDocumentId = new();
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

    public NoteEditorTabsWindow()
    {
        InitializeComponent();
        Scriblucent.Services.WindowThemeService.Register(this);
        PreviewKeyDown += NoteEditorTabsWindow_PreviewKeyDown;
        EditorTabs.SelectionChanged += EditorTabs_SelectionChanged;
        Loaded += (_, _) => UiMotion.ConfigurePopupsForWindow(this);
        Closed += (_, _) =>
        {
            // Safety net: FinalizeWindowClose (the normal "dispose every open tab" path) only
            // runs via the animated-close callback, which WPF skips entirely under reduced
            // motion (in-app setting or the Windows "Show animations" accessibility setting) -
            // see WindowContentEnterHelper.OnWindowClosing. This handler always runs regardless
            // of that path, so sweep any tabs that weren't already disposed (each tab's own
            // DisposeHostedEditor() is idempotent, so re-calling it for already-closed tabs is
            // harmless). Runs synchronously - unlike the per-tab background dispose used for a
            // single tab closing, the whole window (likely the whole app) is tearing down here,
            // so a bounded few-seconds wait to guarantee worker processes are actually killed
            // beats a fire-and-forget task that may get cut off by process exit.
            foreach (var state in _tabsByDocumentId.Values)
                state.Editor.DisposeHostedEditor(disposeAiBackendSynchronously: true);

            // The editor teardown (document, coordinator, wrap engine) leaves sizeable transient
            // garbage that otherwise waits for the next incidental gen2 pass; collect off-thread
            // and return the freed pages (the app is idle at this moment).
            _ = Task.Run(() =>
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                Scriblucent.ViewModels.MainViewModel.TrimWorkingSet();
            });
        };
        Closing += Window_Closing;
        WindowContentEnterHelper.AnimateOnLoaded(this, "EditorTabs", FinalizeWindowClose);
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isDraggingTab || _reorderingTabs)
            return;

        ShowSelectedEditor(animate: IsLoaded);
    }

    public void OpenOrFocusNote(NoteCardViewModel noteViewModel, object? sharedDataContext, bool animateContent = true)
    {
        var documentId = noteViewModel.Document.Id;
        if (_tabsByDocumentId.TryGetValue(documentId, out var existingState))
        {
            EditorTabs.SelectedItem = existingState.TabItem;
            ShowHostedContent(existingState, animate: false);
            existingState.Editor.Focus();
            return;
        }

        var editor = new NoteEditorWindow();
        editor.EnableTabMode();
        editor.DataContext = sharedDataContext;
        editor.LoadFromDocument(noteViewModel.Document);
        editor.SetCurrentDocument(noteViewModel.Document);

        var hostedContent = editor.DetachEditorContentForHosting();
        if (hostedContent == null)
        {
            // editor was constructed (and with it, its own AI worker adapter/idle timer) but
            // never got wired into a tab - dispose it now instead of abandoning it uncleaned.
            editor.DisposeHostedEditor();
            return;
        }

        var noteIcon = new AppIcon
        {
            Kind = AppIconKind.Notes
        };
        if (TryFindResource("EditorTabIconStyle") is Style iconStyle)
            noteIcon.Style = iconStyle;

        var titleText = new TextBlock
        {
            Text = GetTabHeaderText(noteViewModel)
        };
        if (TryFindResource("EditorTabTitleStyle") is Style titleStyle)
            titleText.Style = titleStyle;

        var closeButton = new Button
        {
            Content = new AppIcon
            {
                Kind = AppIconKind.Close,
                Width = 11,
                Height = 11
            }
        };
        if (TryFindResource("EditorTabCloseButtonStyle") is Style closeStyle)
            closeButton.Style = closeStyle;

        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerPanel.Children.Add(noteIcon);
        headerPanel.Children.Add(titleText);
        headerPanel.Children.Add(closeButton);

        // Header-only TabItem — heavy editor UI stays on EditorHost so Items reorder is safe.
        var tabItem = new TabItem
        {
            Header = headerPanel,
            Content = null
        };
        tabItem.PreviewMouseLeftButtonDown += TabItem_PreviewMouseLeftButtonDown;
        tabItem.PreviewMouseMove += TabItem_PreviewMouseMove;
        tabItem.PreviewMouseLeftButtonUp += TabItem_PreviewMouseLeftButtonUp;
        tabItem.LostMouseCapture += TabItem_LostMouseCapture;

        Action<NoteDocument> autoSaveHandler = _ =>
        {
            noteViewModel.NotifyContentChanged();
            titleText.Text = GetTabHeaderText(noteViewModel);
        };
        editor.DocumentAutoSaved += autoSaveHandler;
        editor.CloseRequested += OnEditorCloseRequested;

        RoutedEventHandler closeClickHandler = (s, e) =>
        {
            e.Handled = true;
            CloseTab(documentId);
        };
        closeButton.Click += closeClickHandler;

        var state = new EditorTabState
        {
            DocumentId = documentId,
            Editor = editor,
            NoteViewModel = noteViewModel,
            TabItem = tabItem,
            HostedContent = hostedContent,
            TitleTextBlock = titleText,
            CloseButton = closeButton,
            AutoSaveHandler = autoSaveHandler,
            CloseClickHandler = closeClickHandler
        };

        _tabsByDocumentId[documentId] = state;

        void InsertTab()
        {
            EditorTabs.ApplyTemplate();
            _reorderingTabs = true;
            try
            {
                EditorTabs.Items.Add(tabItem);
                EditorTabs.SelectedItem = tabItem;
            }
            finally
            {
                _reorderingTabs = false;
            }

            ShowHostedContent(state, animate: animateContent && IsLoaded);
        }

        if (IsLoaded)
            InsertTab();
        else
            Loaded += OnLoaded;

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            InsertTab();
        }
    }

    public void ApplyMeasurementUnitSystemPreference(string unitSystem)
    {
        foreach (var state in _tabsByDocumentId.Values)
            state.Editor.ApplyMeasurementUnitSystemPreference(unitSystem);
    }

    public void ApplySpellCheckPreference(bool enabled)
    {
        foreach (var state in _tabsByDocumentId.Values)
            state.Editor.ApplySpellCheckPreference(enabled);
    }

    public void RefreshSpellCheckLanguages()
    {
        foreach (var state in _tabsByDocumentId.Values)
            state.Editor.RefreshSpellCheckLanguages();
    }

    private void NoteEditorTabsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || EditorTabs.SelectedItem is not TabItem selectedTab)
            return;

        var state = _tabsByDocumentId.Values.FirstOrDefault(state => ReferenceEquals(state.TabItem, selectedTab));
        if (state is null)
            return;

        if (state.Editor.TryHandleFloatingImageKey(e))
            return;

        state.Editor.TryHandleEditorTabKey(e);
    }

    private static string GetTabHeaderText(NoteCardViewModel noteViewModel)
    {
        var title = noteViewModel.Document.Title;
        return string.IsNullOrWhiteSpace(title) ? "Untitled" : title;
    }

    private void OnEditorCloseRequested(NoteEditorWindow editor)
    {
        foreach (var state in _tabsByDocumentId.Values)
        {
            if (ReferenceEquals(state.Editor, editor))
            {
                CloseTab(state.DocumentId, askConfirmation: false);
                break;
            }
        }
    }

    private bool CloseTab(Guid documentId, bool askConfirmation = true)
    {
        if (!_tabsByDocumentId.TryGetValue(documentId, out var state))
            return true;

        if (askConfirmation && !state.Editor.ConfirmCloseIfNeeded())
            return false;

        if (ReferenceEquals(_dragTab, state.TabItem))
            EndTabDrag();

        // Last tab: close the window so the shared sink animation runs with content still mounted.
        if (!_isWindowClosing && _tabsByDocumentId.Count == 1)
        {
            _skipCloseConfirmation = true;
            Close();
            return true;
        }

        if (!UiMotion.PrefersReducedMotion && state.HostedContent is FrameworkElement content)
        {
            UiMotion.Fade(
                content,
                0,
                UiMotion.NavFadeOutMs,
                UiMotion.InCubic,
                () => FinishCloseTab(state));
            return true;
        }

        FinishCloseTab(state);
        return true;
    }

    private void FinishCloseTab(EditorTabState state)
    {
        state.Editor.DocumentAutoSaved -= state.AutoSaveHandler;
        state.Editor.CloseRequested -= OnEditorCloseRequested;
        state.CloseButton.Click -= state.CloseClickHandler;
        state.TabItem.PreviewMouseLeftButtonDown -= TabItem_PreviewMouseLeftButtonDown;
        state.TabItem.PreviewMouseMove -= TabItem_PreviewMouseMove;
        state.TabItem.PreviewMouseLeftButtonUp -= TabItem_PreviewMouseLeftButtonUp;
        state.TabItem.LostMouseCapture -= TabItem_LostMouseCapture;

        var host = GetEditorHost();
        if (host is not null && ReferenceEquals(host.Content, state.HostedContent))
            host.Content = null;

        state.Editor.DisposeHostedEditor();

        EditorTabs.Items.Remove(state.TabItem);
        _tabsByDocumentId.Remove(state.DocumentId);

        if (!_isWindowClosing && EditorTabs.Items.Count == 0)
        {
            Close();
            return;
        }

        ShowSelectedEditor(animate: false);
    }

    private void ShowSelectedEditor(bool animate)
    {
        if (EditorTabs.SelectedItem is not TabItem selectedTab)
        {
            var host = GetEditorHost();
            if (host is not null)
                host.Content = null;
            return;
        }

        var state = _tabsByDocumentId.Values.FirstOrDefault(s => ReferenceEquals(s.TabItem, selectedTab));
        if (state is null)
            return;

        ShowHostedContent(state, animate);
    }

    private void ShowHostedContent(EditorTabState state, bool animate)
    {
        var host = GetEditorHost();
        if (host is null)
            return;

        if (!ReferenceEquals(host.Content, state.HostedContent))
            host.Content = state.HostedContent;

        if (animate && state.HostedContent is FrameworkElement hostedElement)
            UiMotion.AnimateContentEnter(hostedElement, durationMs: UiMotion.QuickMs, offsetY: UiMotion.SubtleLiftY);
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
        if (WindowContentEnterHelper.ShouldBypassCloseHandling(this))
            return;

        if (_skipCloseConfirmation)
            return;

        foreach (var state in _tabsByDocumentId.Values.ToList())
        {
            if (state.Editor.ConfirmCloseIfNeeded())
                continue;

            e.Cancel = true;
            return;
        }
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
