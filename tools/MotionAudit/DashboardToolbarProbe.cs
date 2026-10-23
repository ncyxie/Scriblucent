using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;

internal static partial class Program
{
    private static void ProbeDashboardToolbar(string theme)
    {
        _wake16 = true;
        LoadNativeControlResources(theme); ThemeManager.SetTheme(theme);
        var (window, model, _) = LoadingWindow(theme, MotionPreference.Full, 0);
        var report = new List<string>();
        try
        {
            window.Show(); WaitLoading(() => model.IsActiveDashboardReady); Pump(400);
            var header = (DashboardHeaderPanel)window.FindName("DashboardHeader");
            var actions = (DashboardActionStrip)window.FindName("DashboardCollectionActions");
            foreach (var kind in new[] { "Notes", "Flashcards", "Quizzes", "MindMaps" })
            {
                var command = kind switch { "Notes" => model.ShowNotesCommand, "Flashcards" => model.ShowFlashcardsCommand, "Quizzes" => model.ShowQuizzesCommand, _ => model.ShowMindMapsCommand };
                command.Execute(null); WaitLoading(() => model.IsActiveDashboardReady); Pump(32);
                WaitLoading(() => !actions.IsTransitioning);
                window.UpdateLayout();
                bool hasActions = kind is "Flashcards" or "Quizzes";
                Require(actions.IsVisible == hasActions, "Collection actions did not follow the active dashboard.");
                if (!hasActions) continue;
                var buttons = ControlTree(actions).OfType<Button>().Where(button => button.IsVisible).ToArray();
                Require(buttons.Length == (kind == "Flashcards" ? 3 : 2), "Dashboard exposed an irrelevant collection action.");
                foreach (var culture in new[] { "en", "lt" })
                foreach (var width in new[] { 1280d, 900d, 760d })
                {
                    LocalizationService.SetCulture(culture); window.Width = width; Pump(220); window.UpdateLayout();
                    CheckToolbarBounds(header, actions);
                    InvokeOrderingButton((Button)window.FindName("TopSearchButton")); Pump(350);
                    var search = (FrameworkElement)window.FindName("TopSearchPanel");
                    Require(search.IsVisible && search.ActualWidth >= 80, "Narrow toolbar left no usable search field.");
                    CheckToolbarBounds(header, actions);
                    SaveLocalControlCapture(header, $"toolbar-{theme}-{kind}-{culture}-{width}-search");
                    InvokeOrderingButton((Button)window.FindName("TopSearchButton")); Pump(300);
                    CheckToolbarBounds(header, actions);
                    SaveLocalControlCapture(header, $"toolbar-{theme}-{kind}-{culture}-{width}");
                }
                window.Width = 1280; Pump(250);
                SaveControlCapture((FrameworkElement)window.Content, $"toolbar-scene-{theme}-{kind}");
                foreach (var button in buttons)
                {
                    string expected = button.Name switch
                    {
                        "DashboardCombineButton" => kind == "Flashcards" ? "FlashcardCombineWindow" : "QuizLibraryWindow",
                        "DashboardHistoryButton" => kind == "Flashcards" ? "FlashcardStudyHistoryWindow" : "QuizAttemptHistoryWindow",
                        _ => "FlashcardStatisticsWindow"
                    };
                    string? actual = null;
                    var closer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
                    closer.Tick += (_, _) =>
                    {
                        var dialog = window.OwnedWindows.Cast<Window>().FirstOrDefault(child => child.IsVisible);
                        if (dialog is null) return;
                        actual = dialog.GetType().Name; dialog.Close(); closer.Stop();
                    };
                    closer.Start();
                    try { InvokeOrderingButton(button); Pump(500); }
                    finally { closer.Stop(); }
                    Require(actual == expected, $"{kind}/{button.Name}: direct action opened {actual ?? "nothing"}, expected {expected}.");
                }
                report.Add($"{theme}/{kind}: pinned one-click collection actions open the correct dialogs; matching icon controls and English/Lithuanian tooltips fit at three widths, with search open/closed and no overlapping controls.");
            }
        }
        finally { CloseLoadingWindow(window); }
        File.WriteAllLines(Path.Combine(_output, "toolbar-checks.txt"), report);
        foreach (var line in report) Console.WriteLine(line);
    }
    private static void CheckToolbarBounds(DashboardHeaderPanel header, StackPanel actions)
    {
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(header).TransformBounds(new Rect(element.RenderSize));
        var brand = (FrameworkElement)header.Children[0]; var utility = (FrameworkElement)header.Children[2];
        var actionBounds = Bounds(actions); var utilityBounds = Bounds(utility); var brandBounds = Bounds(brand);
        Require(brandBounds.Right <= actionBounds.Left + 1 && actionBounds.Right <= utilityBounds.Left + 1
            && utilityBounds.Right <= header.ActualWidth + 1, $"Toolbar groups overlap at {header.ActualWidth}: brand={brandBounds}, actions={actionBounds}, utility={utilityBounds}.");
        foreach (var button in ControlTree(actions).OfType<Button>().Where(button => button.IsVisible))
        {
            Require(button.IsEnabled && Math.Abs(button.ActualHeight - 32) < 1 && button.ActualWidth >= 33, $"Collection action lost its direct hit target: {button.Name}, enabled={button.IsEnabled}, size={button.RenderSize}, header={header.ActualWidth}.");
            Require(!ControlTree(button).OfType<TextBlock>().Any(), "A collection action retained a text label in the icon toolbar.");
            Require(button.ToolTip is string tip && !string.IsNullOrWhiteSpace(tip)
                && System.Windows.Automation.AutomationProperties.GetName(button) == tip, "Collection action lost its descriptive tooltip or accessible name.");
        }
    }
}
