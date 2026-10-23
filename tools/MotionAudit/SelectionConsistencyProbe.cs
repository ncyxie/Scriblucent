using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeRenameDefault()
    {
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme);
            LocalizationService.SetCulture(theme == "Light" ? "en" : "lt"); MotionPolicy.Configure(MotionPreference.Full, false);
            ConsistencyRenameDefault(checks);
        }
        File.WriteAllLines(Path.Combine(_output, "rename-default-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
    private static void ConsistencyRenameDefault(List<string> checks)
    {
        foreach (int count in new[] { 1, 2 })
        {
            var targets = Enumerable.Range(0, count).Select(index => new RenameTarget(Guid.NewGuid(), "Original " + index)).ToArray();
            var dialog = new BulkRenameDialog(targets);
            DriveRenameDialog(current =>
            {
                var name = (TextBox)current.FindName("NameBox");
                Require(((ComboBox)current.FindName("ModeBox")).SelectedIndex == (int)BulkRenameMode.RenameTo && name.IsVisible && name.IsKeyboardFocused, "Simple rename was not the default focused mode.");
                Require(!((FrameworkElement)current.FindName("ReplaceFields")).IsVisible, "Find/replace fields remained visible in simple rename.");
                Require(name.Text == (count == 1 ? targets[0].Name : "") && name.SelectedText == name.Text && !((Button)current.FindName("ApplyButton")).IsEnabled, "Initial rename value/selection or unchanged Apply state was incorrect.");
                name.Text = "New exact name";
                Require(current.Changes.Count == count && current.Changes.All(change => change.After == "New exact name"), "Simple rename did not preview the exact name for every target.");
                if (count == 2) SaveControlCapture((FrameworkElement)current.Content, "rename-default");
                SelectionClick((Button)current.FindName("ApplyButton"));
            }, () => dialog.ShowDialog());
            Require(dialog.DialogResult == true && dialog.Changes.Count == count && targets[0].Name == "Original 0", "Accepted simple rename lost its plan or mutated its source.");
        }
        checks.Add($"{LocalizationService.GetString("RenameTo")}: default mode, single-name prefill/selection, field focus, exact multi-item preview and accepted plan passed.");
    }
    private static void ProbeSelectionConsistency(string? only)
    {
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
        FlashcardMediaStorageService.MediaDirectoryOverride = Path.Combine(_output, "media");
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, new AppSettings { EnableAutoSave = false, EnableAiFeatures = false });
        var checks = new List<string>();
        foreach (var (theme, mode) in new[] { ("Light", MotionPreference.Full), ("Dark", MotionPreference.Reduced) })
        {
            if (only is not null && only != $"{theme}/{mode}") continue;
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme); LocalizationService.SetCulture(theme == "Light" ? "en" : "lt"); MotionPolicy.Configure(mode, false);
            ConsistencyRenameDefault(checks);
            ConsistencyNodeEditor(theme, mode, checks);
            CompletionTrash(theme + "-" + mode, checks);
            ConsistencyTrashRows(theme, checks);
            ConsistencyDashboardRename(theme, mode, checks);
        }
        ConsistencyLargeRename(checks);
        File.WriteAllLines(Path.Combine(_output, "consistency-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
    private static void ConsistencyNodeEditor(string theme, MotionPreference mode, List<string> checks)
    {
        var left = new MindMapNode { Text = "First idea", FontSize = 19, IsBold = true, Note = "Authored note" };
        var right = new MindMapNode { Text = "Second idea", TextColor = "#AA6677" };
        var root = new MindMapNode { Text = "Selection entry", Children = [left, right, new() { Text = "Untouched" }] };
        var map = new MindMapPreviewWindow(root) { Width = 980, Height = 800, ShowInTaskbar = false };
        try
        {
            map.Show(); map.Activate(); Pump(350);
            var canvas = (Canvas)map.FindName("MapCanvas");
            var scope = CompletionField<ICollectionSelectionScope>(map, "_nodeSelectionScope");
            var dock = (CollectionSelectionDock)map.FindName("NodeSelectionDock");
            var undo = CompletionField<List<MindMapNode>>(map, "_undoStack");
            var face = canvas.Children.OfType<Border>().Single(face => ReferenceEquals(face.Tag, left));
            Require(!dock.IsVisible, "Ordinary node navigation exposed the selection island.");
            face.ContextMenu!.PlacementTarget = face; face.ContextMenu.IsOpen = true; Pump(100);
            var select = face.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocalizationService.GetString("MassSelectModeTitle")));
            select.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); face.ContextMenu.IsOpen = false; Pump(250);
            Require(scope.IsActive && scope.Count == 1 && dock.IsVisible && !map.WorkspaceDirty, "Context Select did not enter the shared one-item mode cleanly.");
            var canvasViewer = (ScrollViewer)map.FindName("MapScrollViewer"); canvasViewer.Focus();
            var tab = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(map), Environment.TickCount, Key.Tab) { RoutedEvent = Keyboard.KeyDownEvent };
            canvasViewer.RaiseEvent(tab); Require(!tab.Handled, "Selection-mode Tab was stolen by add-child instead of navigating focus.");
            var second = canvas.Children.OfType<Border>().Single(face => ReferenceEquals(face.Tag, right));
            second.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent }); Pump(100);
            Require(scope.Count == 2, "Ordinary node click in selection mode did not add the second node.");
            RemainingCall(map, "SetFontPanelOpen", true); Pump(250);
            var size = (ComboBox)map.FindName("FontSizeBox"); var bold = (ToggleButton)map.FindName("FontBoldToggleButton");
            Require(size.IsEnabled && Equals(size.SelectedValue, "__mixed") && bold.IsChecked is null, "Mixed node font values were disabled or hidden.");
            int before = undo.Count; size.SelectedValue = "20"; Pump(150);
            Require(left.FontSize == 20 && right.FontSize == 20 && root.Children[2].FontSize is null && left.Note == "Authored note" && undo.Count == before + 1,
                "Bulk font edit changed unselected properties or made multiple undo steps.");
            // Mixed bold becomes uniformly enabled on the first click.
            bold.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(100);
            Require(left.IsBold && right.IsBold, "Mixed bold did not apply consistently.");
            before = undo.Count;
            DriveRenameDialog(dialog =>
            {
                ((ComboBox)dialog.FindName("ModeBox")).SelectedIndex = (int)BulkRenameMode.Numbered;
                ((TextBox)dialog.FindName("BaseBox")).Text = "Renamed idea";
                ((TextBox)dialog.FindName("StartBox")).Text = "5";
                Require(dialog.Changes.Select(change => change.After).SequenceEqual(new[] { "Renamed idea 5", "Renamed idea 6" }), "Rename preview lost tree order.");
                SaveControlCapture((FrameworkElement)dialog.Content, "bulk-rename-" + theme);
                SelectionClick((Button)dialog.FindName("ApplyButton"));
            }, () => RemainingCall(map, "RenameSelectedNodes")); Pump(200);
            Require(left.Text == "Renamed idea 5" && right.Text == "Renamed idea 6" && root.Children[2].Text == "Untouched" && scope.Count == 2 && undo.Count == before + 1,
                "Bulk node rename lost selection, touched another node or created multiple undo steps.");
            RemainingCall(map, "Undo"); Pump(100);
            left = root.Children[0]; right = root.Children[1];
            Require(left.Text == "First idea" && right.Text == "Second idea" && left.FontSize == 20 && right.FontSize == 20, "One rename undo did not preserve prior style edits.");
            scope.Begin(left.Id); scope.Toggle(right.Id); before = undo.Count;
            DriveRenameDialog(dialog =>
            {
                ((ComboBox)dialog.FindName("ModeBox")).SelectedIndex = (int)BulkRenameMode.Replace;
                ((TextBox)dialog.FindName("FindBox")).Text = "Second idea";
                ((TextBox)dialog.FindName("ReplacementBox")).Text = "";
                Require(!((Button)dialog.FindName("ApplyButton")).IsEnabled, "Rename allowed an empty result.");
                RemainingCall(dialog, "CancelClick", dialog, new RoutedEventArgs());
            }, () => RemainingCall(map, "RenameSelectedNodes"));
            Require(left.Text == "First idea" && right.Text == "Second idea" && undo.Count == before, "Cancelled/invalid rename changed content.");
            scope.Clear(); Pump(100); Require(scope.IsActive && scope.Count == 0 && dock.IsVisible, "Clear exited mind-map selection mode.");
            scope.End(); WaitLoading(() => !dock.IsVisible); Require(!scope.IsActive, "Done did not dismiss the node island.");
            checks.Add($"{theme}/{mode}: real node context Select, single-item island, ordinary selection clicks, mixed/bulk fonts, previewed rename/cancel, one undo, retained selection and Clear/Done passed.");
        }
        finally { map.DisposeWorkspaceEditor(); Pump(150); }
    }
    private static void ConsistencyLargeRename(List<string> checks)
    {
        var targets = Enumerable.Range(0, 20000).Select(index => new RenameTarget(Guid.NewGuid(), "Original " + index)).ToArray();
        var dialog = new BulkRenameDialog(targets);
        DriveRenameDialog(current =>
        {
            ((ComboBox)current.FindName("ModeBox")).SelectedIndex = (int)BulkRenameMode.Affixes;
            var prefix = (TextBox)current.FindName("PrefixBox");
            prefix.Text = "Discarded "; prefix.Text = "Latest ";
            Require(!((Button)current.FindName("ApplyButton")).IsEnabled, "Large rename accepted an outdated preview.");
            WaitLoading(() => ((Button)current.FindName("ApplyButton")).IsEnabled);
            Require(current.Changes.Count == 20000 && current.Changes.All(change => change.After.StartsWith("Latest ")), "Large preview published stale names.");
            var preview = (ItemsControl)current.FindName("PreviewList");
            Require(preview.Items.Count == 60 && ControlTree(preview).OfType<ContentPresenter>().Count() < 30, "Large rename realized an unbounded preview.");
            RemainingCall(current, "CancelClick", current, new RoutedEventArgs());
        }, () => dialog.ShowDialog());
        Require(targets[0].Name == "Original 0", "Cancelled large rename mutated its source.");
        checks.Add("20k names: background/debounced preview, stale-result protection, disabled pending Apply, bounded visible rows and cancellation passed.");
    }
    private static void DriveRenameDialog(Action<BulkRenameDialog> edit, Action open)
    {
        Exception? failure = null; bool found = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) =>
        {
            if (_app.Windows.OfType<BulkRenameDialog>().FirstOrDefault(window => window.IsVisible) is not { } dialog) return;
            timer.Stop(); found = true;
            Pump(450);
            try { edit(dialog); }
            catch (Exception error) { failure = error; RemainingCall(dialog, "CancelClick", dialog, new RoutedEventArgs()); }
        };
        timer.Start(); try { open(); } finally { timer.Stop(); }
        Require(found, "Shared rename dialog was not offered.");
        if (failure is not null) throw new InvalidOperationException("Rename interaction failed.", failure);
    }
    private static void ConsistencyTrashRows(string theme, List<string> checks)
    {
        var panel = new TrashPanel { Visibility = Visibility.Visible };
        var motion = (FrameworkElement)panel.FindName("PanelMotionHost"); motion.Opacity = 1; motion.RenderTransform = System.Windows.Media.Transform.Identity;
        var list = (ListBox)panel.FindName("TrashedItemsListBox");
        var items = new LazyTrashItemList(Enumerable.Range(0, 12_000).Select(index => new TrashItemEntry(TrashItemKind.Note, Guid.NewGuid(), "Deleted " + index, DateTime.UtcNow, new object())).ToArray(), 30);
        list.ItemsSource = items;
        using var fixture = new CompletionWindow(new Window { Content = panel, Width = 820, Height = 740 });
        var scope = CompletionField<ICollectionSelectionScope>(panel, "_trashSelection");
        var dock = (CollectionSelectionDock)panel.FindName("TrashSelectionDock");
        var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var toggle = ControlTree(first).OfType<CollectionSelectionToggle>().Single();
        Require(!toggle.IsVisible && !dock.IsVisible, "Trash exposed permanent selection controls.");
        var face = ControlTree(first).OfType<SurfaceMaterialBorder>().Single();
        face.ContextMenu!.PlacementTarget = face; face.ContextMenu.IsOpen = true; Pump(70);
        ((MenuItem)face.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); face.ContextMenu.IsOpen = false; Pump(240);
        Require(scope.Count == 1 && dock.IsVisible && toggle.IsVisible && toggle.IsChecked == true, "Trash context Select did not synchronize its island and checkbox.");
        var peer = new System.Windows.Automation.Peers.CheckBoxAutomationPeer(toggle);
        var automation = (System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
        automation.Toggle(); Pump(160); Require(scope.Count == 0 && scope.IsActive && toggle.IsChecked == false, "Trash checkbox could not deselect within the mode.");
        SelectionClick((Button)dock.FindName("AllButton")); Pump(160);
        Require(scope.Count == 12000 && panel.IsLogicalAllSelected && list.SelectedItems.Count == 0 && toggle.IsChecked == true, "Trash island lost logical select-all.");
        automation.Toggle(); Pump(160); Require(scope.Count == 11999 && toggle.IsChecked == false && !scope.Contains(items[0].Id), "Logical exclusion did not update its checkbox/count.");
        var viewer = ControlTree(list).OfType<ScrollViewer>().First();
        list.ScrollIntoView(items[^1]); Pump(180); viewer.ScrollToEnd(); Pump(180); viewer.ScrollToEnd(); Pump(100);
        var last = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(items.Count - 1);
        Require(last is not null && last.TranslatePoint(new Point(0, last.ActualHeight - last.Margin.Bottom), fixture.Window).Y < dock.TranslatePoint(new Point(), fixture.Window).Y,
            "Trash island covered the final row.");
        Require(items.CreatedItemCount < 250, $"Trash island materialized the library: {items.CreatedItemCount} created, {ControlTree(list).OfType<ListBoxItem>().Count()} realized.");
        SaveControlCapture(panel, "trash-island-" + theme);
        Require(SelectionKey((Button)dock.FindName("MoreButton"), fixture.Window, Key.Escape).Handled && !scope.IsActive, "Trash Escape did not leave selection from its island.");
        checks.Add(theme + ": Trash context entry, attached hidden-until-selecting checkbox, UIA toggle, 12k logical-all/exclusions, last-row clearance and island Escape passed.");
    }
    private static void OpenConsistencyEditor(Scriblucent.MainWindow window, MainViewModel vm, EditorDocumentKey key)
    {
        switch (key.Kind)
        {
            case EditorDocumentKind.Note: window.EditorWorkspace.OpenNote(vm.Notes.Single(item => item.Document.Id == key.Id)); break;
            case EditorDocumentKind.Flashcards: window.EditorWorkspace.OpenFlashcards(vm.FlashcardSets.Single(item => item.Document.Id == key.Id).Document); break;
            case EditorDocumentKind.Quiz: window.EditorWorkspace.OpenQuiz(vm.Quizzes.Single(item => item.Document.Id == key.Id).Document); break;
            default: window.EditorWorkspace.OpenMindMap(vm.MindMaps.Single(item => item.Document.Id == key.Id).Document); break;
        }
    }
    private static void ConsistencyDashboardRename(string theme, MotionPreference mode, List<string> checks)
    {
        LoadNativeControlResources(theme); // Restore the audit's assembly-qualified logo URI.
        var (window, vm, store) = LoadingWindow(theme, mode, 3);
        try
        {
            window.Show(); window.Activate();
            foreach (var kind in Enum.GetValues<EditorDocumentKind>())
            {
                SelectLoadingDashboard(vm, kind); WaitLoading(() => vm.IsActiveDashboardReady && ((FrameworkElement)window.FindName("DashboardLoadingLayer")).Visibility == Visibility.Collapsed); Pump(200);
                var page = (FrameworkElement)window.FindName(vm.ActiveDashboard + "DashboardView");
                var frame = ControlTree(page).OfType<CollectionSelectionFrame>().First(item => item.IsVisible);
                if (kind == EditorDocumentKind.Note)
                {
                    // MotionAudit substitutes the App-owned shared note menu. The declaration
                    // is verified from production XAML; the real frame's entry is tested here.
                    var source = System.Xml.Linq.XDocument.Load(Path.Combine(Environment.CurrentDirectory, "Scriblucent", "App.xaml"));
                    Require(source.Descendants().Any(element => element.Name.LocalName == "MenuItem" && (string?)element.Attribute("Click") == "OnNoteCardMenuSelectMultiple"), "Notes lost its production context selection declaration.");
                    vm.DashboardSelection.Begin(frame.ItemId);
                }
                else
                {
                    var contextOwner = ControlTree(frame).OfType<FrameworkElement>().FirstOrDefault(item => item.ContextMenu is { } menu && menu.Items.OfType<MenuItem>().Any(entry => Equals(entry.Header, LocalizationService.GetString("SelectMultiple"))));
                    // Notes put their shared menu on the outer NoteCardControl.
                    contextOwner ??= ControlTree(page).OfType<NoteCardControl>().FirstOrDefault();
                    Require(contextOwner?.ContextMenu is not null, kind + " has no explicit context selection entry.");
                    contextOwner!.ContextMenu!.PlacementTarget = contextOwner; contextOwner.ContextMenu.IsOpen = true; Pump(80);
                    var select = contextOwner.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocalizationService.GetString("SelectMultiple")));
                    select.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); contextOwner.ContextMenu.IsOpen = false; Pump(180);
                    Require(vm.DashboardSelection.IsActive && vm.DashboardSelection.Count == 1, kind + " context Select was not wired.");
                }
                vm.DashboardSelection.SelectAll();
                var menu = (ContextMenu)RemainingCall(window, "BuildDashboardSelectionMenu")!;
                var rename = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, LocalizationService.GetString("RenameSelected")));
                // Open one clean and one dirty tab to verify renaming never saves unrelated edits.
                if (vm.SelectedDashboardExportKeys().Count < 2)
                { vm.QuickAddDashboardItemCommand.Execute(null); Pump(220); vm.DashboardSelection.SelectAll(); }
                var keys = vm.SelectedDashboardExportKeys();
                OpenConsistencyEditor(window, vm, keys[0]); Pump(180);
                var workspace = window.EditorWorkspace.Window!; workspace.ShowInTaskbar = false;
                var clean = workspace.Sessions.Single(session => session.Key == keys[0]);
                OpenConsistencyEditor(window, vm, keys[1]); Pump(180);
                var dirty = workspace.Sessions.Single(session => session.Key == keys[1]);
                var pendingTags = (TextBox)dirty.Content.FindName("TagsTextBox"); pendingTags.Text = "pending-unwritten";
                pendingTags.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); dirty.Editor.CommitWorkspaceEdits(); Pump(180);
                Require(!clean.Editor.WorkspaceDirty && dirty.Editor.WorkspaceDirty, kind + " dirty/clean editor fixture did not initialize.");
                window.Activate();
                var before = vm.SelectedDashboardRenameTargets();
                DriveRenameDialog(dialog =>
                {
                    ((ComboBox)dialog.FindName("ModeBox")).SelectedIndex = (int)BulkRenameMode.Affixes;
                    ((TextBox)dialog.FindName("PrefixBox")).Text = "Updated ";
                    SelectionClick((Button)dialog.FindName("ApplyButton"));
                }, () => rename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                var after = vm.SelectedDashboardRenameTargets();
                Require(after.Count == before.Count && after.All(item => item.Name.StartsWith("Updated ")), kind + " shared rename did not persist/refresh the selected titles.");
                Require(!clean.Editor.WorkspaceDirty && dirty.Editor.WorkspaceDirty && pendingTags.Text.Contains("pending-unwritten"), kind + " rename changed the editors dirty state or lost pending content: clean=" + clean.Editor.WorkspaceDirty + ", dirty=" + dirty.Editor.WorkspaceDirty + ", tags=" + pendingTags.Text);
                var filename = kind switch { EditorDocumentKind.Note => "notes.json", EditorDocumentKind.Flashcards => "flashcard-sets.json", EditorDocumentKind.MindMap => "mind-maps.json", _ => "quizzes.json" };
                var persisted = File.ReadAllText(Path.Combine(store, filename));
                Require(!persisted.Contains("pending-unwritten"), kind + " rename saved unrelated editor tags.");
                var blocker = Path.Combine(store, "blocked"); File.WriteAllText(blocker, "fixture"); vm.PersistenceDirectoryOverride = blocker;
                try
                {
                    var failed = BulkRename.Preview(after, new(BulkRenameMode.Affixes, "", "", "Failed ", ""));
                    Require(!vm.TryRenameDashboardSelection(failed), kind + " accepted a failed rename write.");
                    Require(vm.SelectedDashboardRenameTargets().Select(target => target.Name).Order().SequenceEqual(after.Select(target => target.Name).Order()) && File.ReadAllText(Path.Combine(store, filename)) == persisted,
                        kind + " failed rename did not roll back source names and preserve the store.");
                }
                finally { vm.PersistenceDirectoryOverride = store; }
                workspace.CloseAfterPreflight(); Pump(150);
                vm.DashboardSelection.End();
                checks.Add($"{theme}/{mode}/{kind}: selection entry audit, shared rename, persisted titles, failed-write rollback and clean/dirty tab preservation passed.");
            }
            // A selection in an overlay owns Escape independently of the dashboard behind it.
            vm.DashboardSelection.SelectAll(); int dashboardCount = vm.DashboardSelection.Count;
            vm.TrashedNotes.Add(new NoteCardViewModel(new NoteDocument { Title = "Deleted fixture", IsDeleted = true, DeletedAt = DateTime.UtcNow }, _ => { }));
            var trash = window.TrashPanelControl; trash.ShowAnimated(); Pump(500);
            var list = (ListBox)trash.FindName("TrashedItemsListBox");
            WaitLoading(() => list.Items.Count > 0); list.SelectedItems.Add(list.Items[0]); Pump(150);
            var trashScope = CompletionField<ICollectionSelectionScope>(trash, "_trashSelection");
            var dock = (CollectionSelectionDock)trash.FindName("TrashSelectionDock");
            Require(SelectionKey((Button)dock.FindName("DoneButton"), window, Key.Escape).Handled && !trashScope.IsActive && vm.DashboardSelection.Count == dashboardCount,
                "Trash Escape changed the dashboard selection behind the overlay.");
            trash.HideAnimated(); WaitLoading(() => !trash.IsVisible); vm.DashboardSelection.End();
            checks.Add(theme + ": overlay selection keyboard ownership passed with dashboard selection still active behind Trash.");
        }
        finally { window.EditorWorkspace.Window?.CloseAfterPreflight(); CloseLoadingWindow(window); }
    }
}
