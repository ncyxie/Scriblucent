using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class MotionThirdPassTests
{
    private static void Resources(Window window)
    {
        foreach (var name in new[] { "Tokens", "LightTheme", "Generic", "Controls/Shared", "Controls/Chrome", "Controls/Buttons" })
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/Scriblucent;component/Themes/{name}.xaml") });
    }
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 6000) Pump(20);
        Assert.True(condition(), "Asynchronous motion work did not complete within the bounded wait.");
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var preference = MotionPolicy.Preference;
            var system = (bool)typeof(MotionPolicy).GetField("_systemOff", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var material = typeof(MotionPolicy).GetField("_transparencyEnabled", BindingFlags.Static | BindingFlags.NonPublic)!;
            var transparency = material.GetValue(null);
            try { material.SetValue(null, true); MotionPolicy.Configure(MotionPreference.Automatic, false); action(); }
            catch (Exception error) { failure = error; }
            finally { MotionPolicy.Configure(preference, system); material.SetValue(null, transparency); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void Large_feature_body_with_custom_header_has_a_real_stage_and_cancels_stale_entry() => Sta(() =>
    {
        var header = new Border { Height = 60, Child = new TextBlock { Text = "A real custom title" } };
        WindowMotion.SetRole(header, WindowMotionRole.Chrome);
        var body = new Grid { Children = { header } };
        var shell = new FeatureWindowShell { Body = body };
        var window = new Window { Content = shell, Width = 1900, Height = 1300, Opacity = 0, ShowActivated = false };
        Resources(window);
        shell.Resources.MergedDictionaries.Add(window.Resources);
        shell.Style = (Style)window.FindResource(typeof(FeatureWindowShell));
        window.Show(); window.UpdateLayout(); Pump(40);
        try
        {
            var stage = (FrameworkElement)shell.Template.FindName("MotionStage", shell);
            var pixels = body.ActualWidth * body.ActualHeight * Math.Pow(VisualTreeHelper.GetDpi(body).DpiScaleX, 2);
            Assert.True(pixels > MotionCacheBudget.PerSurfacePixels);
            int entered = 0, exited = 0;
            WindowMotion.Prepare(shell); UiMotion.AnimateWindowContentEnter(shell, onComplete: () => entered++);
            Assert.True(NativeSurfaceMotion.IsRunning(stage, MotionChannel.Presence));
            Assert.True(NativeSurfaceMotion.IsRunning(header, MotionChannel.Presence));
            Assert.Null(stage.CacheMode);
            Pump(25); UiMotion.AnimateWindowContentExit(shell, onComplete: () => exited++);
            MotionPolicy.Configure(MotionPreference.Off, false);
            Assert.Equal(0, entered); Assert.Equal(1, exited);
            Assert.False(NativeSurfaceMotion.IsRunning(stage, MotionChannel.Presence));
            Assert.False(NativeSurfaceMotion.IsRunning(header, MotionChannel.Presence));
            Assert.Equal(0, MotionCacheBudget.ReservedPixels);
        }
        finally { WindowMotion.Stop(shell); window.Close(); Pump(20); }
    });

    [Fact]
    public void Animated_modal_close_preserves_accepted_result_and_runs_cleanup_once() => Sta(() =>
    {
        var modal = new Window { Content = new Border { Width = 120, Height = 90 }, Width = 260, Height = 220,
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        var cleanups = 0;
        WindowContentEnterHelper.AnimateOnLoaded(modal, beforeFinalClose: () => cleanups++);
        modal.Dispatcher.BeginInvoke(() => WindowContentEnterHelper.CloseWithDialogResult(modal, true), DispatcherPriority.ApplicationIdle);
        Assert.True(modal.ShowDialog());
        Assert.Equal(1, cleanups); Assert.False(WindowContentEnterHelper.IsEnterAnimating(modal));
        Assert.False(RenderTransitionClock.IsAnyRunning);
    });

    [Fact]
    public void Backdrop_captures_complex_high_dpi_owner_and_blurs_on_another_thread() => Sta(() =>
    {
        var panel = new Grid { Background = Brushes.CornflowerBlue };
        for (var i = 0; i < 220; i++) panel.Children.Add(new TextBlock { Text = $"Synthetic layer {i}", Margin = new Thickness(i % 12) });
        var window = new Window { Content = panel, Width = 1200, Height = 900, Opacity = 0, ShowActivated = false };
        window.Show(); window.UpdateLayout(); Pump(30);
        var uiThread = Environment.CurrentManagedThreadId;
        BitmapSource? first = null, second = null; var calls = 0;
        OverlayBackdropCapture.Measurement? measurement = null;
        void Measured(OverlayBackdropCapture.Measurement value) => measurement = value;
        OverlayBackdropCapture.Measured += Measured;
        var baseline = MotionCacheBudget.ReservedPixels;
        IDisposable? a = null, b = null;
        try
        {
            a = OverlayBackdropCapture.Request(window, image => { Assert.Equal(uiThread, Environment.CurrentManagedThreadId); first = image; calls++; });
            b = OverlayBackdropCapture.Request(window, image => { second = image; calls++; });
            Until(() => calls == 2 && measurement is not null);
            if (SystemParameters.HighContrast) { Assert.Null(first); return; }
            Assert.NotNull(first); Assert.Same(first, second); Assert.True(first.IsFrozen);
            Assert.True(measurement!.Value.Applied); Assert.Equal(uiThread, measurement.Value.CaptureThread);
            Assert.NotEqual(uiThread, measurement.Value.BlurThread);
            Assert.InRange(first.PixelWidth * first.PixelHeight, 1, MotionPolicy.LightweightRendering ? 200_000 : 350_000);
            a.Dispose(); a = null;
            Assert.True(MotionCacheBudget.ReservedPixels > baseline);
            b.Dispose(); b = null;
            Assert.Equal(baseline, MotionCacheBudget.ReservedPixels);
        }
        finally { a?.Dispose(); b?.Dispose(); OverlayBackdropCapture.Measured -= Measured; window.Close(); Pump(30); }
    });

    [Fact]
    public void Disposed_backdrop_cannot_publish_into_a_closed_or_reopened_surface() => Sta(() =>
    {
        var window = new Window { Content = new Grid { Background = Brushes.Coral }, Width = 700, Height = 500,
            Opacity = 0, ShowActivated = false };
        window.Show(); window.UpdateLayout();
        var stale = 0; var current = 0;
        var abandoned = OverlayBackdropCapture.Request(window, _ => stale++); abandoned.Dispose();
        using var latest = OverlayBackdropCapture.Request(window, _ => current++);
        Until(() => current == 1); Assert.Equal(0, stale);
        window.Close(); Pump(50);
        Assert.Equal(0, MotionCacheBudget.ReservedPixels);
    });

    [Fact]
    public void Backdrop_resize_publishes_only_the_current_owner_geometry() => Sta(() =>
    {
        var panel = new Grid { Background = Brushes.MediumPurple };
        var window = new Window { Content = panel, Width = 900, Height = 600, Opacity = 0, ShowActivated = false };
        window.Show(); window.UpdateLayout();
        BitmapSource? image = null; var calls = 0;
        using var request = OverlayBackdropCapture.Request(window, result => { image = result; calls++; });
        window.Width = 1250; window.UpdateLayout();
        Until(() => calls > 0);
        if (!SystemParameters.HighContrast)
        {
            Assert.NotNull(image);
            Assert.InRange(Math.Abs(image.Width - panel.ActualWidth), 0, 4);
        }
        window.Close(); Pump(40); Assert.Equal(0, MotionCacheBudget.ReservedPixels);
    });

    [Fact]
    public void Production_editor_tab_template_retargets_and_releases_its_native_clock_on_close() => Sta(() =>
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "Production", "NoteEditorTabsWindow.xaml");
        var document = System.Xml.Linq.XDocument.Load(file);
        System.Xml.Linq.XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var source = document.Root!;
        var dictionary = new System.Xml.Linq.XElement(wpf + "ResourceDictionary");
        foreach (var attribute in source.Attributes().Where(a => a.IsNamespaceDeclaration))
            dictionary.SetAttributeValue(attribute.Name, attribute.Value.StartsWith("clr-namespace:")
                ? attribute.Value + ";assembly=Scriblucent" : attribute.Value);
        dictionary.Add(new System.Xml.Linq.XElement(wpf + "ResourceDictionary.MergedDictionaries",
            new System.Xml.Linq.XElement(wpf + "ResourceDictionary", new System.Xml.Linq.XAttribute("Source", "/Scriblucent;component/Themes/Tokens.xaml")),
            new System.Xml.Linq.XElement(wpf + "ResourceDictionary", new System.Xml.Linq.XAttribute("Source", "/Scriblucent;component/Themes/Controls/Buttons.xaml"))));
        System.Xml.Linq.XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        dictionary.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "loc", "clr-namespace:Scriblucent.Localization;assembly=Scriblucent");
        dictionary.Add(new System.Xml.Linq.XElement(xaml + "Static", new System.Xml.Linq.XAttribute("Member", "loc:LocalizationProvider.Instance"),
            new System.Xml.Linq.XAttribute(xaml + "Key", "Loc")));
        foreach (var style in source.Element(wpf + "Window.Resources")!.Elements()) dictionary.Add(new System.Xml.Linq.XElement(style));
        foreach (var node in dictionary.DescendantsAndSelf())
        {
            // This fixture hosts the selection plate without the editor controller.
            // Its new-document handler is verified by the compiled workspace probe.
            foreach (var handler in node.Attributes().Where(a => a.Name.LocalName == "Click" && !a.Value.StartsWith("{")).ToArray())
                handler.Remove();
            if (node.Name.NamespaceName.StartsWith("clr-namespace:") && !node.Name.NamespaceName.Contains("assembly="))
                node.Name = System.Xml.Linq.XName.Get(node.Name.LocalName, node.Name.NamespaceName + ";assembly=Scriblucent");
            foreach (var attribute in node.Attributes().Where(a => !a.IsNamespaceDeclaration).ToArray())
                if (attribute.Name.NamespaceName.StartsWith("clr-namespace:") && !attribute.Name.NamespaceName.Contains("assembly="))
                {
                    var name = System.Xml.Linq.XName.Get(attribute.Name.LocalName, attribute.Name.NamespaceName + ";assembly=Scriblucent");
                    var value = attribute.Value; attribute.Remove(); node.SetAttributeValue(name, value);
                }
        }
        var styles = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
        var tabs = new TabControl { Style = (Style)styles["EditorTabControlStyle"], ItemContainerStyle = (Style)styles["EditorTabItemStyle"] };
        TabSelectionPlateBehavior.SetIsEnabled(tabs, true);
        var window = new Window { Content = tabs, Width = 1000, Height = 700, Opacity = 0, ShowActivated = false };
        window.Resources.MergedDictionaries.Add(styles);
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Scriblucent;component/Themes/LightTheme.xaml") });
        foreach (var title in new[] { "First note", "A wider second note", "Third note" }) tabs.Items.Add(new TabItem { Header = title });
        tabs.SelectedIndex = 0; window.Show(); window.UpdateLayout(); Pump(500);
        try
        {
            var plate = (TabSelectionSurface)tabs.Template.FindName("TabSelectionPlate", tabs);
            // Flush the queued selection update without a wall-clock timer that can
            // overshoot the complete 220 ms animation under a busy test runner.
            tabs.SelectedIndex = 2;
            tabs.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            Assert.True(plate.HasAnimatedProperties);
            var stateProperty = (DependencyProperty)typeof(TabSelectionPlateBehavior)
                .GetField("StateProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var state = tabs.GetValue(stateProperty);
            var clockField = state.GetType().GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var firstClock = (System.Windows.Media.Animation.AnimationClock)clockField.GetValue(state)!;
            firstClock.Controller!.Pause();
            Until(() => firstClock.IsPaused);
            firstClock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(70), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
            var visible = plate.Bounds;
            tabs.SelectedIndex = 1;
            tabs.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            var replacement = (System.Windows.Media.Animation.AnimationClock)clockField.GetValue(state)!;
            Assert.NotSame(firstClock, replacement);
            Assert.Equal(visible, ((System.Windows.Media.Animation.RectAnimation)replacement.Timeline).From);
            MotionPolicy.Configure(MotionPreference.Off, false);
            var selected = (TabItem)tabs.SelectedItem;
            Assert.Equal(new Rect(selected.TranslatePoint(new Point(), plate), selected.RenderSize), plate.Bounds);
            Assert.False(plate.HasAnimatedProperties);
        }
        finally { WindowContentEnterHelper.AllowImmediateClose(window); window.Close(); Pump(30); }
        Assert.False(RenderTransitionClock.IsAnyRunning); Assert.Equal(0, MotionCacheBudget.ReservedPixels);
    });

    [Fact]
    public void Backdrop_excludes_its_own_overlay_inside_a_chrome_wrapper() => Sta(() =>
    {
        var overlay = new Border { Background = Brushes.Red };
        Panel.SetZIndex(overlay, 300);
        var page = new Grid { Background = Brushes.Blue, Children = { overlay } };
        var wrapper = new Grid { Children = { page } };
        var window = new Window { Content = wrapper, Width = 600, Height = 450, ShowActivated = false };
        window.Show(); window.UpdateLayout();
        BitmapSource? image = null; var complete = false;
        try
        {
            using var request = OverlayBackdropCapture.Request(window, result => { image = result; complete = true; });
            Until(() => complete);
            if (!SystemParameters.HighContrast)
            {
                Assert.NotNull(image);
                var pixel = new byte[4];
                image.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), pixel, 4, 0);
                Assert.InRange(pixel[0], 230, 255); Assert.InRange(pixel[2], 0, 25);
            }
            Assert.Equal(Visibility.Visible, overlay.Visibility);
        }
        finally { window.Close(); Pump(40); }
        Assert.Equal(0, MotionCacheBudget.ReservedPixels);
    });


    [Fact]
    public void Live_reduced_hides_surface_lighting_even_when_a_hover_clock_settles_to_one() => Sta(() =>
    {
        var host = new Grid { Background = Brushes.White };
        var window = new Window { Content = host, Width = 500, Height = 350, ShowActivated = false };
        window.Show(); window.UpdateLayout();
        try
        {
            var tracks = new List<NativeMotionTrack>();
            SurfaceLight.AddTracks(host, tracks, true);
            SurfaceMotion.AnimateTracks(host, MotionChannel.Interaction, tracks, 160, UiMotion.OutCubic);
            var light = host.Children.OfType<FrameworkElement>().Single();
            MotionPolicy.Configure(MotionPreference.Reduced, false);
            Assert.Equal(Visibility.Hidden, light.Visibility);
            Assert.False(RenderTransitionClock.IsAnyRunning);
        }
        finally { window.Close(); Pump(40); }
    });


    [Fact]
    public void Active_backdrop_lease_refreshes_when_the_owner_theme_changes() => Sta(() =>
    {
        var page = new Grid();
        page.SetResourceReference(Panel.BackgroundProperty, "WindowBackground");
        var window = new Window { Content = page, Width = 700, Height = 450, ShowActivated = false };
        window.Resources["WindowBackground"] = Brushes.Blue;
        window.Show(); window.UpdateLayout();
        BitmapSource? first = null, latest = null; var calls = 0;
        try
        {
            using var request = OverlayBackdropCapture.Request(window, result => { latest = result; calls++; });
            Until(() => calls == 1); first = latest;
            window.Resources["WindowBackground"] = Brushes.Green;
            var changed = typeof(ThemeManager).GetField("ThemeChanged", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as EventHandler;
            changed?.Invoke(null, EventArgs.Empty);
            Until(() => calls == 2);
            if (!SystemParameters.HighContrast) { Assert.NotNull(latest); Assert.NotSame(first, latest); }
        }
        finally { window.Close(); Pump(40); }
        Assert.Equal(0, MotionCacheBudget.ReservedPixels);
    });

    private static BitmapImage ImageFixture()
    {
        var bitmap = BitmapSource.Create(240, 160, 96, 96, PixelFormats.Bgra32, null, new byte[240 * 160 * 4], 240 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new System.IO.MemoryStream(); encoder.Save(stream); stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    [Fact]
    public void Image_viewer_arrives_from_its_thumbnail_and_live_off_settles_without_leaks() => Sta(() =>
    {
        var thumbnail = new Image { Source = ImageFixture(), Width = 120, Height = 80, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20) };
        var owner = new Window { Content = new Grid { Children = { thumbnail } }, Width = 900, Height = 650, ShowActivated = false };
        owner.Show(); owner.UpdateLayout(); Pump(30);
        var viewer = new Scriblucent.Views.FlashcardImageViewerWindow((BitmapImage)thumbnail.Source, thumbnail) { Owner = owner, ShowActivated = false };
        Resources(viewer);
        try
        {
            viewer.Show(); viewer.UpdateLayout(); Pump(20);
            var image = (Image)viewer.FindName("ViewerImage");
            var (scale, move) = UiMotion.EnsureScaleTranslateGroup(image);
            Assert.True(scale.HasAnimatedProperties, "Initial sizing canceled the image's thumbnail entrance.");
            Assert.InRange(scale.ScaleX, 0.01, 0.95);
            Assert.NotEqual(0, move.X);
            MotionPolicy.Configure(MotionPreference.Off, false);
            Assert.Equal(1, scale.ScaleX); Assert.Equal(1, scale.ScaleY);
            Assert.Equal(0, move.X); Assert.Equal(0, move.Y);
            viewer.Close(); Pump(40);
        }
        finally { MotionPolicy.Configure(MotionPreference.Off, false); viewer.Close(); owner.Close(); Pump(40); }
        Assert.False(RenderTransitionClock.IsAnyRunning); Assert.Equal(0, MotionCacheBudget.ReservedPixels);
    });

    [Fact]
    public void Recycled_or_hidden_image_thumbnail_cannot_become_a_return_destination() => Sta(() =>
    {
        var thumbnail = new Image { Source = ImageFixture(), Width = 120, Height = 80, DataContext = new object() };
        var owner = new Window { Content = thumbnail, Width = 900, Height = 650, ShowActivated = false };
        owner.Show(); owner.UpdateLayout(); Pump(30);
        var viewer = new Scriblucent.Views.FlashcardImageViewerWindow((BitmapImage)thumbnail.Source, thumbnail) { Owner = owner, ShowActivated = false };
        Resources(viewer);
        try
        {
            viewer.Show(); viewer.UpdateLayout(); Pump(40);
            var bounds = typeof(Scriblucent.Views.FlashcardImageViewerWindow).GetMethod("ThumbnailBounds", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.NotNull(bounds.Invoke(viewer, null));
            var original = thumbnail.DataContext; thumbnail.DataContext = new object();
            Assert.Null(bounds.Invoke(viewer, null));
            thumbnail.DataContext = original; thumbnail.Visibility = Visibility.Hidden;
            Assert.Null(bounds.Invoke(viewer, null));
        }
        finally { MotionPolicy.Configure(MotionPreference.Off, false); viewer.Close(); owner.Close(); Pump(40); }
        Assert.False(RenderTransitionClock.IsAnyRunning); Assert.Equal(0, MotionCacheBudget.ReservedPixels);
    });

}
