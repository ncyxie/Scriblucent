using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;
using Scriblucent.Services;

internal static partial class Program
{
    private sealed class PolishFixture : IDisposable
    {
        internal readonly Window Window;
        internal readonly Grid Root = new();
        internal readonly List<InteractionSurface> Faces = new();
        internal readonly Border Selection = new() { Padding = new Thickness(18), CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 14, 0, 12),
            Child = new TextBlock { Text = "A clear choice, with a quiet finish", FontSize = 16 } };
        internal readonly Border Context = new() { Child = new Border { Width = 170, Height = 36,
            Child = new TextBlock { Text = "Selected image tools", VerticalAlignment = VerticalAlignment.Center } } };
        internal readonly EditorTitleStatus Status = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

        internal PolishFixture(Window owner)
        {
            Root.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
            var stack = new StackPanel { Margin = new Thickness(28) };
            Root.Children.Add(new AdornerDecorator { Child = stack });
            var heading = new TextBlock { Text = "Scriblucent: everyday feedback", FontSize = 22, Margin = new Thickness(0, 0, 0, 20) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextColor"); stack.Children.Add(heading);
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; stack.Children.Add(actions);
            foreach (var (title, style) in new[] { ("Keep editing", "DialogSecondaryButtonStyle"), ("Save note", "DialogPrimaryButtonStyle"), ("Remove", "ChromeDangerIconButtonStyle") })
                actions.Children.Add(new Button { Content = title, Width = 135, Height = 40, Margin = new Thickness(0, 0, 12, 0), Style = (Style)_app.FindResource(style) });
            Selection.SetResourceReference(Border.BackgroundProperty, "NoteCardBackground");
            ((TextBlock)Selection.Child).SetResourceReference(TextBlock.ForegroundProperty, "TextColor"); stack.Children.Add(Selection);
            var contextRow = new StackPanel { Orientation = Orientation.Horizontal };
            var tag = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 14, 0), Child = new TextBlock { Text = "A small detail" } };
            tag.SetResourceReference(Border.BackgroundProperty, "InputSurface");
            ((TextBlock)tag.Child).SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
            TagChipMotionBehavior.SetIsEnabled(tag, true); contextRow.Children.Add(tag); contextRow.Children.Add(Context); stack.Children.Add(contextRow);
            ((TextBlock)((Border)Context.Child).Child).SetResourceReference(TextBlock.ForegroundProperty, "TextColorSecondary");
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
            var titleLabel = new TextBlock { Text = "My next idea", FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
            titleLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextColor"); statusRow.Children.Add(titleLabel);
            Status.SetResourceReference(Control.ForegroundProperty, "TextColorSecondary");
            statusRow.Children.Add(Status); stack.Children.Add(statusRow);
            Window = new Window { Content = Root, Owner = owner, Width = 650, Height = 370,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            Window.Show(); Window.UpdateLayout(); Pump(50);
            foreach (Button button in actions.Children)
                Faces.Add((InteractionSurface)button.Template.FindName("ButtonBorder", button));
            EditorToolbarReveal.SetVisible(Context, false, false);
        }
        internal void Hover(bool active)
        { foreach (var face in Faces) face.AnimateState(active, false); }
        internal void Save()
        { Status.ShowSaved(() => Status.SetEdited(true)); }
        internal void Select(bool selected)
        { SurfaceFill.Select(Selection, selected, true); AccentMotion.Set(Selection, selected, Selection.TryFindResource("AccentPrimary") as Brush); }
        public void Dispose() { Window.Close(); Pump(40); WaitForRest(); }
    }
    // Raster capture can stall the dispatcher. Stage input by captured frame
    // so a busy first raster cannot swallow an entire hover or selection.
    private static List<System.Windows.Media.Imaging.BitmapSource> CaptureFeedback(
        FrameworkElement root, Action start, int frames, Dictionary<int, Action>? changes = null, Action? afterFrame = null)
    {
        var result = new List<System.Windows.Media.Imaging.BitmapSource>();
        start();
        for (var frame = 0; frame < frames; frame++)
        {
            if (changes?.TryGetValue(frame, out var action) == true) action();
            Pump(33); result.Add(Snapshot(root, .65)); afterFrame?.Invoke();
        }
        return result;
    }
    private static void RecordFourthPass(Scene scene, string theme)
    {
        using var fixture = new PolishFixture(scene.Window); var suffix = theme.ToLowerInvariant();
        SaveGif(CaptureFeedback(fixture.Root, () => fixture.Hover(true), 24,
            new() { [5] = () => fixture.Faces[1].AnimateState(true, true),
                [9] = () => fixture.Faces[1].AnimateState(true, false), [14] = () => fixture.Hover(false) }), $"control-light-and-press-{suffix}.gif");
        SaveGif(CaptureFeedback(fixture.Root, () => fixture.Select(true), 22,
            new() { [12] = () => fixture.Select(false) }), $"selection-wash-{suffix}.gif");
        SaveGif(CaptureFeedback(fixture.Root, () => EditorToolbarReveal.SetVisible(fixture.Context, true), 22,
            new() { [12] = () => EditorToolbarReveal.SetVisible(fixture.Context, false) }), $"context-tools-{suffix}.gif");
        SaveGif(Capture(fixture.Root, fixture.Save, 750), $"save-finish-{suffix}.gif");
        Pump(650); WaitForRest();
        ReducedMotionPreferences.Configure(MotionPreference.Reduced);
        SaveGif(Capture(fixture.Root, () => { fixture.Hover(true); fixture.Select(true); fixture.Save(); }, 250), $"reduced-control-states-{suffix}.gif");
        ReducedMotionPreferences.Configure(MotionPreference.Automatic);
    }
    private static void BenchmarkFourthPass(Scene scene, string theme, MotionPreference preference)
    {
        using var fixture = new PolishFixture(scene.Window);
        Measure($"{theme}/{preference}/control-light", scene, () => fixture.Hover(true), 240);
        Measure($"{theme}/{preference}/control-press", scene, () => fixture.Faces[1].AnimateState(true, true), 200);
        fixture.Hover(false); Pump(180); WaitForRest();
        Measure($"{theme}/{preference}/selection-fill", scene, () => fixture.Select(true), 240);
        Measure($"{theme}/{preference}/context-tools", scene, () => EditorToolbarReveal.SetVisible(fixture.Context, true), 250);
        Measure($"{theme}/{preference}/save-finish", scene, fixture.Save, 1350);
    }
}
