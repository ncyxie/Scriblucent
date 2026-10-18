using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Icons;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Rendering;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static object? RemainingCall(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object? RemainingField(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void RemainingKeyboardClick(Button button)
    {
        button.Focus();
        foreach (var routed in new[] { Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
            button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(button)!,
                Environment.TickCount, Key.Space) { RoutedEvent = routed });
    }
    private static void ProbeRemainingUi(bool baseline, bool quick)
    {
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var checks = new List<string>();
        var modes = baseline ? new[] { MotionPreference.Off } : quick ? new[] { MotionPreference.Automatic }
            : new[] { MotionPreference.Automatic, MotionPreference.Reduced, MotionPreference.Off };
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var language in quick ? new[] { "en" } : new[] { "en", "lt" })
        foreach (var mode in modes)
        {
            LoadNativeControlResources(theme);
            if (!baseline) Scriblucent.ThemeManager.SetTheme(theme);
            LocalizationService.SetCulture(language);
            MotionPolicy.Configure(mode, false);
            var suffix = $"{theme}-{language}-{mode}";
            RemainingMap(suffix, checks, baseline);
            RemainingPicker(suffix, checks, baseline);
            RemainingCaption(suffix, checks, baseline);
            RemainingActions(suffix, checks, baseline);
            Pump(350);
            Require(!RenderTransitionClock.IsAnyRunning, "Remaining UI left motion work after close: " + suffix);
            Console.WriteLine("Completed " + suffix);
        }
        Require(typeof(AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "Synthetic UI checks queued a settings write.");
        File.WriteAllLines(Path.Combine(_output, "remaining-ui-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
    private static void RemainingMap(string suffix, List<string> checks, bool baseline)
    {
        var child = new MindMapNode { Text = "Authored styling stays put", BorderThickness = 5,
            BorderColor = "#9280B5", Note = "A short note", IsItalic = true, Icons = new() { "⭐" } };
        var folded = new MindMapNode { Text = "A folded branch", IsExpanded = false,
            Children = new() { new() { Text = "One" }, new() { Text = "Two" } },
            LinkedNoteId = Guid.NewGuid(), Note = "Dense metadata still fits",
            Icons = new() { "📌", "⭐", "✅", "🚩" } };
        var root = new MindMapNode { Text = "Connected ideas", Note = "A note preview with the same floating surface as other app popups.\n\n" +
            string.Join("\n", Enumerable.Repeat("Long notes remain readable and scroll inside the preview.", 20)),
            LinkedNoteId = Guid.NewGuid(), Icons = new() { "📌", "⭐" }, Children = new() { child, folded } };
        var map = new MindMapPreviewWindow(root, openNoteAction: _ => { });
        map.Show(); Pump(550);
        var canvas = (Canvas)map.FindName("MapCanvas");
        var border = canvas.Children.OfType<Border>().Single(b => ReferenceEquals(b.Tag, child));
        var title = ControlTree(border).OfType<TextBlock>().First(t => t.Text.Contains("Authored"));
        var original = title.TranslatePoint(new Point(), border); var originalSize = title.RenderSize;
        RemainingCall(map, "SelectSingleNode", child); RemainingCall(map, "ApplyNodeSelectionVisuals"); Pump(200);
        RemainingCall(map, "UpdateDropTargetHover", root); Pump(180);
        SaveControlCapture((FrameworkElement)map.Content, "map-selected-drop-" + suffix);
        if (!baseline)
        {
            Require(border.BorderThickness.Left == 5 && ((SolidColorBrush)border.BorderBrush).Color == (Color)ColorConverter.ConvertFromString("#9280B5"),
                "Selection replaced the authored border.");
            Require(title.TranslatePoint(new Point(), border) == original && title.RenderSize == originalSize,
                "Selection changed title layout.");
            var revision = (int)RemainingField(map, "_tileRevision")!;
            CommandLightBehavior.PreviewPointer(border, new Point(20, 20), true); Pump(200);
            Require((int)RemainingField(map, "_tileRevision")! == revision, "Hover rebuilt the whole-map snapshot.");
            Require(CommandLightBehavior.LightFor(border) is { } light &&
                (MotionPolicy.Quality == MotionQuality.Off ? light.Strength == 0 : light.Strength > 0), "Node feedback did not follow motion policy.");
            SaveControlCapture((FrameworkElement)map.Content, "map-hover-" + suffix);
            CommandLightBehavior.PreviewPointer(border, new Point(20, 20), false); Pump(200);
            var snapshot = (MindMapTileSnapshot)RemainingCall(map, "BuildTileSnapshot")!;
            CheckRemainingSnapshot(map, snapshot, suffix);
            var sourceRevision = (int)RemainingField(map, "_snapshotSourceRevision")!;
            Require(sourceRevision == snapshot.Revision, "The latest interaction state did not warm the snapshot.");
            RemainingCall(map, "BeginTileSession");
            Require(canvas.Visibility == Visibility.Collapsed, "A prepared snapshot was not used during zoom.");
            RemainingCall(map, "EndTileSession"); Pump(450);
            Require(canvas.Visibility == Visibility.Visible, "Zoom did not restore the live canvas.");
            foreach (var control in ControlTree(canvas).OfType<Button>())
                Require(control.Focusable && control.Template.FindName("ButtonBorder", control) is InteractionSurface,
                    "A generated map command bypassed shared native button feedback.");
        }
        var popup = ControlTree(canvas).OfType<Popup>().First();
        popup.IsOpen = true; Pump(20);
        var opening = Stopwatch.StartNew();
        while (NativeSurfaceMotion.IsRunning(popup.Child, MotionChannel.Presence) ||
               popup.Child.Opacity != 1)
        {
            if (opening.ElapsedMilliseconds >= 4000) break;
            Pump(20);
        }
        var popupRoot = (FrameworkElement)PresentationSource.FromVisual(popup.Child)!.RootVisual;
        if (!baseline) Require(popup.IsOpen && popup.Child.IsVisible && popup.Child.Opacity == 1,
            $"Note preview failed to become visible: open={popup.IsOpen}, visible={popup.Child.IsVisible}, opacity={popup.Child.Opacity}.");
        SaveControlCapture(popupRoot, "map-note-popup-" + suffix);
        if (!baseline)
        {
            var scroller = ControlTree(popupRoot).OfType<ScrollViewer>().First();
            Require(scroller.ScrollableHeight > 0 && scroller.ActualHeight <= 280,
                "The long note preview was not bounded and scrollable.");
            Require(ControlTree(popupRoot).OfType<TextBlock>().Any(t => t.FontSize == 14), "Note preview text is undersized.");
            scroller.ScrollToVerticalOffset(80); Pump(30); Require(scroller.VerticalOffset > 0, "Note preview did not scroll.");
            popup.IsOpen = false; Pump(80); popup.IsOpen = true; Pump(230);
            Require(popup.Child.Opacity == 1, "A reopened preview did not settle.");
        }
        popup.IsOpen = false; Pump(50);
        RemainingCall(map, "UpdateDropTargetHover", (object?)null);
        if (!baseline)
        {
            foreach (var nodeBorder in canvas.Children.OfType<Border>().Where(b => b.Tag is MindMapNode))
            {
                var contentGrid = (Grid)nodeBorder.Child;
                if (contentGrid.Children.OfType<StackPanel>().FirstOrDefault() is { } metadata)
                    Require(metadata.DesiredSize.Width + nodeBorder.Padding.Left + nodeBorder.Padding.Right
                        + nodeBorder.BorderThickness.Left + nodeBorder.BorderThickness.Right <= nodeBorder.Width + .1,
                        "Wider badge spacing pushed metadata beyond the node.");
            }
            var theme = Scriblucent.ThemeManager.CurrentTheme;
            var previousRevision = (int)RemainingField(map, "_tileRevision")!;
            Scriblucent.ThemeManager.SetTheme(theme == "Light" ? "Dark" : "Light"); Pump(400);
            Require((int)RemainingField(map, "_tileRevision")! > previousRevision,
                "A live theme change retained the old map snapshot.");
            Scriblucent.ThemeManager.SetTheme(theme); Pump(400);
            var foldButton = ControlTree(canvas.Children.OfType<Border>().Single(b => ReferenceEquals(b.Tag, root)))
                .OfType<Button>().First(b => b.Content is AppIcon { Kind: AppIconKind.ChevronDown });
            RemainingKeyboardClick(foldButton); Pump(250);
            Require(!root.IsExpanded && !(bool)RemainingField(map, "_isManualDragPending")!, "Fold activation started a drag or failed.");
            Require(!popup.IsOpen, "Rebuilding the node left its preview open.");
        }
        map.DisposeWorkspaceEditor(); Pump(350);
        checks.Add(suffix + (baseline ? ": baseline map capture." :
            ": generated map actions, authored borders, drop feedback, bounded preview, snapshot metadata, live theme refresh and zoom restoration checked."));
    }
    private static void CheckRemainingSnapshot(MindMapPreviewWindow map, MindMapTileSnapshot snapshot, string suffix)
    {
        Require(snapshot.Nodes.Sum(n => n.Glyphs.Count) >= 8, "Zoom snapshot omitted command or metadata glyphs.");
        Require(snapshot.Nodes.Any(n => n.Metadata.Any(t => t.Text == "2")), "Zoom snapshot omitted the collapsed count.");
        Require(snapshot.Nodes.Count(n => n.RimContour is not null) == 2, "Zoom snapshot omitted selection/drop feedback.");
        Require(snapshot.Nodes.Any(n => n.Title is { Italic: true }), "Zoom snapshot omitted authored italic text.");
        foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            using var bitmap = SkiaMindMapRenderer.RenderFull(snapshot, 1, dpi, out var scale);
            var image = SkiaMindMapSnapshotCache.ToBitmapSource(bitmap, dpi);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(_output, $"map-tile-{suffix}-{(int)(dpi * 100)}.png")); encoder.Save(file);
            Require(bitmap.GetPixel((int)((snapshot.Nodes[0].X + snapshot.Nodes[0].Width / 2) * scale),
                (int)((snapshot.Nodes[0].Y + snapshot.Nodes[0].Height / 2) * scale)).Alpha > 0,
                "Snapshot contour failed to render: " + snapshot.Nodes[0].OuterContour + "; background " + snapshot.Nodes[0].Background.ToString("X8"));
            foreach (var outlined in snapshot.Nodes.Where(n => n.RimContour is not null))
            {
                var pixel = bitmap.GetPixel((int)((outlined.X + outlined.Width / 2) * scale), (int)((outlined.Y + 1) * scale));
                Require(pixel.Blue > pixel.Red + 40, "Snapshot omitted the visible blue rim: " + outlined.RimContour);
            }
        }
        var count = 0; SkiaMindMapSnapshotCache.Clear();
        SkiaMindMapSnapshotCache.Request(snapshot, 1, 1.25f, result => { Require(result is not null, "First render failed."); count++; });
        SkiaMindMapSnapshotCache.Request(snapshot, 1, 1.25f, result => { Require(result is not null, "Coalesced render failed."); count++; });
        var wait = Stopwatch.StartNew(); while (count < 2 && wait.ElapsedMilliseconds < 4000) Pump(20);
        Require(count == 2, "Coalesced rendering lost a completion callback.");
        // Clearing the cache invalidates the prepared layer; restore it before the zoom check.
        RemainingCall(map, "WarmSnapshot"); Pump(150);
    }
    private static void RemainingPicker(string suffix, List<string> checks, bool baseline)
    {
        var picker = new ColorPickerDialog(); picker.Show(); Pump(300);
        var spectrum = (Border)picker.FindName("SpectrumBorder");
        var thumb = (FrameworkElement)picker.FindName("SpectrumThumb");
        foreach (var (x, y, expected) in new[] { (0d, 0d, "#FFFFFF"), (1d, 0d, "#FF0000"),
                     (0d, 1d, "#000000"), (1d, 1d, "#000000") })
        {
            RemainingCall(picker, "SampleSpectrum", new Point(x * spectrum.ActualWidth, y * spectrum.ActualHeight));
            Pump(30);
            Require(((TextBox)picker.FindName("HexTextBox")).Text == expected, "Spectrum endpoint color changed.");
            if (!baseline)
            {
                Require(!ReferenceEquals(((FrameworkElement)thumb.Parent).Parent, spectrum.Child), "Marker still lives inside the spectrum clip.");
                var center = thumb.TranslatePoint(new Point(7, 7), spectrum);
                Require(Math.Abs(center.X - x * spectrum.ActualWidth) < .01 && Math.Abs(center.Y - y * spectrum.ActualHeight) < .01,
                    $"Marker and HSV sample coordinates diverged: {center} vs {x * spectrum.ActualWidth},{y * spectrum.ActualHeight}.");
            }
            SaveControlCapture((FrameworkElement)picker.Content, $"picker-{x}-{y}-" + suffix);
        }
        picker.Close(); Pump(250);
        checks.Add(suffix + ": exact spectrum endpoint colors and complete overlay marker geometry checked at four capture scales.");
    }
    private static void RemainingCaption(string suffix, List<string> checks, bool baseline)
    {
        var dialog = new InsertCaptionDialog("", "A caption", CaptionNumbering.Arabic, ": ",
            CaptionPosition.Below, false, new[] { "Figure", "Diagram" }, new[] { "Figure" }, _ => 1, false);
        dialog.Show(); Pump(550);
        SaveControlCapture((FrameworkElement)dialog.Content, "caption-idle-" + suffix);
        if (!baseline)
        {
            var entry = (ComboBox)dialog.FindName("LabelComboBox");
            var caption = (TextBox)dialog.FindName("CaptionTextBox");
            var position = caption.TranslatePoint(new Point(), (UIElement)dialog.Content);
            RemainingCall(dialog, "Ok_Click", dialog, new RoutedEventArgs()); Pump(170);
            var message = (InlineValidationPresenter)dialog.FindName("LabelValidationMessage");
            Require(message.IsError && ((TextBlock)message.Child).Opacity == 1, "Inline caption error was not shown.");
            Require(entry.IsKeyboardFocusWithin, "Invalid caption did not focus the label.");
            Require(caption.TranslatePoint(new Point(), (UIElement)dialog.Content) == position,
                $"Validation shifted the form: {position} to {caption.TranslatePoint(new Point(), (UIElement)dialog.Content)}.");
            SaveControlCapture((FrameworkElement)dialog.Content, "caption-error-" + suffix);
            var edit = (TextBox)entry.Template.FindName("PART_EditableTextBox", entry);
            edit.Text = "Pasted label"; Pump(170);
            Require(!message.IsError, "Editing did not clear caption validation.");
            entry.Text = ""; RemainingCall(dialog, "Ok_Click", dialog, new RoutedEventArgs()); Pump(160);
            entry.SelectedIndex = 0; Pump(170);
            Require(!message.IsError, "Choosing a label did not clear caption validation.");
        }
        dialog.Close(); Pump(250);
        checks.Add(suffix + ": caption validation, focus, stable form layout, editable correction and selection checked.");
    }
    private static void RemainingActions(string suffix, List<string> checks, bool baseline)
    {
        var cards = new FlashcardsPreviewWindow(new[] { new FlashcardItem { Question = "A useful question", Answer = "An answer", Hint = "A small clue" } });
        cards.Show(); Pump(450);
        RemainingCall(cards, "StartStudyModeButton_Click", cards, new RoutedEventArgs()); Pump(300);
        SaveControlCapture((FrameworkElement)cards.Content, "study-hint-" + suffix);
        if (!baseline)
        {
            var hint = ControlTree(cards).OfType<Button>().First(b => b.Content as string == LocalizationService.GetString("FlashcardShowHint"));
            var host = (FlashcardFlipHost)cards.FindName("StudyModeFlipHost");
            Require(hint.Focusable && hint.Template.FindName("ButtonBorder", hint) is InteractionSurface, "Hint is not a shared native command.");
            RemainingKeyboardClick(hint); Pump(180);
            Require(!host.IsFlipped && hint.Visibility == Visibility.Collapsed, "Hint activation flipped the card or did not reveal the hint.");
            SaveControlCapture((FrameworkElement)cards.Content, "study-hint-revealed-" + suffix);
        }
        cards.DisposeWorkspaceEditor(); Pump(300);
        var quiz = new QuizModeWindow(new QuizDocument { Questions = new() { new() { Question = "Keep the timer steady", Options = new() { new() { Text = "First", IsCorrect = true }, new() { Text = "Second" } } } } }, 60);
        quiz.Show(); Pump(350);
        RemainingCall(quiz, "UpdateTimerDisplay", TimeSpan.FromSeconds(30));
        RemainingCall(quiz, "HighlightTimerWarning"); Pump(180);
        SaveControlCapture((FrameworkElement)quiz.Content, "quiz-warning-" + suffix);
        if (!baseline)
        {
            var timer = (TextBlock)quiz.FindName("TimerText");
            Require(ReferenceEquals(timer.Foreground, quiz.FindResource("AccentWarningSubtleForeground")), "Timer bypassed semantic warning colors.");
            RemainingCall(quiz, "ResetTimerForNewSession"); Pump(60);
            Require(ReferenceEquals(timer.Foreground, quiz.FindResource("TextColor")), "Timer warning did not reset.");
        }
        quiz.Close(); Pump(350);
        var image = new FlashcardImageViewerWindow(PhotoFixture()); image.Show(); Pump(450);
        SaveControlCapture((FrameworkElement)image.Content, "photo-close-" + suffix);
        if (!baseline)
        {
            var close = (Button)image.FindName("CloseButton");
            Require(close.Focusable && close.Template.FindName("ButtonBorder", close) is InteractionSurface, "Photo close bypassed shared feedback.");
            RemainingKeyboardClick(close); Pump(400);
            Require(!image.IsVisible, "Photo close failed to dismiss the viewer.");
        }
        else image.Close();
        var indicator = typeof(MiddleClickScrollBehavior).GetMethod("CreateIndicatorVisual", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var root = (FrameworkElement)indicator.GetType().GetProperty("Root")!.GetValue(indicator)!;
        var markerWindow = HostSurfaceFixture(root, 120, 100); Pump(80);
        SaveControlCapture((FrameworkElement)markerWindow.Content, "scroll-marker-" + suffix);
        if (!baseline) Require(ControlTree(root).OfType<SurfaceMaterialBorder>().Any(), "Scroll marker bypassed the shared surface.");
        markerWindow.Close(); Pump(80);
        checks.Add(suffix + ": hint command, photo close, steady themed countdown and middle-click marker checked.");
    }
}
