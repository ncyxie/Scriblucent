using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Rendering;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeCombineWorkspace(string? only = null)
    {
        _wake16 = true;
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false, EnableAiFeatures = false });
        var report = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            if (only is not null && only != $"{theme}/{language}") continue;
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme);
            LocalizationService.SetCulture(language); MotionPolicy.Configure(MotionPreference.Full, false);
            foreach (var flashcards in new[] { true, false })
            {
                var model = PremiumLibraryFixture();
                model.FlashcardSets[0].Document.Tags = ["Alpha"];
                model.Quizzes[0].Document.Tags = ["Alpha"];
                model.FlashcardSets[1].Document.Cards = [new FlashcardItem { Question = "Question 0", Answer = "Answer 0" }];
                model.Quizzes[1].Document.Questions = [new QuizQuestion { Question = "Question 0" }];
                model.FlashcardTagFilters.Add(new("Alpha", false, (_, _) => { }));
                model.QuizTagFilters.Add(new("Alpha", false, (_, _) => { }));
                Window window = flashcards ? new FlashcardCombineWindow(model) : new QuizLibraryWindow(model);
                using var fixture = new CompletionWindow(window);
                var state = ((ICombineWorkspaceHost)window).Workspace;
                var list = ControlTree(window).OfType<ItemsControl>().Single(item => item.Name == "SourceItems");
                var label = $"{(flashcards ? "sets" : "quizzes")}-{theme}-{language}";
                Pump(450); window.UpdateLayout();
                var activeScope = ControlTree(window).OfType<RadioButton>().Single(item => item.Name == "AllSourcesScope");
                Require(activeScope.Foreground is SolidColorBrush ink && activeScope.Background is SolidColorBrush plate
                    && SurfaceMaterialPalette.Contrast(ink.Color, plate.Color) >= 4.5, "The active source scope has insufficient text contrast.");
                Require(list.Items.Count == 1000, "The source list did not load.");
                Require(ControlTree(list).OfType<CheckBox>().Count() < 60, "The source list realized its entire library.");
                Require(ControlTree(window).OfType<ItemsControl>().Count(item => item.Items.Count == 1000) == 1, "A duplicate source list is still present.");
                SaveControlCapture((FrameworkElement)window.Content, label + "-initial");
                var items = state.Sources.Cast<object>().Take(2).ToArray();
                state.ToggleSourceCommand.Execute(items[0]); state.ToggleSourceCommand.Execute(items[1]); Pump(80);
                Require(state.HasSelection && state.HasRemovedDuplicates && state.ResultCountText.Contains('1'), "The duplicate-aware result summary is wrong.");
                state.SkipDuplicates = false;
                Require(!state.HasRemovedDuplicates && state.ResultCountText.Contains('2'), "Disabling duplicate removal did not update the result.");
                state.SkipDuplicates = true; state.Shuffle = false; state.Title = "A combined working copy";
                if (window is FlashcardCombineWindow sets)
                {
                    var result = (FlashcardSetDocument)typeof(FlashcardCombineWindow).GetMethod("BuildCombinedDocument", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(sets, [sets.SelectedSets.Select(item => item.Model).ToArray()])!;
                    Require(result.Cards.Count == 1 && result.Title == state.Title && model.FlashcardSets[0].Document.Title != state.Title, "Combined set output or source isolation failed.");
                }
                else if (window is QuizLibraryWindow quizzes)
                {
                    var result = (QuizDocument)typeof(QuizLibraryWindow).GetMethod("BuildCombinedQuiz", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [quizzes.SelectedQuizzes.Select(item => item.Model).ToArray(), state.Title, false, true])!;
                    Require(result.Questions.Count == 1 && result.Title == state.Title && model.Quizzes[0].Document.Title != state.Title, "Combined quiz output or source isolation failed.");
                }
                state.SearchText = "Unmatched combine fixture phrase"; Pump(400);
                Require(list.Items.Count == 0 && state.IsSourceListEmpty && state.HasSelection, "No-match filtering lost selection or empty feedback.");
                SaveControlCapture((FrameworkElement)window.Content, label + "-no-matches");
                state.ShowSelectedOnly = true; Pump(100);
                Require(list.Items.Count == 2 && !state.IsSourceListEmpty, "Selected view did not recover choices hidden by filters.");
                var check = ControlTree(list).OfType<CheckBox>().First();
                ((IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle)!).Toggle(); Pump(150);
                Require(list.Items.Count == 1 && state.HasSelection, "Removing a choice from Selected view did not update its list.");
                SaveControlCapture((FrameworkElement)window.Content, label + "-selected");
                state.ShowSelectedOnly = false; Pump(100);
                Require(list.Items.Count == 0 && state.SearchText == "Unmatched combine fixture phrase", "Returning to All lost the prior filter.");
                state.ClearFiltersCommand.Execute(null); Pump(400);
                Require(list.Items.Count == 1000 && !state.HasFilters && state.HasSelection, "Clearing filters changed selection.");
                var filterButton = ControlTree(window).OfType<ToggleButton>().Single(item => item.Name == "TagFilterButton");
                filterButton.IsChecked = true; Pump(100);
                var tagPopup = ControlTree(window).OfType<Popup>().Single();
                SaveControlCapture((FrameworkElement)tagPopup.Child, label + "-populated-tags");
                var tagCheck = ControlTree(tagPopup.Child).OfType<CheckBox>().Single();
                var tagLabels = ControlTree(tagCheck).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text).ToArray();
                Require(tagLabels.SequenceEqual(new[] { "Alpha" }),
                    $"The tag picker rendered object text instead of its label: {string.Join(" / ", tagLabels)}.");
                var popupSize = tagPopup.Child.RenderSize;
                var filterPosition = filterButton.TranslatePoint(new Point(), window);
                var tag = (TagFilterItemViewModel)state.Tags.Cast<object>().Single(); tag.IsSelected = true; Pump(80);
                Require(tagPopup.IsOpen && tagPopup.Child.RenderSize == popupSize
                    && (filterButton.TranslatePoint(new Point(), window) - filterPosition).Length < 1,
                    "Choosing a tag resized or moved the open filter menu.");
                filterButton.IsChecked = false;
                Require(list.Items.Count == 1 && state.HasFilters, "Tag filtering did not narrow the source list.");
                state.SelectAllCommand.Execute(null); Pump(80);
                state.ShowSelectedOnly = true; Pump(80);
                Require(list.Items.Count == 2 && list.Items.Contains(items[1]), "Matching select-all or Selected view lost a choice hidden by tag filtering.");
                state.ShowSelectedOnly = false; state.ClearFiltersCommand.Execute(null); Pump(100);
                var options = ControlTree(window).OfType<Expander>().Single(item => item.Name == "ConfigurationOptions");
                foreach (var size in new[] { new Size(680, 520), new Size(680, 480), new Size(1180, 760), new Size(680, 520) })
                {
                    window.Width = size.Width; window.Height = size.Height; window.UpdateLayout(); Pump(380);
                    Require(state.Title == "A combined working copy" && !state.Shuffle && state.HasSelection, "Responsive layout lost the working configuration.");
                    var viewport = ControlTree(list).OfType<ScrollViewer>().First();
                    Require(viewport.ViewportHeight > 40 && double.IsFinite(viewport.ViewportHeight), "The source viewport became unbounded or unusable.");
                    if (size.Width == 680)
                    {
                        Require(!options.IsExpanded, "Compact configuration did not start collapsed or recover its previous state.");
                        var body = (Grid)ControlTree(window).OfType<FeatureWindowShell>().Single().Body;
                        var configuration = (FrameworkElement)body.Children[1];
                        Require(configuration.ActualHeight + configuration.Margin.Top + configuration.Margin.Bottom <= body.RowDefinitions[0].ActualHeight + 1,
                            "The result panel exceeds its row and clips off its rounded lower corners.");
                        var header = (FrameworkElement)options.Template.FindName("HeaderSite", options);
                        var scroll = ControlTree(configuration).OfType<ScrollViewer>().First();
                        var bounds = header.TransformToAncestor(scroll).TransformBounds(new Rect(header.RenderSize));
                        if (bounds.Top < -1 || bounds.Bottom > scroll.ViewportHeight + 1)
                            SaveControlCapture((FrameworkElement)window.Content, label + "-header-failure");
                        Require(bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1,
                            $"Compact options header is cut off at {size}: header={bounds}, viewport={scroll.ViewportHeight}, panel={configuration.RenderSize}, padding={((Border)configuration).Padding}, expander padding={options.Padding}.");
                        options.IsExpanded = true; Pump(220); window.UpdateLayout();
                        Require(viewport.ViewportHeight > 40, "Expanding options consumed the source viewport.");
                        options.IsExpanded = false; Pump(220); window.UpdateLayout();
                    }
                }
                SaveControlCapture((FrameworkElement)window.Content, label + "-compact");
                window.Width = 1180; window.Height = 760; Pump(120);
                Require(options.IsExpanded, "Compact options state leaked into the wide layout.");
                state.ClearSelectionCommand.Execute(null); state.ShowSelectedOnly = true; Pump(100);
                Require(state.IsSourceListEmpty && !state.HasSelection, "Clearing Selected view did not show its empty state.");
                report.Add(label + ": one virtualized source list; retained cross-filter selection; native checkbox removal; duplicate counts/output; responsive configuration and empty states passed.");
            }
            var empty = PremiumLibraryFixture(); empty.FlashcardSets.Clear(); empty.Quizzes.Clear();
            foreach (var flashcards in new[] { true, false })
            {
                Window window = flashcards ? new FlashcardCombineWindow(empty) : new QuizLibraryWindow(empty);
                using var fixture = new CompletionWindow(window);
                var state = ((ICombineWorkspaceHost)window).Workspace;
                Require(state.IsSourceListEmpty && !state.HasSelection && !state.HasTags, "Empty library was not described.");
                var toggle = ControlTree(window).OfType<ToggleButton>().Single(item => item.Name == "TagFilterButton");
                toggle.IsChecked = true; Pump(100);
                var popup = ControlTree(window).OfType<Popup>().Single();
                Require(popup.IsOpen && ControlTree(popup.Child).OfType<TextBlock>().Any(text => text.IsVisible && text.Text == LocalizationService.GetString("CombineNoTags")),
                    $"Empty tag picker has no explanation: open={popup.IsOpen}, toggle={toggle.IsChecked}, texts={string.Join(" / ", ControlTree(popup.Child).OfType<TextBlock>().Select(text => $"{text.Text}: {text.Visibility}/{text.IsVisible}"))}.");
                SaveControlCapture((FrameworkElement)popup.Child, $"empty-tags-{window.GetType().Name}-{theme}-{language}");
                toggle.IsChecked = false;
            }
            report.Add($"{theme}/{language}: both empty libraries and empty tag pickers passed.");
        }
        Require(report.Count > 0, "No combine case matched; use Light/en, Light/lt, Dark/en or Dark/lt.");
        File.WriteAllLines(Path.Combine(_output, "combine-workspace-checks.txt"), report);
        foreach (var line in report) Console.WriteLine(line);
    }
}
