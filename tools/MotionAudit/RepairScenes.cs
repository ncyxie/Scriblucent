using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Icons;

internal static partial class Program
{
    private static void RecordRepairs()
    {
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadResources(theme); ReducedMotionPreferences.Configure(MotionPreference.Automatic);
            var suffix = theme.ToLowerInvariant();
            var owner = new Window { Content = new Grid(), Width = 960, Height = 680, ShowInTaskbar = false, ShowActivated = false };
            owner.Show(); Pump(60);
            using (var sidebar = new SidebarFixture(owner))
            {
                sidebar.Resize(232); Pump(350);
                SaveGif(CaptureFeedback(sidebar.Root, () => sidebar.Resize(64), 26,
                    new() { [12] = () => sidebar.Destinations[2].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent }),
                        [17] = () => sidebar.Select(2), [21] = () => sidebar.Resize(232) }), $"sidebar-close-hover-and-stage-shadow-{suffix}.gif");
            }
            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var buttons = new[] { "DialogSecondaryButtonStyle", "DialogPrimaryButtonStyle", "DialogDangerButtonStyle" }.Select((style, i) =>
                new Button { Content = new[] { "Keep editing", "Save changes", "Delete" }[i], Style = (Style)_app.FindResource(style), Margin = new(12), MinWidth = 150 }).ToArray();
            foreach (var button in buttons) buttonRow.Children.Add(button);
            var buttonRoot = new Grid { Children = { buttonRow } }; buttonRoot.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
            owner.Content = buttonRoot; owner.UpdateLayout(); Pump(60);
            void HoverButtons(bool entered)
            {
                foreach (var button in buttons) button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                    { RoutedEvent = entered ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent });
            }
            HoverButtons(false); Pump(220);
            SaveGif(CaptureFeedback(buttonRoot, () => HoverButtons(true), 24, new() { [11] = () => HoverButtons(false), [18] = () => HoverButtons(true) }), $"filled-button-light-{suffix}.gif");

            // Production Settings overlay XAML, handlers removed; no SettingsPanel constructor/settings reads.
            var source = XDocument.Load("Scriblucent/Views/SettingsPanel.xaml");
            var clone = new XElement(source.Root!); XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            clone.Attribute(x + "Class")!.Remove(); StripHandlers(clone);
            var settings = Fragment(clone, source.Root!); owner.Content = settings; owner.UpdateLayout(); Pump(80);
            var viewer = Descendants(settings).OfType<ScrollViewer>().First(v => v.Content is StackPanel);
            Scriblucent.Behaviors.SmoothScrollBehavior.SetIsEnabled(viewer, true);
            if (viewer.ScrollableHeight <= 500 || viewer.CanContentScroll) throw new InvalidOperationException("Settings must have a pixel scroll viewport.");
            SaveGif(CaptureFeedback(settings, () => viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent }), 36, new() { [18] = () => viewer.ScrollToEnd() }), $"settings-wheel-to-lower-sections-{suffix}.gif");
            if (viewer.VerticalOffset < viewer.ScrollableHeight - 1) throw new InvalidOperationException("Settings lower sections must be reachable.");
            Console.WriteLine($"Settings {theme}: viewport {viewer.ViewportHeight:F1}, extent {viewer.ExtentHeight:F1}, final offset {viewer.VerticalOffset:F1}");

            var cards = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var cardFaces = new List<Border>();
            foreach (var title in new[] { "Flashcards", "Quizzes", "Mind maps" })
            {
                var face = new Border { CornerRadius = new(14), BorderThickness = new(1), Padding = new(18), RenderTransformOrigin = new(.5, .5),
                    Child = new TextBlock { Text = title, FontSize = 19, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
                face.SetResourceReference(Border.BackgroundProperty, "CardBackground"); face.SetResourceReference(Border.BorderBrushProperty, "NoteCardBorder");
                ((TextBlock)face.Child).SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
                var stage = new Grid { Width = 244, Height = 180, Margin = new(14), Children = { new CardShadowLayer(), face } };
                cards.Children.Add(stage); cardFaces.Add(face);
            }
            var cardRoot = new Grid { Children = { cards } }; cardRoot.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
            owner.Content = cardRoot; owner.UpdateLayout(); Pump(50);
            SaveGif(CaptureFeedback(cardRoot, () =>
            {
                foreach (var face in cardFaces)
                {
                    UiMotion.EnsureScale(face).ScaleX = UiMotion.EnsureScale(face).ScaleY = .88;
                    SurfaceMotion.AnimateTracks(face, MotionChannel.Presence,
                        [new(UiMotion.EnsureScale(face), ScaleTransform.ScaleXProperty, 1, true), new(UiMotion.EnsureScale(face), ScaleTransform.ScaleYProperty, 1, true)], 300, UiMotion.OutCubic);
                }
            }, 26, new() { [12] = () =>
                {
                    foreach (var face in cardFaces)
                    {
                        SurfaceFill.Select(face, true, true); AccentMotion.Set(face, true, (Brush)_app.FindResource("NoteCardSelectionBorder"));
                        var tracks = new List<NativeMotionTrack> { new(UiMotion.ChannelTranslate(face, MotionChannel.Interaction), TranslateTransform.YProperty, -2, true) };
                        SurfaceLight.AddTracks(face, tracks, true); SurfaceMotion.AnimateTracks(face, MotionChannel.Interaction, tracks, 160, UiMotion.OutSine);
                    }
                } }), $"aligned-card-shadow-and-rim-{suffix}.gif");

            var recent = new ScrollViewer { Style = (Style)_app.FindResource("RecentItemsStripScrollViewerStyle"), VerticalAlignment = VerticalAlignment.Center, Margin = new(35) };
            var items = new ItemsControl { ItemsPanel = (ItemsPanelTemplate)_app.FindResource("RecentItemsStripPanelTemplate") };
            foreach (var title in new[] { "A recent note", "A recent quiz", "A recent mind map", "A recent flashcard set", "One more idea" })
                items.Items.Add(new Button { Content = title, Width = 210, Height = 54, Style = (Style)_app.FindResource("RecentNoteCardStyle") });
            recent.Content = items; var recentRoot = new Grid { Children = { recent } }; recentRoot.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
            owner.Content = recentRoot; owner.UpdateLayout(); Pump(50);
            SaveGif(CaptureFeedback(recentRoot, () => recent.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent }), 30, new() { [18] = () => recent.ScrollToRightEnd() }), $"recent-shadow-clearance-{suffix}.gif");

            var selectedRows = new StackPanel { Margin = new(24) };
            var outlines = new List<Border>();
            for (int i = 0; i < 14; i++)
            {
                var row = new Border { Height = 100, CornerRadius = new(14), BorderThickness = new(1), Padding = new(18),
                    Child = new TextBlock { Text = $"Selected card {i + 1} / continuous feedback", FontSize = 17 } };
                row.SetResourceReference(Border.BackgroundProperty, "CardBackground"); row.SetResourceReference(Border.BorderBrushProperty, "NoteCardBorder");
                ((TextBlock)row.Child).SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
                selectedRows.Children.Add(new Grid { Margin = new(17), Children = { new CardShadowLayer(), row } }); outlines.Add(row);
            }
            var selectedScroll = new ScrollViewer { Content = selectedRows, CanContentScroll = false };
            selectedScroll.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
            owner.Content = selectedScroll; owner.UpdateLayout(); Pump(50);
            foreach (var row in outlines) { SurfaceFill.Select(row, true, false); AccentMotion.Set(row, true, (Brush)_app.FindResource("NoteCardSelectionBorder"), animate: false); }
            void ScrollPose(double offset, bool commit = false) => SmoothScrollCompositor.TryApply(selectedScroll, 0, offset, commit, out _, out _);
            SaveGif(CaptureFeedback(selectedScroll, () => { SmoothScrollCompositor.Begin(selectedScroll, []); ScrollPose(8); }, 25,
                new() { [3] = () => ScrollPose(32), [6] = () => ScrollPose(68), [9] = () => ScrollPose(110, true), [12] = () => ScrollPose(170),
                    [16] = () => SmoothScrollCompositor.End(selectedScroll, 0, 170) }), $"selection-during-drift-and-commit-{suffix}.gif");

            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            empty.Children.Add(new EmptyStateIllustration { IconKind = AppIconKind.Flashcards });
            var prompt = new TextBlock { Text = "Create your first flashcard", FontSize = 19, Margin = new(0, 15, 0, 18), HorizontalAlignment = HorizontalAlignment.Center };
            prompt.SetResourceReference(TextBlock.ForegroundProperty, "TextColor"); empty.Children.Add(prompt);
            empty.Children.Add(new Button { Content = "Add flashcard", Style = (Style)_app.FindResource("DialogPrimaryButtonStyle"), HorizontalAlignment = HorizontalAlignment.Center });
            var emptyRoot = new Grid(); emptyRoot.SetResourceReference(Panel.BackgroundProperty, "WindowBackground"); owner.Content = emptyRoot; owner.UpdateLayout();
            SaveGif(CaptureFeedback(emptyRoot, () => emptyRoot.Children.Add(empty), 22), $"flashcard-editor-empty-artwork-{suffix}.gif");
            owner.Close(); Pump(50); WaitForRest();
        }
        File.WriteAllText(Path.Combine(_output, "capture-scope.txt"), "Production sidebar and Settings layouts; production button/recent templates; synthetic card faces sharing production shadow, contour and motion; shared flashcard editor artwork. No Scriblucent.App startup, user settings or documents. Captures are offscreen render readback, not presented-frame performance traces.");
    }
}
