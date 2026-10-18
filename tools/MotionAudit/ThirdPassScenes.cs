using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Services;
using Scriblucent.Views;

internal static partial class Program
{
    private static RichTextBox EditorFixture()
    {
        var document = new FlowDocument { PagePadding = new Thickness(28), FontSize = 16 };
        document.Blocks.Add(new Paragraph(new Run("Design should feel alive.")) { FontSize = 28, FontWeight = FontWeights.SemiBold });
        document.Blocks.Add(new Paragraph(new Run("Visual depth, useful continuity, and a little personality.")) { FontSize = 18 });
        foreach (var text in new[]
        {
            "A surface arrives as one object. Its shadow moves with it, its title leads, and its actions follow.",
            "Selection travels between related destinations. Reading and editing remain immediately available.",
            "Meaningful moments have room for expression. The interface returns to a quiet resting state."
        }) document.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0, 0, 0, 18) });
        var editor = new RichTextBox { Document = document, BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        editor.SetResourceReference(Control.BackgroundProperty, "CardBackground");
        editor.SetResourceReference(Control.ForegroundProperty, "TextColor");
        return editor;
    }
    private static Window FeatureFixture(Window owner, bool large = false)
    {
        var title = new TextBlock { Text = "Notes  /  Material and motion", FontSize = 23, Margin = new Thickness(22, 20, 22, 16) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 14, 20, 16) };
        foreach (var label in new[] { "Close", "Save note" }) footer.Children.Add(new Button { Content = label, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(8, 0, 0, 0) });
        var shell = new FeatureWindowShell { Header = title, Body = EditorFixture(), Footer = footer };
        var window = new Window { Content = shell, Title = "Scriblucent motion fixture", Owner = owner,
            Width = large ? 2050 : 1050, Height = large ? 1350 : 720, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
        WindowThemeService.Register(window); WindowContentEnterHelper.AnimateOnLoaded(window);
        return window;
    }
    private static NoteEditorTabsWindow TabsFixture(Window owner)
    {
        var window = new NoteEditorTabsWindow { Owner = owner, ShowInTaskbar = false };
        var tabs = (TabControl)window.FindName("EditorTabs");
        foreach (var name in new[] { "Design should feel alive", "Material and motion", "A new idea" }) tabs.Items.Add(new TabItem { Header = name });
        tabs.SelectedIndex = 0;
        window.Show(); window.UpdateLayout();
        var host = (ContentControl)tabs.Template.FindName("EditorHost", tabs); host.Content = EditorFixture();
        Pump(600); WaitForRest(); return window;
    }
    private static (Window Window, OverlayDialogShell Shell) DialogFixture(Window owner)
    {
        var title = new TextBlock { Text = "Keep this idea?", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
        var body = new TextBlock { Text = "Save your changes before moving to another note.", FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 24) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextColorSecondary");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(new Button { Content = "Keep editing", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) });
        actions.Children.Add(new Button { Content = "Save note", Padding = new Thickness(16, 8, 16, 8) });
        var shell = new OverlayDialogShell { Header = title, Body = body, Footer = actions };
        var window = new Window { Content = shell, Owner = owner, WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        OverlayDialogAnimator? animator = null; var closing = false;
        window.SourceInitialized += (_, _) => OverlayDialogBoundsHelper.Apply(window);
        window.Loaded += (_, _) => { animator = shell.BindAnimator(window); animator.BeginOpen(); };
        window.Closing += (_, args) =>
        {
            if (closing || UiMotion.PrefersReducedMotion) return;
            args.Cancel = true; closing = true; animator?.BeginClose(window.Close);
        };
        return (window, shell);
    }
    private static BitmapImage PhotoFixture()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(42, 62, 115), Color.FromRgb(232, 167, 151), 90), null, new Rect(0, 0, 1200, 800));
            drawing.DrawEllipse(Brushes.PeachPuff, null, new Point(850, 250), 76, 76);
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(47, 85, 100)), null,
                Geometry.Parse("M 0,800 L 0,550 L 240,360 L 450,560 L 760,350 L 1200,610 L 1200,800 Z"));
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(21, 42, 66)), null,
                Geometry.Parse("M 0,800 L 0,680 L 360,560 L 650,720 L 960,550 L 1200,650 L 1200,800 Z"));
        }
        var bitmap = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    private static void RecordThirdPass(Scene scene, string theme)
    {
        var suffix = theme.ToLowerInvariant();
        var feature = FeatureFixture(scene.Window);
        SaveGif(Capture((FrameworkElement)feature.Content, feature.Show, 650), $"secondary-window-{suffix}.gif");
        SaveGif(Capture((FrameworkElement)feature.Content, feature.Close, 260), $"secondary-window-exit-{suffix}.gif");
        Pump(150); WaitForRest();
        var large = FeatureFixture(scene.Window, true);
        SaveGif(Capture((FrameworkElement)large.Content, large.Show, 650), $"secondary-window-large-{suffix}.gif");
        WindowContentEnterHelper.AllowImmediateClose(large); large.Close(); Pump(50);
        var tabsWindow = TabsFixture(scene.Window); var tabs = (TabControl)tabsWindow.FindName("EditorTabs");
        SaveGif(Capture((FrameworkElement)tabsWindow.Content, () => tabs.SelectedIndex = 2, 450), $"editor-tabs-{suffix}.gif");
        WindowContentEnterHelper.AllowImmediateClose(tabsWindow); tabsWindow.Close(); Pump(50);
        var dialog = DialogFixture(scene.Window);
        SaveGif(Capture(dialog.Shell, dialog.Window.Show, 650), $"dialog-material-{suffix}.gif");
        dialog.Window.Close(); Pump(350); WaitForRest();
        var thumbnail = new Image { Source = PhotoFixture(), Width = 160, Height = 110, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(35) };
        scene.Root.Children.Add(thumbnail); scene.Window.Activate(); Pump(60);
        var viewer = new FlashcardImageViewerWindow((BitmapImage)thumbnail.Source, thumbnail) { Owner = scene.Window };
        SaveGif(Capture((FrameworkElement)viewer.Content, viewer.Show, 650), $"image-origin-{suffix}.gif");
        SaveGif(Capture((FrameworkElement)viewer.Content, viewer.Close, 260), $"image-return-{suffix}.gif");
        Pump(150); WaitForRest(); scene.Root.Children.Remove(thumbnail);
        ReducedMotionPreferences.Configure(MotionPreference.Reduced);
        scene.Empty.Visibility = Visibility.Visible;
        SaveGif(Capture(scene.Root, () => UiMotion.AnimateEmptyDashboardEnter(scene.Empty), 200), $"reduced-empty-{suffix}.gif");
        scene.Empty.Visibility = Visibility.Collapsed;
        ReducedMotionPreferences.Configure(MotionPreference.Automatic);
        if (MotionCacheBudget.ReservedPixels != 0 || RenderTransitionClock.IsAnyRunning)
            throw new InvalidOperationException("Third-pass window or material cleanup left activity or a texture lease behind.");
    }
    private static void BenchmarkThirdPass(Scene scene, string theme, MotionPreference preference)
    {
        foreach (var large in new[] { false, true })
        {
            var window = FeatureFixture(scene.Window, large); window.Show(); Pump(500); WaitForRest();
            var shell = Find<FeatureWindowShell>((DependencyObject)window.Content)!;
            Measure($"{theme}/{preference}/secondary-{(large ? "large" : "normal")}-enter", scene,
                () => { WindowMotion.Prepare(shell); UiMotion.AnimateWindowContentEnter(shell); }, 420);
            Measure($"{theme}/{preference}/secondary-{(large ? "large" : "normal")}-exit", scene,
                () => UiMotion.AnimateWindowContentExit(shell), 220);
            WindowContentEnterHelper.AllowImmediateClose(window); window.Close(); Pump(50);
        }
        var tabsWindow = TabsFixture(scene.Window); var tabs = (TabControl)tabsWindow.FindName("EditorTabs");
        Measure($"{theme}/{preference}/editor-tab-selection", scene, () => tabs.SelectedIndex = 2, 320);
        WindowContentEnterHelper.AllowImmediateClose(tabsWindow); tabsWindow.Close(); Pump(40);
        var dialog = DialogFixture(scene.Window);
        Measure($"{theme}/{preference}/dialog-open", scene, dialog.Window.Show, 440);
        Measure($"{theme}/{preference}/dialog-close", scene, dialog.Window.Close, 240);
        var thumbnail = new Image { Source = PhotoFixture(), Width = 160, Height = 110, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(35) };
        scene.Root.Children.Add(thumbnail); scene.Window.UpdateLayout(); Pump(40);
        var viewer = new FlashcardImageViewerWindow((BitmapImage)thumbnail.Source, thumbnail) { Owner = scene.Window };
        Measure($"{theme}/{preference}/image-open", scene, viewer.Show, 460);
        Measure($"{theme}/{preference}/image-close", scene, viewer.Close, 240);
        scene.Root.Children.Remove(thumbnail);
        if (MotionCacheBudget.ReservedPixels != 0 || RenderTransitionClock.IsAnyRunning)
            throw new InvalidOperationException("Window benchmarks left activity or material leases behind.");

    }
}
