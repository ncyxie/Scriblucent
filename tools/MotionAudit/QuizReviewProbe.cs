using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeQuizReview()
    {
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in new[] { "en", "lt" })
        {
            LoadNativeControlResources(theme);
            Scriblucent.ThemeManager.SetTheme(theme);
            LocalizationService.SetCulture(language);
            var quiz = new QuizDocument
            {
                Questions = Enumerable.Range(1, 108).Select(index => new QuizQuestion
                {
                    Question = "Question " + index,
                    Options = [new() { Text = "First answer", IsCorrect = true }, new() { Text = "Second answer" }]
                }).ToList()
            };
            var progress = new QuizProgressSession { Questions = quiz.Questions, CurrentQuestionIndex = 2 };
            for (var index = 0; index < quiz.Questions.Count; index++)
            {
                if (index % 4 >= 2) progress.FlaggedQuestionIds.Add(quiz.Questions[index].Id);
                if (index % 2 == 1) progress.SelectedOptionIndexesByQuestionId[quiz.Questions[index].Id] = [0];
            }
            QuizModeWindow Open(QuizProgressSession session)
            {
                var window = new QuizModeWindow(quiz, resumeFrom: session)
                { WindowState = WindowState.Normal, Width = 920, Height = 720, ShowInTaskbar = false };
                window.Show(); Pump(450); return window;
            }
            var taking = Open(progress);
            try
            {
                var list = (ItemsControl)taking.FindName("QuestionNavItemsControl");
                Button Chip(int index) => ControlTree(list).OfType<Button>().Single(button => button.Tag is int tag && tag == index);
                void Check(int index, bool current)
                {
                    var chip = Chip(index);
                    var item = (QuizModeWindow.QuizNavItem)chip.DataContext;
                    var flagged = item.IsFlagged;
                    Require(item.IsCurrent == current, "Navigator current-position state did not follow navigation.");
                    var expected = current ? "SelectionBackground"
                        : item.IsAnswered ? "AccentSuccessSubtle" : "IconButtonBackground";
                    Require(ReferenceEquals(chip.Background, taking.FindResource(expected)), "Navigator fill hid a review/answer state.");
                    if (current)
                        Require(chip.BorderThickness == new Thickness(2) && ReferenceEquals(chip.BorderBrush, taking.FindResource("AccentRimBrush")),
                            "Current flagged question lost its independent selection ring.");
                    var flag = ControlTree(chip).OfType<Border>().Single(border => border.Name == "ReviewFlag");
                    Require(flag.IsVisible == flagged, "Review flag visibility did not follow the question.");
                    if (flagged)
                    {
                        var bounds = flag.TransformToAncestor(chip).TransformBounds(new Rect(flag.RenderSize));
                        Require(bounds.Left >= 2 && bounds.Top >= 2 && bounds.Right <= chip.ActualWidth - 2 && bounds.Bottom <= chip.ActualHeight - 2,
                            $"Review marker entered the tile's corner/rim clearance: {bounds} within {chip.RenderSize} ({index}, current={current}).");
                        var number = ControlTree(chip).OfType<TextBlock>().Single(text => text.Name == "QuestionNumber");
                        var numberBounds = number.TransformToAncestor(chip).TransformBounds(new Rect(number.RenderSize));
                        Require(numberBounds.Left >= 0 && numberBounds.Top >= 0 && numberBounds.Bottom <= chip.ActualHeight && numberBounds.Right <= chip.ActualWidth,
                            "Question number was clipped by its review marker layout.");
                        Require(!numberBounds.IntersectsWith(bounds), "Review marker overlapped the question number.");
                        Require(Math.Abs(numberBounds.Left + numberBounds.Width / 2 - chip.ActualWidth / 2) < .75
                            && Math.Abs(numberBounds.Top + numberBounds.Height / 2 - chip.ActualHeight / 2) < .75,
                            "Flagging moved the number away from the tile's center.");
                        Require(ReferenceEquals(flag.Background, taking.FindResource("AccentRimBrush")), "Review marker lost the app's accent material.");
                    }
                    Require(AutomationProperties.GetName(chip) == item.NavToolTip, "Navigator accessibility lost the visible question states.");
                    var face = (InteractionSurface)chip.Template.FindName("NavChipBorder", chip);
                    face.AnimateState(true, false, false);
                    if (flagged) Require(flag.IsVisible, "Hover hid the review marker.");
                    face.AnimateState(false, false, false);
                }
                foreach (var current in new[] { 0, 1, 2, 3 })
                {
                    Chip(current).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(500);
                    for (var index = 0; index < 4; index++) Check(index, index == current);
                }
                SaveControlCapture((FrameworkElement)taking.Content, $"quiz-review-{theme}-{language}");
                var flagAction = (Button)taking.FindName("FlagQuestionButton");
                Require(((TextBlock)taking.FindName("FlagQuestionLabel")).Text == LocalizationService.GetString("QuizFlaggedForReview"),
                    "Active review button did not state that the question is flagged.");
                Require((string)flagAction.ToolTip == LocalizationService.GetString("QuizUnflagQuestion"), "Active review button did not explain how to remove the flag.");
                flagAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(200); Check(3, true);
                Require(!taking.CaptureProgress()!.FlaggedQuestionIds.Contains(quiz.Questions[3].Id), "Unflagging was not reflected in progress.");
                flagAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(200); Check(3, true);
                progress = taking.CaptureProgress()!;
                var scroll = ControlTree(list).OfType<ScrollViewer>().First();
                scroll.ScrollToBottom(); Pump(250);
                Check(106, false); Check(107, false);
                SaveControlCapture((FrameworkElement)taking.Content, $"quiz-review-long-{theme}-{language}");
            }
            finally { taking.Close(); Pump(80); }
            taking = Open(progress);
            try
            {
                Require(((TextBlock)taking.FindName("FlagQuestionLabel")).Text == LocalizationService.GetString("QuizFlaggedForReview"),
                    "Resuming lost the active question's review state.");
                quiz.ExamMode = true;
                RemainingCall(taking, "RefreshQuestionNavigator"); Pump(80);
                var chips = ControlTree((ItemsControl)taking.FindName("QuestionNavItemsControl")).OfType<Button>()
                    .Where(button => button.Tag is int).ToArray();
                var lockedFlagged = chips.First(button => button.DataContext is QuizModeWindow.QuizNavItem { IsFlagged: true, IsCurrent: false });
                Require(!lockedFlagged.IsEnabled && ((string)lockedFlagged.ToolTip).Contains(LocalizationService.GetString("QuizExamNavLockedTooltip")),
                    "Exam mode navigation was unlocked or lost its explanation.");
                Require(((string)lockedFlagged.ToolTip).Contains(string.Format(LocalizationService.GetString("QuizFlaggedNavTooltipFormat"), (int)lockedFlagged.Tag + 1)),
                    "Locked question tooltip hid its review state.");
            }
            finally { taking.Close(); Pump(80); }
            checks.Add($"{theme}/{language}: all current/answered/flagged combinations, hover, toggle, resume, exam lock and three-digit virtualized tiles passed.");
        }
        File.WriteAllLines(Path.Combine(_output, "quiz-review-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
}
