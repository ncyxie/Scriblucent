using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.ViewModels;
using Scriblucent.Rendering;

internal static partial class Program
{
    private static void ProbeDashboardSelection(string? only = null)
    {
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var mode in new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off })
        {
            if (only is not null && !only.StartsWith($"{theme}/{mode}")) continue;
            LoadNativeControlResources(theme);
            var (window, vm, store) = LoadingWindow(theme, mode, 48);
            vm.IsRecentSectionVisible = false;
            vm.IsCalendarSectionExpanded = false;
            var group = Guid.NewGuid();
            // Real stores, startup, grouping and virtualization; every write stays in this fixture.
            File.WriteAllText(Path.Combine(store, "notes.json"), JsonSerializer.Serialize(new NotesStoreData
            {
                Notes = Enumerable.Range(0, 48).Select(i => new NoteDocument
                { Title = i == 2 ? "Planning a longer title that must leave room for selection" : $"Planning {i:D2}", Content = "A thought to develop.", GroupId = i < 3 ? group : null }).ToList(),
                Groups = [new NoteGroupData { GroupId = group, Name = "Selection research" }]
            }));
            File.WriteAllText(Path.Combine(store, "flashcard-sets.json"), JsonSerializer.Serialize(new FlashcardSetStoreData
            {
                Sets = Enumerable.Range(0, 48).Select(i => new FlashcardSetDocument
                { Title = $"Practice {i:D2}", GroupId = i < 3 ? group : null, Cards = [new FlashcardItem { Question = "Question", Answer = "Answer" }] }).ToList(),
                Groups = [new FlashcardSetGroupData { GroupId = group, Name = "Selection research" }]
            }));
            File.WriteAllText(Path.Combine(store, "mind-maps.json"), JsonSerializer.Serialize(new MindMapStoreData
            {
                Maps = Enumerable.Range(0, 48).Select(i => { var map = Scriblucent.Services.NewDocumentDefaults.MindMap(); map.Title = $"Explore {i:D2}"; map.GroupId = i < 3 ? group : null; return map; }).ToList(),
                Groups = [new MindMapGroupData { GroupId = group, Name = "Selection research" }]
            }));
            File.WriteAllText(Path.Combine(store, "quizzes.json"), JsonSerializer.Serialize(new QuizStoreData
            {
                Quizzes = Enumerable.Range(0, 48).Select(i => new QuizDocument
                { Title = $"Knowledge {i:D2}", GroupId = i < 3 ? group : null }).ToList(),
                Groups = [new QuizGroupData { GroupId = group, Name = "Selection research" }]
            }));
            try
            {
                window.Show(); window.Activate();
                foreach (var kind in Enum.GetValues<Scriblucent.Services.EditorDocumentKind>())
                {
                    if (only?.Split('/').ElementAtOrDefault(2) is { } target && target != kind.ToString()) continue;
                    SelectLoadingDashboard(vm, kind);
                    WaitLoading(() => vm.IsActiveDashboardReady && ((FrameworkElement)window.FindName("DashboardLoadingLayer")).Visibility == Visibility.Collapsed);
                    Pump(300);
                    var page = (FrameworkElement)window.FindName(vm.ActiveDashboard + "DashboardView");
                    var viewer = ControlTree(page).OfType<ScrollViewer>().First();
                    viewer.ScrollToTop(); Pump(200);
                    var frames = ControlTree(page).OfType<CollectionSelectionFrame>().Where(f => f.IsVisible).ToArray();
                    Require(frames.Length is > 0 and < 48, $"{kind} did not retain bounded realization ({frames.Length}).");
                    var first = frames[0];
                    var dock = (CollectionSelectionDock)window.FindName("DashboardSelectionDock");
                    SelectionEnterFromCard(first); vm.DashboardSelection.Clear(); Pump(300);
                    Require(vm.DashboardSelection.IsActive && vm.DashboardSelection.TotalCount == 48 && first.IsSelecting && dock.IsVisible,
                        $"{kind} selection dock or matching total did not initialize.");
                    Require(((ContextMenu)RemainingCall(window, "BuildDashboardSelectionMenu")!).Items.OfType<MenuItem>().Any(item => Equals(item.Header, Scriblucent.Localization.LocalizationService.GetString("SelectionExportSelected"))), "Dashboard More menu has no selected export.");
                    var toggle = ControlTree(first).OfType<CollectionSelectionToggle>().Single();
                    var peer = new CheckBoxAutomationPeer(toggle);
                    ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)).Toggle(); Pump(220);
                    var glyph = (System.Windows.Shapes.Path)toggle.Template.FindName("CheckGlyph", toggle);
                    if (!(first.IsItemSelected && vm.DashboardSelection.Count == 1 && toggle.IsChecked == true && glyph.Opacity > .99 && Math.Abs(glyph.StrokeDashOffset) < .01))
                        Console.WriteLine($"Waiting for fresh checkbox paint: {theme}/{mode}/{kind}, selected={first.IsItemSelected}, count={vm.DashboardSelection.Count}, checked={toggle.IsChecked}, opacity={glyph.Opacity}, dash={glyph.StrokeDashOffset}");
                    WaitLoading(() => glyph.Opacity > .99 && Math.Abs(glyph.StrokeDashOffset) < .01);
                    Require(first.IsItemSelected && vm.DashboardSelection.Count == 1 && toggle.IsChecked == true && glyph.Opacity > .99 && Math.Abs(glyph.StrokeDashOffset) < .01,
                        $"{kind} fresh selection did not paint a checkmark.");
                    Require(((CheckBox)dock.FindName("AllToggle")).IsChecked is null, "Partial selection did not show its mixed state.");
                    var feedback = ControlTree(first).OfType<CollectionSelectionFeedback>().Single();
                    var face = ControlTree(first).OfType<Border>().First(b => b.Name == first.FaceName);
                    var faceBounds = face.TransformToAncestor(first).TransformBounds(new Rect(face.RenderSize));
                    Require((feedback.TranslatePoint(new(), first) - faceBounds.TopLeft).Length < 1 && Math.Abs(feedback.ActualWidth - faceBounds.Width) < 1,
                        $"{kind} rim did not match the actual card face.");
                    ProbeSelectionAttachment(first);
                    var margin = first.FaceMargin;
                    var liveFrameSize = first.RenderSize;
                    if (kind == Scriblucent.Services.EditorDocumentKind.Note)
                    {
                        var card = ControlTree(page).OfType<NoteCardControl>().First(c => ControlTree(c).Contains(first));
                        card.IsBitmapMode = true; Pump(140);
                        window.UpdateLayout();
                        Require(first.IsItemSelected && first.FaceMargin == margin && feedback.ActualWidth > 100 && toggle.IsVisible,
                            "Note selection disappeared or moved while using cached scroll paint.");
                        Require(first.RenderSize == liveFrameSize, $"Cached note paint changed its frame size: {liveFrameSize} to {first.RenderSize}.");
                        if (mode == MotionPreference.Automatic && theme == "Light") SaveControlCapture((FrameworkElement)window.Content, "note-selection-cached-paint");
                        card.IsBitmapMode = false;
                    }
                    else
                    {
                        var tile = ControlTree(first).OfType<DashboardTileHost>().Single();
                        if (mode == MotionPreference.Automatic)
                        {
                            tile.QueueWarm(); WaitLoading(() => tile.HasCachedScrollTile);
                        }
                        tile.IsBitmapMode = true; Pump(100);
                        Require(first.IsItemSelected && toggle.IsVisible && feedback.Progress > .99, "Cached listing paint covered selection.");
                        if (mode == MotionPreference.Automatic)
                        {
                            Require(tile.HasReadyScrollTile, "The warmed listing did not use its exact cached drawing.");
                            var image = (Image)tile.Template.FindName("BitmapLayer", tile);
                            var paint = image.Source;
                            vm.DashboardSelection.Toggle(first.ItemId); vm.DashboardSelection.Toggle(first.ItemId);
                            Require(tile.HasReadyScrollTile && ReferenceEquals(paint, image.Source), "Selection rebuilt the cached card image.");
                        }
                        if (mode == MotionPreference.Automatic && theme == "Light")
                        {
                            var ungrouped = frames.First(frame => !frame.FaceName.StartsWith("Grouped"));
                            var ungroupedTile = ControlTree(ungrouped).OfType<DashboardTileHost>().Single();
                            vm.DashboardSelection.Toggle(ungrouped.ItemId); ungroupedTile.QueueWarm(); WaitLoading(() => ungroupedTile.HasCachedScrollTile); ungroupedTile.IsBitmapMode = true; Pump(220);
                            SaveControlCapture((FrameworkElement)window.Content, $"listing-selection-cached-{kind}");
                            ungroupedTile.IsBitmapMode = false; vm.DashboardSelection.Toggle(ungrouped.ItemId);
                        }
                        tile.IsBitmapMode = false;
                    }
                    SelectionClick((Button)dock.FindName("AllButton")); Pump(220);
                    Require(vm.DashboardSelection.Count == 48 && ((CheckBox)dock.FindName("AllToggle")).IsChecked == true, "Select all ignored offscreen items.");
                    vm.DashboardSelection.SelectAll(); Require(vm.DashboardSelection.Count == 48, "Repeated select all cleared selection.");
                    Require(ControlTree(page).OfType<CollectionSelectionFrame>().Count() < 48, "Select all realized the entire collection.");
                    if (mode == MotionPreference.Automatic) SaveControlCapture((FrameworkElement)window.Content, $"dashboard-selection-{kind}-{theme}");
                    viewer.ScrollToEnd(); Pump(180);
                    viewer.ScrollToTop(); Pump(220);
                    WaitLoading(() => ControlTree(page).OfType<CollectionSelectionFrame>().Where(f => f.IsVisible).All(f => f.IsItemSelected));
                    Require(ControlTree(page).OfType<CollectionSelectionFrame>().Where(f => f.IsVisible).All(f => f.IsItemSelected), $"Recycling lost selected visuals on {theme}/{mode}/{kind}: count={vm.DashboardSelection.Count}; " + string.Join("; ", ControlTree(page).OfType<CollectionSelectionFrame>().Where(f => f.IsVisible && !f.IsItemSelected).Select(f => $"id={f.ItemId}, loaded={f.IsLoaded}, mode={f.IsSelecting}, contains={vm.DashboardSelection.Contains(f.ItemId)}, scope={f.Selection is not null}, window={Window.GetWindow(f) is not null}, source={PresentationSource.FromVisual(f) is not null}, binding={f.GetBindingExpression(CollectionSelectionFrame.SelectionProperty)?.Status}, resolved={f.GetBindingExpression(CollectionSelectionFrame.SelectionProperty)?.ResolvedSource?.GetType().Name}, windowDc={Window.GetWindow(f)?.DataContext?.GetType().Name}, owner={Window.GetWindow(f)?.GetType().Name}, context={f.DataContext?.GetType().Name}")));
                    SelectionClick((Button)dock.FindName("AllButton")); Pump(220);
                    Require(vm.DashboardSelection.IsActive && vm.DashboardSelection.Count == 0, "Clear all unexpectedly exited selection.");
                    var ordered = kind switch
                    {
                        Scriblucent.Services.EditorDocumentKind.Note => vm.NoteGroups.SelectMany(g => g.Notes).Select(n => n.Document.Id).Concat(vm.NotesView.Cast<NoteCardViewModel>().Select(n => n.Document.Id)),
                        Scriblucent.Services.EditorDocumentKind.Flashcards => vm.FlashcardSetGroups.SelectMany(g => g.Sets).Select(n => n.Document.Id).Concat(vm.FlashcardSetsView.Cast<FlashcardSetViewModel>().Select(n => n.Document.Id)),
                        Scriblucent.Services.EditorDocumentKind.MindMap => vm.MindMapGroups.SelectMany(g => g.MindMaps).Select(n => n.Document.Id).Concat(vm.MindMapsView.Cast<MindMapViewModel>().Select(n => n.Document.Id)),
                        _ => vm.QuizGroups.SelectMany(g => g.Quizzes).Select(n => n.Document.Id).Concat(vm.QuizzesView.Cast<QuizViewModel>().Select(n => n.Document.Id))
                    };
                    // Check logical range without depending on physical modifier-key state in CI.
                    bool groupsFirst = mode != MotionPreference.Reduced;
                    var ids = ordered.ToArray();
                    if (!groupsFirst) ids = ids[3..].Concat(ids[..3]).ToArray();
                    vm.DashboardSelection.Toggle(ids[1]); vm.DashboardSelection.Toggle(ids[4], range: true);
                    Require(vm.DashboardSelection.Count == 4 && ids[1..5].All(vm.DashboardSelection.Contains), "Range did not follow displayed group/ungrouped order.");
                    vm.DashboardSelection.Toggle(ids[7], range: true, additive: true);
                    Require(vm.DashboardSelection.Count == 7, "Additive range replaced existing selection.");
                    vm.QuickAddDashboardItemCommand.Execute(null);
                    WaitLoading(() => vm.DashboardSelection.TotalCount == 49);
                    Require(vm.DashboardSelection.Count == 7 && ids[1..8].All(vm.DashboardSelection.Contains), "Adding an item changed the existing selection.");
                    vm.ActiveSearchQuery = "no-such-selection-fixture"; WaitLoading(() => vm.DashboardSelection.TotalCount == 0);
                    Require(vm.DashboardSelection.Count == 0 && vm.DashboardSelection.TotalCount == 0, "Filtering kept hidden selections.");
                    vm.ActiveSearchQuery = "";
                    try { WaitLoading(() => vm.DashboardSelection.TotalCount == 49); }
                    catch (Exception error) { throw new InvalidOperationException($"Restored {theme}/{mode}/{kind}: total={vm.DashboardSelection.TotalCount}, active={vm.DashboardSelection.IsActive}, query={vm.ActiveSearchQuery}, notes={vm.Notes.Count}/{vm.NoteGroups.Count}/{vm.NotesView.Cast<object>().Count()}, flashcards={vm.FlashcardSets.Count}/{vm.FlashcardSetGroups.Count}/{vm.FlashcardSetsView.Cast<object>().Count()}, quizzes={vm.Quizzes.Count}/{vm.QuizGroups.Count}/{vm.QuizzesView.Cast<object>().Count()}, maps={vm.MindMaps.Count}/{vm.MindMapGroups.Count}/{vm.MindMapsView.Cast<object>().Count()}", error); }
                    SelectionClick((Button)dock.FindName("DoneButton")); Pump(220);
                    WaitLoading(() => !dock.IsVisible);
                    Require(!vm.DashboardSelection.IsActive && !dock.IsVisible && ControlTree(page).OfType<CollectionSelectionFrame>().All(f => !f.IsItemSelected), "Exit retained selection decoration.");
                    var check = $"PASS {theme}/{mode}/{kind}: shared dock, fresh tick, face contour, cached paint, bounded select-all, recycling, ranges, unselected additions, filters and exit";
                    checks.Add(check); Console.WriteLine(check);
                }
            }
            finally { CloseLoadingWindow(window); }
        }
        File.WriteAllLines(Path.Combine(_output, "dashboard-selection-checks.txt"), checks);
    }
}
