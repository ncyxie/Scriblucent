using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeUiFixes()
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false, EnableAiFeatures = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme); Scriblucent.ThemeManager.SetTheme(theme); LocalizationService.SetCulture("en");
            // The audit executable is a different entry assembly from the application.
            _app.Resources["BrandLogoSvg"] = new Uri($"pack://application:,,,/Scriblucent;component/Assets/brand-logo-{(theme == "Dark" ? "light" : "dark")}.svg");
            MotionPolicy.Configure(MotionPreference.Full, false);
            var settings = new SettingsPanel();
            typeof(SettingsPanel).GetField("_isApplyingSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settings, true);
            settings.DataContext = new SettingsProbeModel { EnableScrollbar = false };
            var owner = new Window { Width = 980, Height = 820, Content = settings, ShowInTaskbar = false };
            owner.Show(); _app.MainWindow = owner; Pump(150);
            var motion = (FrameworkElement)settings.FindName("PanelMotionHost"); motion.Opacity = 1; motion.RenderTransform = Transform.Identity;
            var measurement = (ComboBox)settings.FindName("MeasurementUnitSystemBox");
            var columns = ControlTree(settings).OfType<Grid>().Single(grid => grid.Children.OfType<StackPanel>()
                .Any(panel => panel.Children.Contains(measurement)));
            Require(columns.Children.OfType<Border>().Any(border => Grid.GetColumn(border) == 1 && border.Width == 1 && border.ActualHeight > 10),
                "Measurement and animation columns lack a visible divider.");
            var divider = columns.Children.OfType<Border>().Single(border => Grid.GetColumn(border) == 1);
            var separatorBounds = divider.TransformToAncestor(settings).TransformBounds(new Rect(divider.RenderSize));
            var scale = VisualTreeHelper.GetDpi(settings).DpiScaleX;
            var separatorPaint = Snapshot(settings, scale);
            var pixel = new byte[4];
            separatorPaint.CopyPixels(new Int32Rect((int)Math.Floor((separatorBounds.Left + separatorBounds.Width / 2) * scale),
                (int)Math.Floor((separatorBounds.Top + separatorBounds.Height / 2) * scale), 1, 1), pixel, 4, 0);
            var separatorColor = ((SolidColorBrush)divider.Background).Color;
            Require(pixel[2] == separatorColor.R && pixel[1] == separatorColor.G && pixel[0] == separatorColor.B,
                "Settings divider did not paint at the current monitor scale.");
            checks.Add($"{theme}: separator bounds={separatorBounds}; opacity={divider.Opacity}; fill={divider.Background}; visibility={divider.Visibility}.");
            SaveControlCapture(settings, "settings-appearance-" + theme);
            var toggle = (CheckBox)settings.FindName("EnableScrollbarCheckBox");
            toggle.BringIntoView(); owner.UpdateLayout(); Pump(80);
            if (!_pointerControls)
            {
                // Exercise the actual template trigger in a desktop-independent fixture.
                // This is synthetic hover, not a physical pointer measurement.
                var key = typeof(UIElement).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                    .Select(field => field.GetValue(null)).OfType<DependencyPropertyKey>()
                    .Single(key => key.DependencyProperty == UIElement.IsMouseOverProperty);
                toggle.SetValue(key, true); Pump(60);
                var track = (Border)toggle.Template.FindName("SwitchTrack", toggle);
                Require(ReferenceEquals(track.Background, owner.FindResource("SwitchTrackHoverBackground")) && toggle.IsChecked == false,
                    "Off hover changed the value or used the wrong track fill.");
                SaveControlCapture(settings, "switch-off-hover-" + theme);
                toggle.SetValue(key, false); Pump(60);
                ProbeSwitchSurface(owner, toggle);
            }
            if (_pointerControls) WithUiPointer(owner, toggle, new Point(toggle.ActualWidth - 20, toggle.ActualHeight / 2), () =>
            {
                Require(toggle.IsMouseOver && toggle.IsChecked == false, "Off switch did not receive hover.");
                var track = (Border)toggle.Template.FindName("SwitchTrack", toggle);
                Require(ReferenceEquals(track.Background, owner.FindResource("SwitchTrackHoverBackground")), "Off hover uses the wrong track fill.");
                Require(((SolidColorBrush)track.Background).Color != ((SolidColorBrush)owner.FindResource("InputRestBorder")).Color,
                    "Off hover became the input-border colour.");
                SaveControlCapture(settings, "switch-off-hover-" + theme);
                ProbePointerToggle(owner, toggle, toggle.TranslatePoint(new Point(toggle.ActualWidth - 20, toggle.ActualHeight / 2), owner));
            });
            owner.Close(); Pump(80);

            foreach (var key in new[] { "ChromeComboBoxStyle", "InsetComboBoxStyle", "SetSelectorComboBoxStyle", "LinkedNoteComboBoxStyle" })
            {
                var combo = new ComboBox { Width = 300, ItemsSource = new[] { "A readable choice", "Another choice" }, SelectedIndex = 0,
                    Style = (Style)_app.FindResource(key), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                owner = new Window { Width = 500, Height = 240, Content = combo, ShowInTaskbar = false };
                owner.Show(); owner.UpdateLayout(); Pump(80);
                var hit = combo.InputHitTest(new Point(25, combo.ActualHeight / 2)) as DependencyObject;
                while (hit is not null && hit is not ToggleButton) hit = VisualTreeHelper.GetParent(hit);
                Require(hit is ToggleButton, key + " label does not share its disclosure hit target.");
                var disclosure = (IToggleProvider)new ToggleButtonAutomationPeer((ToggleButton)hit!).GetPattern(PatternInterface.Toggle)!;
                disclosure.Toggle(); Pump(100); Require(combo.IsDropDownOpen, key + " disclosure did not open.");
                disclosure.Toggle(); Pump(80); Require(!combo.IsDropDownOpen, key + " disclosure did not close.");
                if (_pointerControls) WithUiPointer(owner, combo, new Point(25, combo.ActualHeight / 2), () =>
                {
                    mouse_event(2, 0, 0, 0, UIntPtr.Zero); Pump(20); mouse_event(4, 0, 0, 0, UIntPtr.Zero); Pump(100);
                    Require(combo.IsDropDownOpen, key + " only opens from its arrow.");
                    combo.IsDropDownOpen = false; Pump(100);
                });
                owner.Close(); Pump(40);
            }

            var about = new AboutPanel { Visibility = Visibility.Collapsed };
            owner = new Window { Width = 980, Height = 820, Content = about, ShowInTaskbar = false };
            owner.Show(); _app.MainWindow = owner; about.ShowAnimated(); Pump(1000);
            var author = ControlTree(about).OfType<Button>().First(button => button.Tag is string tag && tag.StartsWith("https://github.com/"));
            PointCommand(author, author);
            var face = ControlTree(author).OfType<InteractionSurface>().Single();
            Require(face.CornerRadius == new CornerRadius(10) && face.Padding == new Thickness(12, 9, 12, 9) && face.ActualHeight >= 40,
                "Profile link hover retained compact chip geometry.");
            SaveControlCapture(about, "about-author-hover-" + theme);
            owner.Close(); Pump(100);

            var answers = new StackPanel { Margin = new Thickness(30) };
            answers.Children.Add(new Button { Content = "An answer row", Style = (Style)_app.FindResource("QuizOptionButtonBaseStyle") });
            answers.Children.Add(new Button { Content = "2", Style = (Style)_app.FindResource("QuizNavQuestionButtonStyle"), HorizontalAlignment = HorizontalAlignment.Left });
            owner = new Window { Width = 600, Height = 300, Content = answers, ShowInTaskbar = false };
            owner.Show(); Pump(80);
            foreach (var button in answers.Children.OfType<Button>())
                Require(SurfaceMaterial.GetKind(button) == SurfaceMaterialKind.Neutral && ReferenceEquals(button.Background, _app.FindResource("IconButtonBackground")),
                    "Quiz action retained the flat canvas treatment.");
            PointCommand(answers.Children.OfType<Button>().First(), answers.Children.OfType<Button>().First());
            SaveControlCapture(answers, "quiz-actions-" + theme); owner.Close(); Pump(80);

            var quiz = new QuizDocument { Title = "Synthetic design check", Questions = [
                new QuizQuestion { Question = "Which answer makes the action clear?", Options = [
                    new QuizOption { Text = "A coordinated material and selection state", IsCorrect = true },
                    new QuizOption { Text = "An unrelated flat canvas fill" } ] },
                new QuizQuestion { Type = QuizQuestionType.PlainText, Question = "Describe a clear visual hierarchy.", PlainTextAnswer = "Clear surfaces" } ] };
            var preview = new QuizPreviewWindow(quiz, noteOptions: null) { WindowState = WindowState.Normal, Width = 920, Height = 720, ShowInTaskbar = false };
            preview.Show(); Pump(450);
            var summary = (Border)preview.FindName("QuizSummaryPanel");
            Require(ReferenceEquals(summary.Background, preview.FindResource("CardBackground")) && SurfaceMaterial.GetKind(summary) == SurfaceMaterialKind.Raised,
                "Quiz count summary did not match the editor content surface.");
            Require(((TextBlock)preview.FindName("QuestionCountTextBlock")).IsVisible, "Quiz count summary was not visible.");
            SaveControlCapture((FrameworkElement)preview.Content, "quiz-count-summary-" + theme);
            typeof(QuizPreviewWindow).GetField("_allowCloseWithoutPrompt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(preview, true);
            preview.Close(); Pump(100);
            var taking = new QuizModeWindow(quiz) { WindowState = WindowState.Normal, Width = 920, Height = 720, ShowInTaskbar = false };
            taking.Show(); Pump(450);
            var flag = (Button)taking.FindName("FlagQuestionButton");
            Require(SurfaceMaterial.GetKind(flag) == SurfaceMaterialKind.Neutral && ReferenceEquals(flag.Background, taking.FindResource("IconButtonBackground")),
                "Flag-for-review retained a flat idle face.");
            flag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(240);
            Require(SurfaceMaterial.GetKind(flag) == SurfaceMaterialKind.Tinted && ReferenceEquals(InteractionSurface.GetHoverBackground(flag), taking.FindResource("AccentDangerSubtleHover")),
                "Flagged review action lost its semantic hover.");
            SaveControlCapture((FrameworkElement)taking.Content, "quiz-flagged-" + theme);
            flag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(240);
            Require(SurfaceMaterial.GetKind(flag) == SurfaceMaterialKind.Neutral, "Unflagging did not restore the neutral action.");
            var options = (StackPanel)taking.FindName("OptionsPanel");
            var option = options.Children.OfType<Button>().First();
            Require(ReferenceEquals(option.Background, taking.FindResource("IconButtonBackground")), "Take-mode option reset to the canvas fill.");
            option.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(240);
            SaveControlCapture((FrameworkElement)taking.Content, "quiz-selected-" + theme);
            ((Button)taking.FindName("NextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(240);
            Require(ControlTree(taking).OfType<TextBox>().Any(text => text.IsVisible), "Plain-text question input was not rendered.");
            SaveControlCapture((FrameworkElement)taking.Content, "quiz-input-" + theme);
            taking.Close(); Pump(100);

            owner = new Window { Width = 920, Height = 820, ShowInTaskbar = false }; owner.Show(); _app.MainWindow = owner;
            var flashcard = new EditFlashcardDialog(true) { Owner = owner };
            flashcard.Question = "A useful question"; flashcard.Answer = "A clear answer"; flashcard.Show(); Pump(450);
            SaveControlCapture((FrameworkElement)flashcard.Content, "flashcard-fields-" + theme);
            flashcard.Close(); owner.Close(); Pump(100);
            checks.Add(theme + ": Settings divider, profile row geometry, quiz material, flashcard fields" +
                (_pointerControls ? "; real off-switch hover/toggle and selector clicks over label passed." : "; synthetic off hover, native hit targets and automation disclosure toggles passed; physical pointer checks omitted."));
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "UI fixture queued a user preference write.");
        File.WriteAllLines(Path.Combine(_output, "ui-fixes-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }

    private static void WithUiPointer(Window window, FrameworkElement target, Point local, Action check)
    {
        var previousWindow = GetForegroundWindow(); GetCursorPos(out var previous);
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            SetForegroundWindow(handle); window.Activate(); Pump(80);
            Require(GetForegroundWindow() == handle, "Native pointer fixture could not activate its window.");
            var screen = target.PointToScreen(local);
            var point = new CursorPoint { X = (int)screen.X, Y = (int)screen.Y };
            Require(WindowFromPoint(point) == handle && SetCursorPos(point.X, point.Y), "Native pointer target is unavailable.");
            Pump(250); check();
        }
        finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); SetCursorPos(previous.X, previous.Y); SetForegroundWindow(previousWindow); }
    }
}
