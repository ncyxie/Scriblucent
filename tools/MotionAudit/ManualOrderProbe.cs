using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Rendering;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

internal static partial class Program
{
    private const BindingFlags ReorderFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void ProbeManualOrder(string? only, bool editorsOnly)
    {
        _wake16 = true;
        var report = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var mode in new[] { MotionPreference.Full, MotionPreference.Reduced, MotionPreference.Off })
        {
            if (only is not null && only != $"{theme}/{mode}") continue;
            LoadNativeControlResources(theme); ThemeManager.SetTheme(theme); LocalizationService.SetCulture(theme == "Light" ? "en" : "lt");
            // Other audit scenes use an empty note-menu placeholder. This probe
            // needs the real menu declarations, without invoking App event handlers.
            var appMarkup = XDocument.Load("Scriblucent/App.xaml");
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            var noteMenu = new XElement(appMarkup.Descendants().Single(element => (string?)element.Attribute(xaml + "Key") == "NoteCardContextMenu"));
            noteMenu.Attribute(xaml + "Key")!.Remove();
            foreach (var click in noteMenu.Descendants().Attributes("Click").ToArray()) click.Remove();
            _app.Resources["NoteCardContextMenu"] = (ContextMenu)Fragment(noteMenu, appMarkup.Root!);
            if (!editorsOnly) ProbeDashboardOrder(theme, mode, report);
            if (editorsOnly)
            {
                AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
                typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null,
                    new AppSettings { EnableAutoSave = false, EnableAiFeatures = false });
            }
            ProbeEditorOrder(theme, mode, report);
        }
        Require(report.Count != 0, "No manual-order case matched.");
        File.WriteAllLines(Path.Combine(_output, "manual-order-checks.txt"), report);
        foreach (var line in report) Console.WriteLine(line);
    }
    private static object ReorderState(ItemsControl list)
    {
        var property = (DependencyProperty)typeof(ItemReorderBehavior).GetField("StateProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var state = list.GetValue(property)!;
        Require(state is not null, "The actual list did not attach reordering."); return state!;
    }
    private static DataObject ReorderData(ItemsControl list, IReadOnlyList<object> items)
    {
        var state = ReorderState(list);
        var adapter = state.GetType().GetField("_adapter", ReorderFlags)!.GetValue(state);
        Require(adapter is not null, "The compiled list did not resolve its reorder owner.");
        var payloadType = typeof(ItemReorderBehavior).GetNestedType("Payload", BindingFlags.NonPublic)!;
        var payload = Activator.CreateInstance(payloadType, ReorderFlags, null, [adapter, items], null)!;
        var data = new DataObject(); data.SetData("Scriblucent.ItemReorder", payload);
        if (ItemReorderBehavior.GetKind(list) == ItemReorderKind.DashboardItems && items.Count == 1) data.SetData(items[0].GetType(), items[0]);
        return data;
    }
    private static DragEventArgs ReorderEvent(ItemsControl list, FrameworkElement container, DataObject data, Point point, RoutedEvent routed)
    {
        var constructor = typeof(DragEventArgs).GetConstructors(ReorderFlags).Single(ctor => ctor.GetParameters().Length == 5);
        var args = (DragEventArgs)constructor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, list, point]);
        args.RoutedEvent = routed; container.RaiseEvent(args); return args;
    }
    private static void RoutedReorder(ItemsControl list, IReadOnlyList<object> moving, object anchor, bool after, string? capture = null, bool cancel = false, bool centre = false)
    {
        list.UpdateLayout();
        var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(anchor)!;
        Require(container is not null && container.IsVisible, "The destination was not realized.");
        bool vertical = ItemReorderBehavior.GetKind(list) is ItemReorderKind.DashboardGroups or ItemReorderKind.QuizQuestions or ItemReorderKind.QuizOptions
            || list is DashboardVirtualizedCardGrid { Layout: DashboardCardGridLayout.VerticalList };
        var bounds = container!.TransformToAncestor(list).TransformBounds(new Rect(container.RenderSize));
        var point = vertical ? new Point(bounds.Left + bounds.Width / 2, after ? bounds.Bottom - 3 : bounds.Top + 3)
            : new Point(after ? bounds.Right - 3 : bounds.Left + 3, bounds.Top + bounds.Height / 2);
        if (centre) point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        var data = ReorderData(list, moving);
        var over = ReorderEvent(list, container, data, point, DragDrop.PreviewDragOverEvent);
        Require(over.Handled && over.Effects == DragDropEffects.Move && ItemReorderBehavior.IsInsertionActive,
            $"Routed drag did not choose insertion before grouping: kind={ItemReorderBehavior.GetKind(list)}, handled={over.Handled}, effects={over.Effects}.");
        Require(ReorderState(list).GetType().GetField("_adorner", ReorderFlags)!.GetValue(ReorderState(list)) is System.Windows.Documents.Adorner,
            "Insertion accepted a destination without presenting its marker.");
        if (capture is not null)
        {
            var window = Window.GetWindow(list)!;
            // Include the window template's AdornerLayer, which can be a sibling
            // of ContentPresenter rather than a descendant of editor content.
            var root = VisualTreeHelper.GetChild(window, 0) as FrameworkElement ?? (FrameworkElement)window.Content;
            SaveControlCapture(root, capture);
        }
        if (cancel) ReorderState(list).GetType().GetMethod("Clear", ReorderFlags)!.Invoke(ReorderState(list), null);
        else
        {
            var drop = ReorderEvent(list, container, data, point, DragDrop.PreviewDropEvent);
            Require(drop.Handled && drop.Effects == DragDropEffects.Move,
                $"The routed drop did not commit its insertion: kind={ItemReorderBehavior.GetKind(list)}, moving={string.Join(",", moving.Select(item => list.Items.IndexOf(item)))}, anchor={list.Items.IndexOf(anchor)}, after={after}, centre={centre}.");
        }
        Pump(32);
        var active = typeof(ReorderMotion).GetField("Active", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var realized in ItemReorderBehavior.Realized(list))
        {
            bool movingItem = moving.Contains(list.ItemContainerGenerator.ItemFromContainer(realized));
            if (!movingItem && !UiMotion.IsMotionDisabled) continue;
            Require(!(bool)active.GetType().GetMethod("TryGetValue")!.Invoke(active, [realized, null])!,
                "Dropped items or reduced-motion neighbours retained spatial settling.");
        }
        Pump(300);
        Require(!ItemReorderBehavior.IsInsertionActive, "The insertion marker survived drop/cancellation.");
        WaitLoading(() => ItemReorderBehavior.Realized(list).All(item => !(bool)active.GetType().GetMethod("TryGetValue")!.Invoke(active, [item, null])!), 2500);
        var unfinished = new List<string>();
        foreach (var containerNow in ItemReorderBehavior.Realized(list))
        {
            object?[] lookup = [containerNow, null];
            if (!(bool)active.GetType().GetMethod("TryGetValue")!.Invoke(active, lookup)!) continue;
            var running = lookup[1]!;
            var offset = (TranslateTransform)running.GetType().GetProperty("Offset")!.GetValue(running)!;
            unfinished.Add($"{containerNow.GetType().Name} visible={containerNow.IsVisible}, offset={offset.X}/{offset.Y}, animated={offset.HasAnimatedProperties}, attached={ReferenceEquals(containerNow.RenderTransform, running.GetType().GetProperty("Group")!.GetValue(running))}");
        }
        Require(unfinished.Count == 0, "Unfinished reorder: " + string.Join("; ", unfinished));
    }
    private static void ProbeDashboardOrder(string theme, MotionPreference mode, List<string> report)
    {
        var (window, model, store) = LoadingWindow(theme, mode, 1000);
        model.IsRecentSectionVisible = false; model.IsCalendarSectionExpanded = false; model.IsCalendarSectionVisible = false;
        // The loading fixture deliberately varies section order by motion preference.
        // Ordering starts at the groups in every case, then scrolls the real viewport.
        model.IsGroupsFirst = true; model.IsFlashcardDashboardGroupsFirst = true;
        model.IsMindMapDashboardGroupsFirst = true; model.IsQuizDashboardGroupsFirst = true;
        var group = Guid.NewGuid(); var secondGroup = Guid.NewGuid();
        Guid? Group(int i) => i < 4 ? group : i < 8 ? secondGroup : null;
        File.WriteAllText(Path.Combine(store, "notes.json"), JsonSerializer.Serialize(new NotesStoreData
        {
            Notes = Enumerable.Range(0, 1000).Select(i => new NoteDocument { Title = $"Planning {i:D4}", Content = "A short thought.", GroupId = Group(i), LastModified = new DateTime(2026, 1, 1).AddMinutes(i) }).ToList(),
            Groups = [new() { GroupId = group, Name = "First group", SortOrder = 0 }, new() { GroupId = secondGroup, Name = "Second group", SortOrder = 1 }]
        }));
        File.WriteAllText(Path.Combine(store, "flashcard-sets.json"), JsonSerializer.Serialize(new FlashcardSetStoreData
        {
            Sets = Enumerable.Range(0, 1000).Select(i => new FlashcardSetDocument { Title = $"Practice {i:D4}", GroupId = Group(i) }).ToList(),
            Groups = [new() { GroupId = group, Name = "First group", SortOrder = 0 }, new() { GroupId = secondGroup, Name = "Second group", SortOrder = 1 }]
        }));
        File.WriteAllText(Path.Combine(store, "mind-maps.json"), JsonSerializer.Serialize(new MindMapStoreData
        {
            Maps = Enumerable.Range(0, 1000).Select(i => { var item = NewDocumentDefaults.MindMap(); item.Title = $"Explore {i:D4}"; item.GroupId = Group(i); return item; }).ToList(),
            Groups = [new() { GroupId = group, Name = "First group", SortOrder = 0 }, new() { GroupId = secondGroup, Name = "Second group", SortOrder = 1 }]
        }));
        File.WriteAllText(Path.Combine(store, "quizzes.json"), JsonSerializer.Serialize(new QuizStoreData
        {
            Quizzes = Enumerable.Range(0, 1000).Select(i => new QuizDocument { Title = $"Knowledge {i:D4}", GroupId = Group(i) }).ToList(),
            Groups = [new() { GroupId = group, Name = "First group", SortOrder = 0 }, new() { GroupId = secondGroup, Name = "Second group", SortOrder = 1 }]
        }));
        try
        {
            window.Show(); window.Activate();
            foreach (var kind in Enum.GetValues<EditorDocumentKind>())
            {
                SelectLoadingDashboard(model, kind);
                WaitLoading(() => model.IsActiveDashboardReady && ((FrameworkElement)window.FindName("DashboardLoadingLayer")).Visibility == Visibility.Collapsed);
                WaitLoading(() => !window.IsDashboardTransitionActive);
                Pump(250);
                var page = (FrameworkElement)window.FindName(model.ActiveDashboard + "DashboardView");
                ProbeDashboardSectionOrdering(page, model.ActiveDashboard, report.Add);
                var scope = kind switch { EditorDocumentKind.Flashcards => DashboardMotionScope.Flashcards, EditorDocumentKind.MindMap => DashboardMotionScope.MindMaps, EditorDocumentKind.Quiz => DashboardMotionScope.Quizzes, _ => DashboardMotionScope.Notes };
                var grids = ControlTree(page).OfType<DashboardVirtualizedCardGrid>().ToArray();
                var grouped = grids.First(grid => grid.Items.Count == 4);
                var groupedItems = grouped.Items.Cast<object>().ToArray();
                RoutedReorder(grouped, [groupedItems[3]], groupedItems[0], false, $"order-{theme}-{mode}-{kind}-group");
                Require(ReferenceEquals(grouped.Items[0], groupedItems[3]), "Group order snapped back to date order.");
                var otherGroup = grids.First(grid => !ReferenceEquals(grid, grouped) && grid.Items.Count == 4);
                var otherAnchor = otherGroup.Items[0];
                var scroll = ControlTree(page).OfType<ScrollViewer>().Single(viewer => viewer.Name == model.ActiveDashboard + "ScrollViewer");
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + otherGroup.TranslatePoint(new Point(), scroll).Y); Pump(250);
                RoutedReorder(otherGroup, [groupedItems[2]], otherAnchor, false);
                Require(ReferenceEquals(otherGroup.Items[0], groupedItems[2]) && grouped.Items.Count == 3
                    && MainViewModel.DashboardOrderItem.From(groupedItems[2]).Group == MainViewModel.DashboardOrderItem.From(otherAnchor).Group,
                    "Cross-group insertion lost membership or its destination.");
                var groupsList = ControlTree(page).OfType<ItemsControl>().Single(list => ItemReorderBehavior.GetKind(list) == ItemReorderKind.DashboardGroups);
                scroll.ScrollToHome(); Pump(200);
                var groups = groupsList.Items.Cast<object>().ToArray();
                RoutedReorder(groupsList, [groups[1]], groups[0], false);
                Require(ReferenceEquals(groupsList.Items[0], groups[1]), "Group container drag did not reorder headers.");
                ProbeReorderMenus(groupsList);
                ProbeDirectItemSteps(groupsList);
                scroll.ScrollToEnd(); Pump(250);
                var ungrouped = grids.Single(grid => grid.Items.Count > 900);
                Require(ItemReorderBehavior.Realized(ungrouped).Count() < 100, "Dashboard drag realized the full library.");
                ProbeReorderMenus(ungrouped);
                var visible = ItemReorderBehavior.Realized(ungrouped).Select(container => ungrouped.ItemContainerGenerator.ItemFromContainer(container))
                    .OrderBy(item => ungrouped.Items.IndexOf(item)).TakeLast(5).ToArray();
                Require(visible.Length >= 4, "Dashboard end did not realize destinations.");
                var moving = visible[^1]; var anchor = visible[^3];
                var before = ungrouped.Items.Cast<object>().ToArray();
                RoutedReorder(ungrouped, [moving], anchor, false, cancel: true);
                Require(before.SequenceEqual(ungrouped.Items.Cast<object>()), "Cancelled insertion changed data.");
                RoutedReorder(ungrouped, [moving], anchor, false);
                Require(ungrouped.Items.IndexOf(moving) + 1 == ungrouped.Items.IndexOf(anchor) && model.DashboardSortKey(scope) == MainViewModel.SortManual,
                    "Ungrouped insertion or Manual activation failed.");
                model.DashboardSelection.Begin(MainViewModel.DashboardOrderItem.From(visible[0]).Id);
                model.DashboardSelection.Toggle(MainViewModel.DashboardOrderItem.From(visible[1]).Id);
                Require(model.DashboardDragItems(visible[0]).Count == 2, "Dashboard dragging did not include its selection.");
                var selectedBlock = model.DashboardDragItems(visible[0]);
                RoutedReorder(ungrouped, selectedBlock, moving, true, centre: true);
                Require(ungrouped.Items.IndexOf(selectedBlock[0]) == ungrouped.Items.IndexOf(moving) + 1
                    && ungrouped.Items.IndexOf(selectedBlock[1]) == ungrouped.Items.IndexOf(moving) + 2,
                    "A centre drop split the selected block or fell through to single-card grouping.");
                model.DashboardSelection.End();
                if (_pointerControls)
                {
                    Pump(400); scroll.ScrollToEnd(); Pump(220);
                    var hitCards = ItemReorderBehavior.Realized(ungrouped).Where(container =>
                    {
                        var point = container.TranslatePoint(new Point(container.ActualWidth * .4, container.ActualHeight * .5), window);
                        var hit = window.InputHitTest(point) as DependencyObject;
                        return hit is not null && ReferenceEquals(ItemsControl.ContainerFromElement(ungrouped, hit), container);
                    }).OrderBy(container => Math.Abs(container.TranslatePoint(new Point(0, container.ActualHeight * .5), scroll).Y - scroll.ViewportHeight / 2))
                        .ThenBy(container => container.TranslatePoint(new Point(), scroll).X)
                        .Select(container => ungrouped.ItemContainerGenerator.ItemFromContainer(container)).Take(3).ToArray();
                    if (hitCards.Length < 2) SaveControlCapture((FrameworkElement)window.Content, "pointer-dashboard-layout-failure");
                    Require(hitCards.Length >= 2, "No unobscured cards for actual dashboard dragging: " + string.Join("; ", ItemReorderBehavior.Realized(ungrouped).TakeLast(6).Select(container =>
                    {
                        var point = container.TranslatePoint(new Point(container.ActualWidth * .4, container.ActualHeight * .5), window);
                        return $"{point}: {window.InputHitTest(point)?.GetType().Name ?? "null"}";
                    })));
                    ProbeReorderPointer(ungrouped, hitCards[1], hitCards[0], $"pointer-{theme}-{mode}-dashboard-{kind}");
                    ProbeReorderPointer(ungrouped, hitCards[1], hitCards[0], $"pointer-{theme}-{mode}-dashboard-{kind}-group", grouping: true);
                    ProbeDragScrolling(ungrouped, scroll, (CollectionSelectionDock)window.FindName("DashboardSelectionDock"), model.DashboardSelection, $"drag-scroll-{theme}-{mode}-{kind}");
                    report.Add($"{theme}/{mode}/{kind}: native mouse insertion, centre-drop grouping and single/block outside-window drag scrolling passed; pointer restored.");
                }
                report.Add($"{theme}/{mode}/{kind}: 1,000-item real library, nested routed insertion, group-container order, cancellation, selected block and bounded realization passed.");
            }
            ProbeSettingsOrdering(window, theme, mode, report);
            model.FlushPendingNotesSaveBeforeExit(TimeSpan.FromSeconds(5));
        }
        finally { CloseLoadingWindow(window); }
        // A fresh real startup must use the persisted IDs rather than the stored document/date sequence.
        var settings = AppSettingsService.Load();
        var reopened = new MainWindow(settings) { Width = 1280, Height = 900, ShowInTaskbar = false };
        _app.MainWindow = reopened; var restored = (MainViewModel)reopened.DataContext; restored.PersistenceDirectoryOverride = store;
        try
        {
            reopened.Show();
            foreach (var kind in Enum.GetValues<EditorDocumentKind>())
            {
                SelectLoadingDashboard(restored, kind); WaitLoading(() => restored.IsActiveDashboardReady); Pump(100);
                var file = kind switch { EditorDocumentKind.Flashcards => "flashcard-sets.json", EditorDocumentKind.MindMap => "mind-maps.json", EditorDocumentKind.Quiz => "quizzes.json", _ => "notes.json" };
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(store, file)));
                var ids = json.RootElement.GetProperty("ManualOrder").EnumerateArray().Select(item => item.GetGuid()).ToArray();
                var page = (FrameworkElement)reopened.FindName(restored.ActiveDashboard + "DashboardView");
                var list = ControlTree(page).OfType<DashboardVirtualizedCardGrid>().Single(grid => grid.Items.Count > 900);
                var liveIds = list.Items.Cast<object>().Select(item => MainViewModel.DashboardOrderItem.From(item).Id).ToArray();
                Require(ids.Where(liveIds.ToHashSet().Contains).SequenceEqual(liveIds), "Cold startup lost the saved manual order: " + kind);
            }
        }
        finally { CloseLoadingWindow(reopened); }
        report.Add($"{theme}/{mode}: fresh startup restored all four manual arrangements.");
    }
    private static void ProbeReorderMenus(ItemsControl list)
    {
        var container = ItemReorderBehavior.Realized(list).First();
        var owners = ControlTree(container).OfType<FrameworkElement>().Where(element => element.ContextMenu is not null).ToArray();
        var primary = owners.First(element => ItemReorderBehavior.GetMenuActions(element.ContextMenu!));
        var menu = primary.ContextMenu!;
        ItemReorderBehavior.PrepareContextMenu(menu, primary);
        int count = menu.Items.Count;
        ItemReorderBehavior.PrepareContextMenu(menu, primary);
        Require(menu.Items.Count == count && menu.Items.OfType<MenuItem>().Count(item =>
            Equals(item.Header, LocalizationService.GetString("ReorderEarlier")) || Equals(item.Header, LocalizationService.GetString("ReorderLater"))) == 2,
            "The real item menu lost or duplicated its reorder actions.");
        ItemReorderBehavior.PrepareContextMenu(menu, new Border());
        Require(menu.Items.Count < count, "A shared menu retained commands from its previous list owner.");
        ItemReorderBehavior.PrepareContextMenu(menu, primary);
        Require(menu.Items.Count == count, "The item menu did not recover after an unowned opening.");
        foreach (var owner in owners.Where(element => !ItemReorderBehavior.GetMenuActions(element.ContextMenu!)))
        {
            int original = owner.ContextMenu!.Items.Count;
            ItemReorderBehavior.PrepareContextMenu(owner.ContextMenu, owner);
            Require(owner.ContextMenu.Items.Count == original, "Reorder actions leaked into a nested picker.");
        }
    }
    private static void ProbeEditorOrder(string theme, MotionPreference mode, List<string> report)
    {
        MotionPolicy.Configure(mode, false);
        // Enough extent to measure sustained outside-window acceleration without
        // hitting the bottom of the library halfway through the native gesture.
        var originals = Enumerable.Range(0, 160).Select(i => new FlashcardItem { Question = $"Question {i}", Answer = $"Answer {i}",
            DueDate = new DateTime(2025, 1, 1).AddDays(160 - i), EaseFactor = 2.7, IsStarred = i == 2, Hint = "Authored hint" }).ToArray();
        var flashcards = new FlashcardsPreviewWindow(originals, title: "Arrange your flashcards", studyDueCardsOnly: false) { Width = 1180, Height = 850, ShowInTaskbar = false };
        using (var fixture = new CompletionWindow(flashcards))
        {
            try
            {
                flashcards.MarkCurrentStateAutoSaved();
                var list = (ItemsControl)flashcards.FindName("FlashcardsItemsControl");
                Require(ControlTree(list).OfType<ItemReorderGrip>().Any(grip => grip.IsVisible && grip.IsHitTestVisible), "Flashcard cache hid the drag handle.");
                Require(!ControlTree(list).OfType<ItemReorderControls>().Any(), "Flashcard grid duplicated its drag grips with list-style arrow controls.");
                var items = list.Items.Cast<object>().ToArray();
                ProbeFlashcardMarkerGeometry(flashcards, list, items, $"marker-{theme}-{mode}-viewport");
                report.Add($"{theme}/{mode}: flashcard insertion marker passed adaptive geometry, viewport clipping and rejection of drops outside the card viewport.");
                RoutedReorder(list, [items[4]], items[1], false, $"order-{theme}-{mode}-flashcards");
                Require(flashcards.WorkspaceDirty && flashcards.GetFlashcardItems()[1].Id == originals[4].Id, "Flashcard authored order/save state did not change.");
                flashcards.BulkSelection.Begin();
                flashcards.BulkSelection.SelectOnly([originals[0].Id, originals[2].Id]);
                var moving = flashcards.CreateCardReorderAdapter().DraggedItems(items[0]);
                RoutedReorder(list, moving, items[3], false);
                var ids = flashcards.GetFlashcardItems().Select(item => item.Id).ToArray();
                Require(Array.IndexOf(ids, originals[0].Id) + 1 == Array.IndexOf(ids, originals[2].Id) && flashcards.BulkSelection.Count == 2, "Flashcard block order or selection was lost.");
                Require(flashcards.GetFlashcardItems().All(item => item.EaseFactor == 2.7 && item.Hint == "Authored hint"), "Reordering changed study/authored metadata.");
                flashcards.BulkSelection.End();
                var grip = ControlTree((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0)).OfType<ItemReorderGrip>().First(button => button.IsVisible);
                var menu = grip.ContextMenu!;
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    UiMotion.OpenContextMenu(menu, grip); Pump(80);
                    Require(menu.Items.OfType<MenuItem>().Count() == 2, "Programmatic context opening lost or duplicated reorder actions.");
                    menu.IsOpen = false; Pump(80);
                }
                Require(ItemReorderBehavior.MoveFromHandle(grip, true), "The keyboard/handle move command was disconnected."); Pump(300);
                var search = (TextBox)flashcards.FindName("FlashcardSearchTextBox");
                search.Text = "Question 1"; Pump(280);
                var filtered = list.Items.Cast<FlashcardsPreviewWindow.FlashcardPreviewItem>().ToArray();
                var visibleIds = filtered.Select(item => item.Id).ToHashSet();
                var hiddenBefore = flashcards.GetFlashcardItems().Where(item => !visibleIds.Contains(item.Id)).Select(item => item.Id).ToArray();
                RoutedReorder(list, [filtered[3]], filtered[0], false);
                Require(hiddenBefore.SequenceEqual(flashcards.GetFlashcardItems().Where(item => !visibleIds.Contains(item.Id)).Select(item => item.Id)), "Filtered insertion reordered hidden flashcards.");
                search.Clear(); Pump(280);
                var authored = flashcards.GetFlashcardItems().Select(item => item.Id).ToArray();
                var dueOnly = (CheckBox)flashcards.FindName("DueOnlyFilterCheckBox");
                dueOnly.IsChecked = true; dueOnly.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(100);
                Require(authored.SequenceEqual(list.Items.Cast<FlashcardsPreviewWindow.FlashcardPreviewItem>().Select(item => item.Id)), "Due filtering overrode authored grid order.");
                ((FlashcardsPreviewWindow.FlashcardPreviewItem)list.Items[0]).IsKnown = true;
                var continueStudy = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                continueStudy.Tick += (_, _) =>
                {
                    if (_app.Windows.OfType<ModernMessageDialog>().FirstOrDefault() is not { } dialog) return;
                    continueStudy.Stop(); SelectionClick((Button)dialog.FindName("NoButton"));
                };
                var start = (Button)flashcards.FindName("StartStudyModeButton");
                continueStudy.Start();
                try { SelectionClick(start); }
                finally { continueStudy.Stop(); }
                Pump(180);
                Require(!flashcards.CreateCardReorderAdapter().IsEnabled, "Study mode allowed structural dragging.");
                ItemReorderBehavior.PrepareContextMenu(menu, grip);
                Require(menu.Items.Count == 0, "Study mode retained stale reorder commands.");
                var dueDates = list.Items.Cast<FlashcardsPreviewWindow.FlashcardPreviewItem>().Select(item => item.DueDate).ToArray();
                Require(dueDates.Distinct().Count() > 2 && dueDates.SequenceEqual(dueDates.Order()), $"Starting study lost due-date priority: distinct={dueDates.Distinct().Count()}, dueOnly={typeof(FlashcardsPreviewWindow).GetField("_dueOnly", ReorderFlags)!.GetValue(flashcards)}, dates={string.Join(",", dueDates.Take(12))}.");
                SelectionClick(start); Pump(180);
                Require(authored.SequenceEqual(list.Items.Cast<FlashcardsPreviewWindow.FlashcardPreviewItem>().Select(item => item.Id)), "Leaving study did not restore authored order.");
                var adapter = flashcards.CreateCardReorderAdapter();
                for (int burst = 0; burst < 12; burst++)
                {
                    var first = list.Items[0]; var second = list.Items[1];
                    Require(ReorderMotion.Commit(list, () => adapter.Move([second], first, false)), "An interrupted move was dropped.");
                }
                Pump(500);
                Require(flashcards.GetFlashcardItems().Select(item => item.Id).SequenceEqual(authored), "Rapid opposite moves drifted the saved sequence.");
                if (_pointerControls)
                {
                    ProbeDragScrolling(list, ControlTree(list).OfType<ScrollViewer>().First(), (CollectionSelectionDock)flashcards.FindName("BulkSelectionBar"), flashcards.BulkSelection, $"drag-scroll-{theme}-{mode}-editor-flashcards");
                    ProbeReorderPointer(list, list.Items[1], list.Items[0], $"pointer-{theme}-{mode}-editor-flashcards");
                    ProbeReorderPointer(list, list.Items[1], list.Items[0], $"pointer-{theme}-{mode}-editor-flashcards-cancel", cancel: true);
                    ProbeReorderPointer(list, list.Items[1], list.Items[0], $"pointer-{theme}-{mode}-editor-flashcards-recycle", cancel: true, recycle: true);
                    report.Add($"{theme}/{mode}: native flashcard drop, cancellation and source recycling retained opaque live faces and scroll tiles.");
                }
            }
            finally { flashcards.MarkCurrentStateAutoSaved(); }
        }
        flashcards.DisposeWorkspaceEditor();
        var quiz = new QuizPreviewWindow(new QuizDocument { Title = "Arrange your quiz", Questions = Enumerable.Range(0, 12).Select(i => new QuizQuestion
        { Question = $"Question {i}", Options = [new() { Text = "Correct", IsCorrect = true }, new() { Text = "Second" }, new() { Text = "Third" }] }).ToList() }, noteOptions: null)
        { Width = 1180, Height = 900, ShowInTaskbar = false, IsEditMode = true };
        using (var fixture = new CompletionWindow(quiz))
        {
            try
            {
                quiz.MarkCurrentStateAutoSaved(); Pump(150);
                var list = (ItemsControl)quiz.FindName("QuestionsItemsControl"); var items = list.Items.Cast<object>().ToArray();
                RoutedReorder(list, [items[1]], items[0], false, $"order-{theme}-{mode}-quiz");
                Require(quiz.WorkspaceDirty && ReferenceEquals(list.Items[0], items[1]), "Quiz question insertion did not change authored order.");
                var options = ControlTree(list).OfType<ItemsControl>().First(control => ItemReorderBehavior.GetKind(control) == ItemReorderKind.QuizOptions && control.IsVisible);
                var answers = options.Items.Cast<object>().ToArray();
                RoutedReorder(options, [answers[0]], answers[2], true);
                Require(ReferenceEquals(options.Items[2], answers[0]) && ((QuizPreviewWindow.QuizPreviewOption)options.Items[2]).IsCorrect,
                    "Option insertion lost its correct-answer identity.");
                ProbeDirectItemSteps(options);
                quiz.BulkSelection.Begin();
                quiz.BulkSelection.SelectOnly([((QuizPreviewWindow.QuizPreviewQuestion)items[1]).Id, ((QuizPreviewWindow.QuizPreviewQuestion)items[2]).Id]);
                var selected = quiz.CreateQuestionReorderAdapter().DraggedItems(items[1]);
                RoutedReorder(list, selected, items[0], false);
                Require(ReferenceEquals(list.Items[0], items[1]) && ReferenceEquals(list.Items[1], items[2]) && quiz.BulkSelection.Count == 2,
                    "Quiz selection did not move as one retained block.");
                var selectedControls = ControlTree((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(0)).OfType<ItemReorderControls>().First();
                Pump(100);
                Require(!selectedControls.EarlierButton.IsEnabled && selectedControls.LaterButton.IsEnabled, "Selected-block arrows ignored its boundary.");
                InvokeOrderingButton(selectedControls.LaterButton); Pump(300);
                Require(ReferenceEquals(list.Items[1], items[1]) && ReferenceEquals(list.Items[2], items[2]) && quiz.BulkSelection.Count == 2, "Direct arrow split or deselected the question block.");
                selectedControls = ControlTree((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(1)).OfType<ItemReorderControls>().First();
                InvokeOrderingButton(selectedControls.EarlierButton); Pump(300);
                Require(ReferenceEquals(list.Items[0], items[1]) && ReferenceEquals(list.Items[1], items[2]), "Direct block movement could not reverse.");
                quiz.BulkSelection.End();
                ProbeDirectItemSteps(list);
                var secondFrame = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(1);
                var questionGrip = ControlTree(secondFrame).OfType<ItemReorderGrip>().First();
                InvokeOrderingButton(questionGrip); Pump(100);
                Require(questionGrip.ContextMenu.IsOpen && questionGrip.ContextMenu.Items.Count == 2, "Clicking the question grip did not open its precise move menu.");
                var earlier = (MenuItem)questionGrip.ContextMenu.Items[0];
                Require(earlier.IsEnabled, "The question grip disabled a valid earlier move.");
                earlier.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); questionGrip.ContextMenu.IsOpen = false; Pump(350);
                Require(ReferenceEquals(list.Items[0], items[2]), "The quiz grip menu disagreed with drag ordering.");
                var preview = ItemReorderPreview.Create(list, items[0], 1);
                var blockPreview = ItemReorderPreview.Create(list, items[0], 12);
                Require(preview.ActualWidth < 320 && blockPreview.ActualHeight > preview.ActualHeight && blockPreview.ActualHeight <= 120,
                    "Compact drag previews ignore their content or selection count.");
                SaveControlCapture(blockPreview, $"order-{theme}-{mode}-block-preview");
                if (_pointerControls) ProbeReorderPointer(list, list.Items[1], list.Items[0], $"pointer-{theme}-{mode}-editor-questions");
            }
            finally { quiz.MarkCurrentStateAutoSaved(); }
        }
        quiz.DisposeWorkspaceEditor();
        report.Add($"{theme}/{mode}: compiled editor grips, routed question/answer insertion, flashcard blocks, saved sequence, dirty status and metadata retention passed.");
        if (_pointerControls) report.Add($"{theme}/{mode}: native mouse dragging passed in both editors; pointer restored.");
    }
    private static void ProbeFlashcardMarkerGeometry(Window window, ItemsControl list, object[] items, string capture)
    {
        GetCursorPos(out var geometryPointer);
        if (_pointerControls)
        {
            var chrome = window.PointToScreen(new Point(300, 12));
            SetCursorPos((int)chrome.X, (int)chrome.Y);
        }
        var state = ReorderState(list);
        var data = ReorderData(list, [items[7]]);
        Rect Face(object item)
        {
            var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(item);
            var face = ControlTree(container).OfType<Border>().First(element => element.Name == "CardSurface" && element.IsVisible);
            return face.TransformToAncestor(list).TransformBounds(new Rect(face.RenderSize));
        }
        (Point Start, Point End) Marker(object item, bool after)
        {
            var face = Face(item);
            var container = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(item);
            var point = new Point(after ? face.Right - 2 : face.Left + 2, face.Top + face.Height / 2);
            var over = ReorderEvent(list, container, data, point, DragDrop.PreviewDragOverEvent);
            Require(over.Handled && over.Effects == DragDropEffects.Move, "The measured card edge rejected insertion.");
            var adorner = state.GetType().GetField("_adorner", ReorderFlags)!.GetValue(state)!;
            var type = adorner.GetType();
            var bounds = (Rect)type.GetField("_bounds", ReorderFlags)!.GetValue(adorner)!;
            bool selectedAfter = (bool)type.GetField("_after", ReorderFlags)!.GetValue(adorner)!;
            double x = (double?)type.GetField("_markerX", ReorderFlags)?.GetValue(adorner)
                ?? (selectedAfter ? bounds.Right - 4 : bounds.Left + 2);
            state.GetType().GetMethod("Clear", ReorderFlags)!.Invoke(state, null);
            return (new(x, bounds.Top + 8), new(x, bounds.Bottom - 8));
        }
        (Point Start, Point End) CheckGap(object leftItem, object rightItem)
        {
            var left = Face(leftItem); var right = Face(rightItem);
            var fromLeft = Marker(leftItem, true); var fromRight = Marker(rightItem, false);
            double centre = (left.Right + right.Left) / 2;
            Require(Math.Abs(fromLeft.Start.X - centre) < .75 && Math.Abs(fromRight.Start.X - centre) < .75
                && (fromLeft.Start - fromRight.Start).Length < .75 && (fromLeft.End - fromRight.End).Length < .75,
                $"Flashcard marker disagrees across the same gap at width {window.Width}: after={fromLeft.Start.X}, before={fromRight.Start.X}, centre={centre}.");
            return fromRight;
        }
        double originalWidth = window.Width;
        var viewer = ControlTree(list).OfType<ScrollViewer>().Single(element => element.Name == "PART_ScrollViewer");
        try
        {
            foreach (double width in new[] { 1180d, 840d, 720d })
            {
                window.Width = width; Pump(140); list.UpdateLayout();
                var first = Face(items[0]);
                int columns = items.Take(6).TakeWhile(item => Math.Abs(Face(item).Top - first.Top) < 1).Count();
                var before = Marker(items[0], false);
                var last = Face(items[columns - 1]);
                var after = Marker(items[columns - 1], true);
                Require(before.Start.X < first.Left && after.Start.X > last.Right, "Row-boundary markers overlap a flashcard face.");
                Require(Math.Abs(before.Start.Y - first.Top - 8) < .75 && Math.Abs(before.End.Y - first.Bottom + 8) < .75,
                    "The insertion line follows padded container height instead of the card face.");
                if (columns < 2) continue;
                var fromRight = CheckGap(items[0], items[1]);
                var frame = ControlTree((FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(items[1])).OfType<CollectionSelectionFrame>().First();
                var hover = UiMotion.ChannelTranslate(frame, MotionChannel.Interaction); double saved = hover.Y;
                try
                {
                    hover.Y = -5;
                    var lifted = Marker(items[1], false);
                    Require((lifted.Start - fromRight.Start).Length < .75 && (lifted.End - fromRight.End).Length < .75,
                        "Hover lift moved the insertion marker away from its layout gap.");
                }
                finally { hover.Y = saved; }
            }
            window.Width = originalWidth; Pump(140);
            var flipped = (FlashcardsPreviewWindow.FlashcardPreviewItem)items[1];
            try { flipped.IsFlipped = true; Pump(140); CheckGap(items[0], items[1]); }
            finally { flipped.IsFlipped = false; Pump(140); }
            viewer.ScrollToVerticalOffset(1056); Pump(180);
            var visible = ItemReorderBehavior.Realized(list).Select(container => list.ItemContainerGenerator.ItemFromContainer(container))
                .OrderBy(item => list.Items.IndexOf(item)).Where(item => Face(item).Top >= 0 && Face(item).Bottom <= list.ActualHeight).Take(2).ToArray();
            Require(visible.Length == 2 && Math.Abs(Face(visible[0]).Top - Face(visible[1]).Top) < 1, "The scrolled marker fixture did not expose one complete row.");
            CheckGap(visible[0], visible[1]);
            var presenter = ControlTree(viewer).OfType<ScrollContentPresenter>().Single(element => ReferenceEquals(element.TemplatedParent, viewer));
            var viewport = presenter.TransformToAncestor(list).TransformBounds(new Rect(presenter.RenderSize));
            var partial = ItemReorderBehavior.Realized(list).Select(container => list.ItemContainerGenerator.ItemFromContainer(container))
                .First(item => Face(item).Top < viewport.Top && Face(item).Bottom > viewport.Top + 12);
            var partialContainer = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(partial);
            var partialFace = Face(partial);
            var inside = new Point(partialFace.Left + 2, Math.Max(viewport.Top + 2, partialFace.Top + partialFace.Height / 2));
            var partialOver = ReorderEvent(list, partialContainer, data, inside, DragDrop.PreviewDragOverEvent);
            Require(partialOver.Handled && partialOver.Effects == DragDropEffects.Move, "A partly visible card rejected its visible edge.");
            var clipped = (System.Windows.Documents.Adorner)state.GetType().GetField("_adorner", ReorderFlags)!.GetValue(state)!;
            SaveControlCapture((FrameworkElement)VisualTreeHelper.GetChild(window, 0), capture);
            bool HasViewportClip(Drawing? paint) => paint is DrawingGroup group
                && (group.ClipGeometry?.Bounds == viewport || group.Children.Any(HasViewportClip));
            Require(HasViewportClip(VisualTreeHelper.GetDrawing(clipped)),
                $"The rendered flashcard insertion line is not clipped to its card viewport: expected={viewport}.");
            var unchanged = list.Items.Cast<object>().ToArray();
            foreach (var outside in new[] { new Point(inside.X, viewport.Top - 16), new Point(inside.X, viewport.Bottom + 16),
                new Point(viewport.Left - 4, viewport.Top + viewport.Height / 2), new Point(viewport.Right + 4, viewport.Top + viewport.Height / 2) })
            {
                var over = ReorderEvent(list, partialContainer, data, outside, DragDrop.PreviewDragOverEvent);
                Require(over.Handled && over.Effects == DragDropEffects.None && !ItemReorderBehavior.IsInsertionActive,
                    "Leaving the card viewport retained an insertion target.");
                var drop = ReorderEvent(list, partialContainer, data, outside, DragDrop.PreviewDropEvent);
                Require(drop.Handled && drop.Effects == DragDropEffects.None && unchanged.SequenceEqual(list.Items.Cast<object>()),
                    "A drop outside the card viewport changed its order.");
            }
        }
        finally
        {
            state.GetType().GetMethod("Clear", ReorderFlags)!.Invoke(state, null); window.Width = originalWidth; viewer.ScrollToHome(); Pump(180);
            if (_pointerControls) SetCursorPos(geometryPointer.X, geometryPointer.Y);
        }
    }
    private static void ProbeReorderPointer(ItemsControl list, object moving, object anchor, string capture, bool grouping = false, bool cancel = false, bool recycle = false)
    {
        var window = Window.GetWindow(list)!; var handle = new WindowInteropHelper(window).Handle;
        var previousWindow = GetForegroundWindow(); GetCursorPos(out var previous);
        var originalOrder = list.Items.Cast<object>().ToArray();
        bool flashcards = ItemReorderBehavior.GetKind(list) == ItemReorderKind.Flashcards;
        var viewer = flashcards ? ControlTree(list).OfType<ScrollViewer>().First() : null;
        var source = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(moving);
        var sourceFrame = flashcards ? ControlTree(source).OfType<CollectionSelectionFrame>().First() : null;
        var grip = ControlTree(source).OfType<ItemReorderGrip>().FirstOrDefault(item => item.IsVisible);
        var target = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(anchor);
        var start = grip is not null ? grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2))
            : source.PointToScreen(new Point(source.ActualWidth * .4, source.ActualHeight * .5));
        bool vertical = ItemReorderBehavior.GetKind(list) is ItemReorderKind.QuizQuestions or ItemReorderKind.AiTools;
        var end = target.PointToScreen(grouping ? new Point(target.ActualWidth / 2, target.ActualHeight / 2)
            : vertical ? new Point(target.ActualWidth / 2, 3) : new Point(3, target.ActualHeight / 2));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        using var releaseWatchdog = new System.Threading.Timer(_ => mouse_event(4, 0, 0, 0, UIntPtr.Zero), null, flashcards ? 6000 : 3000, System.Threading.Timeout.Infinite);
        var events = new Queue<string>();
        DragEventHandler observe = (_, args) =>
        {
            events.Enqueue($"{args.RoutedEvent.Name}: {args.OriginalSource?.GetType().Name}/{(args.OriginalSource as FrameworkElement)?.Name}, point={args.GetPosition(list)}, handled={args.Handled}, effect={args.Effects}, insertion={ItemReorderBehavior.IsInsertionActive}");
            while (events.Count > 8) events.Dequeue();
        };
        window.AddHandler(DragDrop.PreviewDragOverEvent, observe, true); window.AddHandler(DragDrop.PreviewDropEvent, observe, true);
        int phase = 0; bool realDrag = false;
        bool cancelRequested = false;
        var nativeSource = (FrameworkElement?)sourceFrame ?? source;
        QueryContinueDragEventHandler cancelDrag = (_, args) => { if (cancelRequested) { args.Action = DragAction.Cancel; args.Handled = true; } };
        nativeSource.QueryContinueDrag += cancelDrag;
        var flashcardPaint = new List<string>();
        var paintFailures = new List<string>();
        void RecordFlashcardPaint(string stage)
        {
            if (!flashcards) return;
            flashcardPaint.Add(stage + ": " + string.Join("; ", ControlTree(list).OfType<Border>()
                .Where(face => face.Name == "CardSurface" && face.IsVisible)
                .Select(face => $"{(face.DataContext as FlashcardsPreviewWindow.FlashcardPreviewItem)?.Question}: face={face.Opacity}, frame={CollectionSelectionFrame.For(face)?.Opacity}")));
            foreach (var frame in ControlTree(list).OfType<CollectionSelectionFrame>())
            {
                bool sourceFrame = ItemReorderBehavior.IsDragging && ReferenceEquals(frame.DataContext, moving);
                if (!sourceFrame && frame.Opacity != 1) paintFailures.Add($"{stage}: unrelated item retained opacity {frame.Opacity}.");
            }
            SaveControlCapture((FrameworkElement)window.Content, capture + "-" + stage);
        }
        timer.Tick += (_, _) =>
        {
            phase++;
            if (phase == 1) MoveTestPointer(new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2));
            if (phase == 2) { realDrag = ItemReorderBehavior.IsDragging; MoveTestPointer(end); }
            if (phase == 3 && typeof(DashboardDragGhost).GetField("_activeGhostWindow", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is Window ghost)
                SaveControlCapture((FrameworkElement)ghost.Content, capture + "-ghost");
            if (phase == 3) RecordFlashcardPaint("during");
            if (phase == 4 && recycle) { viewer!.ScrollToVerticalOffset(1500); window.UpdateLayout(); }
            if (phase == 6 && recycle)
            {
                if (sourceFrame!.IsLoaded && ReferenceEquals(sourceFrame.DataContext, moving))
                    paintFailures.Add("The recycling check did not release its source container.");
                if (sourceFrame.Opacity != 1) paintFailures.Add("The released source frame retained drag dimming.");
                RecordFlashcardPaint("recycled");
            }
            if (phase == 10 && cancel)
            {
                cancelRequested = true;
                var outside = window.PointToScreen(new Point(window.ActualWidth / 2, 85));
                MoveTestPointer(outside);
            }
            if (phase >= (flashcards ? 12 : 4)) { timer.Stop(); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
        };
        try
        {
            window.Activate(); SetForegroundWindow(handle); Pump(80);
            Require(GetForegroundWindow() == handle, "Real dragging requires the fixture's interactive desktop.");
            window.UpdateLayout();
            start = grip is not null ? grip.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2))
                : source.PointToScreen(new Point(source.ActualWidth * .4, source.ActualHeight * .5));
            Require(SetCursorPos((int)start.X, (int)start.Y), "Could not position the test pointer."); Pump(40);
            Require(grip?.IsMouseOver ?? source.IsMouseOver, $"The actual pointer missed its drag source: capture={Mouse.Captured}, over={Mouse.DirectlyOver}, hit={window.InputHitTest(window.PointFromScreen(start))}, start={start}, grip-now={grip?.PointToScreen(new Point(grip.ActualWidth / 2, grip.ActualHeight / 2))}.");
            if (flashcards)
                foreach (var host in ControlTree(source).OfType<DashboardTileHost>()) host.RefreshScrollPaint();
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); Pump(30);
            timer.Start(); Pump(flashcards ? 2000 : 950); Pump(250); window.UpdateLayout();
            if (grouping)
                WaitLoading(() => MainViewModel.DashboardOrderItem.From(moving).Group is Guid id && MainViewModel.DashboardOrderItem.From(anchor).Group == id);
            Require(realDrag && (cancel ? originalOrder.SequenceEqual(list.Items.Cast<object>()) : grouping || list.Items.IndexOf(moving) + 1 == list.Items.IndexOf(anchor)),
                $"Actual pointer dragging did not commit its destination: started={realDrag}, source={list.Items.IndexOf(moving)}, anchor={list.Items.IndexOf(anchor)}; {string.Join("; ", events)}");
            Require(!ItemReorderBehavior.IsDragging && !ItemReorderBehavior.IsInsertionActive, "Actual pointer release retained drag state.");
            Require(Mouse.Captured is null, $"The drag retained mouse capture: {Mouse.Captured}.");
            Require(!ControlTree(list).OfType<ItemReorderGrip>().Any(handle => handle.ContextMenu?.IsOpen == true), "Releasing a drag accidentally opened its move menu.");
            RecordFlashcardPaint("after");
            Require(paintFailures.Count == 0, string.Join("\n", paintFailures));
            if (flashcards)
            {
                Require(sourceFrame!.Opacity == 1, "The original source frame retained drag dimming after release.");
                foreach (var face in ControlTree(list).OfType<Border>().Where(face => face.Name == "CardSurface"))
                    Require(face.Opacity == 1, "A live flashcard retained drag dimming.");
                var cached = SkiaCardTileCache.GetReady(moving, VisualTreeHelper.GetDpi(list).PixelsPerDip, SkiaTileRenderers.FlashcardGridCard, 316);
                if (MotionPolicy.Spatial && !recycle) Require(cached is not null, "The cold source tile never warmed during or after the native drag.");
                if (cached is not null)
                {
                    var bytes = new byte[4]; int pixel = (int)((FrozenCardDrawing.Fringe + 100) * VisualTreeHelper.GetDpi(list).PixelsPerDip);
                    cached.CopyPixels(new Int32Rect(pixel, pixel, 1, 1), bytes, 4, 0);
                    flashcardPaint.Add($"source scroll tile: alpha={bytes[3]}");
                    Require(bytes[3] > 240, $"The scroll cache retained drag dimming: alpha={bytes[3]}.");
                }
                File.WriteAllLines(Path.Combine(_output, capture + "-paint.txt"), flashcardPaint);
            }
        }
        finally
        {
            timer.Stop(); ReleaseTestMouse(); SetCursorPos(previous.X, previous.Y);
            window.RemoveHandler(DragDrop.PreviewDragOverEvent, observe); window.RemoveHandler(DragDrop.PreviewDropEvent, observe);
            nativeSource.QueryContinueDrag -= cancelDrag;
            if (recycle) { viewer!.ScrollToHome(); Pump(200); }
            if (previousWindow != IntPtr.Zero) SetForegroundWindow(previousWindow);
        }
    }
}
