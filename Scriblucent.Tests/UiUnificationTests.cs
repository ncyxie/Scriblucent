using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using Scriblucent.Animations;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class UiUnificationTests
{
    [Fact]
    public void Native_quiz_review_binds_the_same_result_data_as_the_cached_painter() => Sta(() =>
    {
        var result = new Scriblucent.Views.QuizModeWindow.QuizResultItem { Index = 3, IsCorrect = true,
            HeaderText = "A question", UserAnswerText = "Your answer", CorrectAnswerText = "Correct answer", Explanation = "Why it is correct" };
        var root = new StackPanel { DataContext = result };
        foreach (var name in new[] { "HeaderText", "UserAnswerText", "CorrectAnswerText", "Explanation" })
        {
            var text = new TextBlock(); text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(name)); root.Children.Add(text);
        }
        var state = new CheckBox(); state.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding("IsCorrect") { Mode = System.Windows.Data.BindingMode.OneWay }); root.Children.Add(state);
        using var host = new Host(root);
        Assert.Equal(new[] { "A question", "Your answer", "Correct answer", "Why it is correct" }, root.Children.OfType<TextBlock>().Select(t => t.Text));
        Assert.True(state.IsChecked);
    });

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Editor_color_samples_keep_exact_interior_pixels_during_hover_and_press(string theme) => Sta(() =>
    {
        var sample = new Button { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        using var host = new Host(new Grid { Children = { sample } }, theme);
        sample.Style = (Style)host.Window.FindResource("EditorColorSwatchButtonStyle"); host.Window.UpdateLayout(); Pump();
        var face = (InteractionSurface)sample.Template.FindName("SwatchBorder", sample);
        foreach (var color in new[] { Colors.White, Colors.Black, Color.FromRgb(35, 129, 222) })
        {
            sample.Background = new SolidColorBrush(color);
            foreach (var pressed in new[] { false, true })
            {
                face.AnimateState(true, pressed, false);
                Scriblucent.Behaviors.CommandLightBehavior.PreviewPointer(sample, new Point(16, 16), true);
                Scriblucent.Behaviors.CommandLightBehavior.PreviewPress(sample, sample, pressed); Pump(180);
                var image = new System.Windows.Media.Imaging.RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
                image.Render(face); var pixel = new byte[4];
                image.CopyPixels(new Int32Rect(16, 16, 1, 1), pixel, 4, 0);
                Assert.Equal(new byte[] { color.B, color.G, color.R, 255 }, pixel);
            }
        }
    });

    [Theory]
    [InlineData(MotionPreference.Automatic)] [InlineData(MotionPreference.Reduced)] [InlineData(MotionPreference.Off)]
    public void Disclosure_header_preserves_native_toggle_and_collapses_its_body_gap_after_reversal(MotionPreference mode) => Sta(() =>
    {
        var disclosure = new Expander { Header = "Optional details", Content = new Border { Height = 52 } };
        var following = new Button { Content = "Following action" };
        var root = new StackPanel { Width = 280, VerticalAlignment = VerticalAlignment.Top, Children = { disclosure, following } };
        using var host = new Host(root); MotionPolicy.Configure(mode, false);
        var header = (System.Windows.Controls.Primitives.ToggleButton)disclosure.Template.FindName("HeaderSite", disclosure);
        Assert.IsType<InteractionSurface>(header.Template.FindName("DisclosureHeaderFace", header));
        Assert.NotNull(header.FocusVisualStyle);
        var collapsedHeight = disclosure.ActualHeight;
        var collapsedNext = following.TranslatePoint(new Point(), root);
        var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(header).GetPattern(PatternInterface.Toggle)!;
        toggle.Toggle(); Assert.True(disclosure.IsExpanded);
        for (var i = 0; i < 6; i++) { toggle.Toggle(); Pump(15); }
        Pump(400); host.Window.UpdateLayout();
        Assert.True(disclosure.IsExpanded); Assert.True(header.IsChecked);
        Assert.Equal(collapsedHeight + 64, disclosure.ActualHeight, 1);
        Assert.Equal(default(Thickness), header.BorderThickness);
        toggle.Toggle(); Pump(400); host.Window.UpdateLayout();
        Assert.False(disclosure.IsExpanded); Assert.Equal(collapsedHeight, disclosure.ActualHeight, 1);
        Assert.Equal(collapsedNext, following.TranslatePoint(new Point(), root));
    });

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Off_switch_hover_retains_a_muted_track_and_does_not_change_value(string theme) => Sta(() =>
    {
        var toggle = new CheckBox { Content = "Preference", IsChecked = false, Width = 320 };
        using var host = new Host(new Grid { Children = { toggle } }, theme);
        toggle.Style = (Style)host.Window.FindResource("SettingsSwitchStyle"); host.Window.UpdateLayout(); Pump();
        var key = typeof(UIElement).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.GetValue(null)).OfType<DependencyPropertyKey>()
            .Single(key => key.DependencyProperty == UIElement.IsMouseOverProperty);
        toggle.SetValue(key, true); Pump();
        var track = (Border)toggle.Template.FindName("SwitchTrack", toggle);
        Assert.Same(host.Window.FindResource("SwitchTrackHoverBackground"), track.Background);
        Assert.False(toggle.IsChecked);
        Assert.NotEqual(Colors.White, ((SolidColorBrush)track.Background).Color);
        toggle.SetValue(key, false); Pump();
        Assert.Same(host.Window.FindResource("SliderTrackBackground"), track.Background);
    });

    [Theory]
    [InlineData("Light", "ChromeComboBoxStyle")]
    [InlineData("Dark", "ChromeComboBoxStyle")]
    [InlineData("Light", "InsetComboBoxStyle")]
    [InlineData("Dark", "InsetComboBoxStyle")]
    [InlineData("Light", "SetSelectorComboBoxStyle")]
    [InlineData("Dark", "LinkedNoteComboBoxStyle")]
    public void Readonly_dropdown_has_one_click_target_across_the_face(string theme, string style) => Sta(() =>
    {
        var combo = new ComboBox { Width = 280, ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 };
        using var host = new Host(new Grid { Children = { combo } }, theme);
        combo.Style = (Style)host.Window.FindResource(style);
        host.Window.UpdateLayout(); Pump();
        if (style is "ChromeComboBoxStyle" or "InsetComboBoxStyle")
        {
            var disclosure = (System.Windows.Controls.Primitives.ToggleButton)combo.Template.FindName("DropDownToggle", combo);
            var chevron = (FrameworkElement)disclosure.Template.FindName("Chevron", disclosure);
            foreach (var width in new[] { 160d, 280d, 420d })
            {
                combo.Width = width; host.Window.UpdateLayout();
                var center = chevron.TranslatePoint(new Point(chevron.ActualWidth / 2, chevron.ActualHeight / 2), combo);
                Assert.InRange(center.X, combo.ActualWidth - 16.5, combo.ActualWidth - 15.5);
            }
            combo.Width = 280; host.Window.UpdateLayout();
        }
        foreach (var x in new[] { 12d, 140d, 264d })
        {
            var hit = combo.InputHitTest(new Point(x, combo.ActualHeight / 2)) as DependencyObject;
            while (hit is not null && hit is not System.Windows.Controls.Primitives.ToggleButton)
                hit = VisualTreeHelper.GetParent(hit);
            var toggle = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(hit);
            toggle.IsChecked = true; Pump(); Assert.True(combo.IsDropDownOpen);
            toggle.IsChecked = false; Pump(); Assert.False(combo.IsDropDownOpen);
        }
    });

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Editable_dropdown_preserves_the_text_hit_target(string theme) => Sta(() =>
    {
        var combo = new ComboBox { Width = 280, ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 };
        using var host = new Host(new Grid { Children = { combo } }, theme);
        combo.Style = (Style)host.Window.FindResource("InsetEditableComboBoxStyle");
        host.Window.UpdateLayout(); Pump();
        var hit = combo.InputHitTest(new Point(50, combo.ActualHeight / 2)) as DependencyObject;
        while (hit is not null && hit is not TextBox) hit = VisualTreeHelper.GetParent(hit);
        Assert.Same(combo.Template.FindName("PART_EditableTextBox", combo), hit);
        Assert.False(combo.IsDropDownOpen);
    });

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Quiz_button_faces_inherit_the_shared_material_and_type(string theme) => Sta(() =>
    {
        var button = new Button { Content = "An answer", Width = 280 };
        using var host = new Host(new Grid { Children = { button } }, theme);
        host.Window.Resources.MergedDictionaries.Add(ControlTestResources.Load("Controls/Quiz"));
        foreach (var key in new[] { "QuizOptionButtonBaseStyle", "QuizNavQuestionButtonStyle" })
        {
            button.Style = (Style)host.Window.FindResource(key); host.Window.UpdateLayout(); Pump();
            Assert.Equal(SurfaceMaterialKind.Neutral, SurfaceMaterial.GetKind(button));
            Assert.Same(host.Window.FindResource("IconButtonBackground"), button.Background);
            Assert.NotNull(InteractionSurface.GetHighlightWash(button));
            Assert.NotNull(button.FocusVisualStyle);
        }
    });

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Switch_hit_targets_follow_label_and_track_without_the_empty_row_gap(string theme) => Sta(() =>
    {
        var toggle = new CheckBox { Content = "Preference", Width = 320, IsChecked = false };
        using var host = new Host(new Grid { Children = { toggle } }, theme);
        toggle.Style = (Style)host.Window.FindResource("SettingsSwitchStyle"); host.Window.UpdateLayout(); Pump();
        bool HitsSwitch(Point point)
        {
            var hit = toggle.InputHitTest(point) as DependencyObject;
            while (hit is not null && hit != toggle) hit = VisualTreeHelper.GetParent(hit);
            return hit == toggle;
        }
        Assert.True(HitsSwitch(new Point(12, toggle.ActualHeight / 2)));
        Assert.True(HitsSwitch(new Point(300, toggle.ActualHeight / 2)));
        Assert.False(HitsSwitch(new Point(180, toggle.ActualHeight / 2)));
        Assert.False(HitsSwitch(new Point(300, 2)));
        Assert.False(toggle.IsChecked);
    });

    [Theory]
    [InlineData("Light", "ChromeComboBoxStyle")]
    [InlineData("Dark", "InsetComboBoxStyle")]
    [InlineData("Light", "EditableComboBoxStyle")]
    [InlineData("Dark", "SetSelectorComboBoxStyle")]
    [InlineData("Light", "LinkedNoteComboBoxStyle")]
    public void Dropdown_keeps_its_viewport_while_scrolling_and_remeasures_on_reopen(string theme, string style) => Sta(() =>
    {
        var combo = new ComboBox { Width = 180, MaxDropDownHeight = 150,
            ItemsSource = Enumerable.Range(0, 100).Select(i => i < 40 ? $"Item {i}" : $"A much longer dropdown item at index {i}").ToArray(), SelectedIndex = 0 };
        if (style == "InsetComboBoxStyle")
        {
            combo.ItemsSource = combo.Items.Cast<string>().Select(text => new { Label = text }).ToArray();
            combo.DisplayMemberPath = "Label";
        }
        using var host = new Host(new Grid { Children = { combo } }, theme);
        combo.Style = (Style)host.Window.FindResource(style); host.Window.UpdateLayout();
        combo.IsDropDownOpen = true; Pump(120);
        var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
        var viewer = Visuals(popup.Child).OfType<ScrollViewer>().First();
        var initial = viewer.RenderSize;
        Assert.False(double.IsNaN(viewer.Width)); Assert.False(double.IsNaN(viewer.Height));
        Assert.Null(((UIElement)popup.Child).InputHitTest(new Point(8, 8)));
        viewer.ScrollToEnd(); Pump(120); host.Window.UpdateLayout();
        Assert.True(viewer.VerticalOffset > 0);
        var label = Visuals(popup.Child).OfType<TextBlock>().First(text => text.Text.StartsWith("A much longer"));
        Assert.True(label.ActualHeight > label.FontSize * 1.8, $"Long dropdown text must render on multiple lines, not get cut off: wrap={label.TextWrapping}; size={label.RenderSize}; desired={label.DesiredSize}; viewport={viewer.ViewportWidth}.");
        Assert.True(label.ActualWidth <= viewer.ViewportWidth);
        Assert.Equal(initial.Width, viewer.ActualWidth, 1); Assert.Equal(initial.Height, viewer.ActualHeight, 1);
        combo.IsDropDownOpen = false; Pump(80);
        Assert.True(double.IsNaN(viewer.Width)); Assert.True(double.IsNaN(viewer.Height));
        combo.Width = 300; host.Window.UpdateLayout(); combo.IsDropDownOpen = true; Pump(120);
        Assert.True(viewer.ActualWidth > initial.Width + 60);
        combo.IsDropDownOpen = false; Pump();
    });

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { MotionPolicy.Configure(MotionPreference.Automatic, false); action(); }
            catch (Exception e) { failure = e; }
            finally { MotionPolicy.Configure(MotionPreference.Off, false); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Pump(int ms = 35)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 3000) Pump();
        Assert.True(condition());
    }
    private sealed class Host : IDisposable
    {
        internal Window Window { get; }
        internal Host(FrameworkElement content, string theme = "Light")
        {
            Window = new Window { Width = 640, Height = 720, Content = content, ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
            foreach (var name in new[] { "Tokens", theme + "Theme", "Generic", "Controls/Shared", "Controls/Chrome", "Controls/Buttons" })
                Window.Resources.MergedDictionaries.Add(ControlTestResources.Load(name));
            Window.Show(); Window.UpdateLayout(); Pump();
        }
        public void Dispose() { Window.Close(); Pump(); }
    }
    private static void Key(TableSizePicker picker, Key key)
        => picker.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(picker)!, Environment.TickCount, key)
            { RoutedEvent = Keyboard.KeyDownEvent });

    [Theory]
    [InlineData(MotionPreference.Automatic)] [InlineData(MotionPreference.Reduced)] [InlineData(MotionPreference.Off)]
    public void Picker_rapid_preview_changes_commit_logical_selection_without_layout_or_clock_leaks(MotionPreference mode) => Sta(() =>
    {
        var picker = new TableSizePicker { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var root = new Grid { Children = { picker } };
        using var host = new Host(root); MotionPolicy.Configure(mode, false);
        var size = picker.RenderSize; var commits = new List<(int, int)>(); var dismissals = 0;
        picker.InsertRequested += (_, _) => commits.Add((picker.Rows, picker.Columns));
        picker.DismissRequested += (_, _) => dismissals++;
        picker.Select(3, 3, false);
        for (var i = 0; i < 30; i++) picker.Select(i % 8 + 1, 8 - i % 8);
        picker.Select(4, 6); Key(picker, System.Windows.Input.Key.Right); Key(picker, System.Windows.Input.Key.Up);
        // Commit while the painted preview is still catching up.
        Key(picker, System.Windows.Input.Key.Enter); Assert.Equal((3, 7), Assert.Single(commits));
        picker.Select(-50, 90); Assert.Equal(1, picker.Rows); Assert.Equal(8, picker.Columns);
        var peer = UIElementAutomationPeer.CreatePeerForElement(picker)!;
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke(); Assert.Equal((1, 8), commits.Last());
        Assert.Contains("1", peer.GetName()); Assert.Contains("8", peer.GetName());
        Key(picker, System.Windows.Input.Key.Escape); Assert.Equal(1, dismissals);
        host.Window.UpdateLayout(); Assert.Equal(size, picker.RenderSize);
        if (mode != MotionPreference.Automatic) Assert.False(NativeSurfaceMotion.IsRunning(picker, MotionChannel.Selection));
        root.Children.Remove(picker); Pump(); Assert.False(NativeSurfaceMotion.IsRunning(picker, MotionChannel.Selection));
        root.Children.Add(picker); Pump(); Assert.Equal((1, 8), (picker.Rows, picker.Columns));
    });

    [Theory]
    [InlineData(MotionPreference.Automatic)] [InlineData(MotionPreference.Reduced)] [InlineData(MotionPreference.Off)]
    public void Inline_validation_preserves_one_line_and_recovers_from_wrapping_reversal_and_reload(MotionPreference mode) => Sta(() =>
    {
        var feedback = new InlineValidationPresenter();
        var input = new TextBox { Text = "Still editable" }; var button = new Button { Content = "Still clickable" };
        var form = new StackPanel { Width = 280, VerticalAlignment = VerticalAlignment.Top, Children = { input, feedback, button } };
        using var host = new Host(form); MotionPolicy.Configure(mode, false);
        var idlePosition = button.TranslatePoint(new Point(), form);
        Assert.Equal(18, feedback.ActualHeight, 1);
        feedback.Show("Required."); Pump(190); Assert.Equal(idlePosition, button.TranslatePoint(new Point(), form));
        feedback.Hide(); feedback.Show(string.Join(" ", Enumerable.Repeat("A longer validation message", 12)));
        Pump(50); feedback.Hide(); feedback.Show("Latest correction."); input.Text = "Changed during feedback";
        var clicks = 0; button.Click += (_, _) => clicks++; button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(1, clicks);
        Pump(350); host.Window.UpdateLayout();
        Assert.True(feedback.IsError); Assert.Equal("Latest correction.", ((TextBlock)feedback.Child).Text);
        Assert.Equal(1, ((TextBlock)feedback.Child).Opacity); Assert.True(input.IsEnabled); Assert.True(button.IsHitTestVisible);
        Assert.True((idlePosition - button.TranslatePoint(new Point(), form)).Length < .1);
        feedback.Show(string.Join(" ", Enumerable.Repeat("Wrapping error", 20))); Pump(50);
        form.Width = 180; host.Window.UpdateLayout();
        Until(() => !NativeSurfaceMotion.IsRunning(feedback, MotionChannel.Layout)
            && feedback.ActualHeight >= ((TextBlock)feedback.Child).DesiredSize.Height - .5);
        Assert.True(feedback.ActualHeight >= ((TextBlock)feedback.Child).DesiredSize.Height - .5);
        feedback.Hide();
        Until(() => !NativeSurfaceMotion.IsRunning(feedback, MotionChannel.Presence)
            && !NativeSurfaceMotion.IsRunning(feedback, MotionChannel.Layout));
        host.Window.UpdateLayout(); Assert.Equal(18, feedback.ActualHeight, 1);
        Assert.False(NativeSurfaceMotion.IsRunning(feedback, MotionChannel.Layout));
        feedback.Show("Reloaded."); form.Children.Remove(feedback); Pump(); form.Children.Insert(1, feedback); Pump(350);
        Assert.Equal(1, ((TextBlock)feedback.Child).Opacity); Assert.Equal("Reloaded.", ((TextBlock)feedback.Child).Text);
    });

    [Theory]
    [InlineData(MotionPreference.Automatic)] [InlineData(MotionPreference.Reduced)] [InlineData(MotionPreference.Off)]
    public void Content_swaps_retarget_current_height_without_disabling_other_actions(MotionPreference mode) => Sta(() =>
    {
        var content = new Border { Height = 32, Child = new Button { Content = "Remove" } };
        var reveal = new LayoutRevealHost { Child = content };
        var next = new Button { Content = "Following field", Height = 32 };
        var root = new StackPanel { Width = 280, VerticalAlignment = VerticalAlignment.Top, Children = { reveal, next } };
        using var host = new Host(root); MotionPolicy.Configure(mode, false);
        for (var i = 0; i < 10; i++)
        {
            reveal.ChangeContent(() => content.Height = i % 2 == 0 ? 72 : 32);
            Assert.True(reveal.IsEnabled); Assert.True(reveal.IsHitTestVisible); Assert.True(next.IsEnabled); Pump(15);
        }
        reveal.ChangeContent(() => content.Height = 72);
        Until(() => !NativeSurfaceMotion.IsRunning(reveal, MotionChannel.Layout) && Math.Abs(reveal.ActualHeight - 72) < .05);
        host.Window.UpdateLayout();
        Assert.Equal(72, reveal.ActualHeight, 1); Assert.Equal(72, next.TranslatePoint(new Point(), root).Y, 1);
        Assert.False(NativeSurfaceMotion.IsRunning(reveal, MotionChannel.Layout));
        reveal.ChangeContent(() => content.Height = 32); root.Children.Remove(reveal); Pump();
        root.Children.Insert(0, reveal); Pump(320); Assert.Equal(32, reveal.ActualHeight, 1);
    });

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Long_form_boolean_labels_wrap_and_keep_native_toggle_contracts(string theme) => Sta(() =>
    {
        var box = new CheckBox { Width = 190, HorizontalAlignment = HorizontalAlignment.Left,
            Content = "Egzamino režimas: vienas bandymas ir jokių atsakymo pakeitimų" };
        using var host = new Host(box, theme);
        box.Style = (Style)host.Window.FindResource("FormCheckBoxStyle");
        host.Window.UpdateLayout(); Pump();
        var presenter = (ContentPresenter)((Grid)VisualTreeHelper.GetChild(box, 0)).Children[1];
        var text = (TextBlock)VisualTreeHelper.GetChild(presenter, 0);
        Assert.True(text.ActualHeight > 25);
        var bounds = text.TransformToAncestor(box).TransformBounds(new Rect(text.RenderSize));
        Assert.True(bounds.Right <= box.ActualWidth + .5);
        var changes = 0; box.Checked += (_, _) => changes++;
        ((IToggleProvider)new CheckBoxAutomationPeer(box).GetPattern(PatternInterface.Toggle)!).Toggle();
        Assert.True(box.IsChecked); Assert.Equal(1, changes);
    });

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Dialog_card_fits_resized_owner_and_reserves_its_shadow(string theme) => Sta(() =>
    {
        var shell = new OverlayDialogShell { MaxCardWidth = 540, Header = new TextBlock { Text = "A coherent dialog" },
            Body = new TextBox { Text = "Readable input" }, Footer = new Button { Content = "Confirm" } };
        using var host = new Host(shell, theme);
        var card = (FrameworkElement)shell.Template.FindName("DialogCard", shell);
        Assert.Equal(540, card.ActualWidth);
        foreach (var width in new[] { 420d, 500d, 900d })
        {
            host.Window.Width = width; host.Window.UpdateLayout(); Pump();
            var bounds = card.TransformToAncestor(shell).TransformBounds(new Rect(card.RenderSize));
            Assert.True(bounds.Left >= 23 && bounds.Right <= shell.ActualWidth - 23);
            Assert.True(bounds.Top >= 23 && bounds.Bottom <= shell.ActualHeight - 23);
            Assert.True(card.ActualWidth <= shell.MaxCardWidth);
        }
    });

    [Theory]
    [InlineData("Light")] [InlineData("Dark")]
    public void Form_surfaces_remain_distinct_and_multiline_padding_belongs_only_to_the_shell(string theme) => Sta(() =>
    {
        var text = new TextBox { AcceptsReturn = true, Text = "Editable text" };
        var field = new Border { Child = text, Padding = new Thickness(9, 7, 9, 7) };
        var group = new Border { Child = field };
        using var host = new Host(group, theme);
        group.Style = (Style)host.Window.FindResource("ContentInsetBorderStyle");
        field.Style = (Style)host.Window.FindResource("ChromeFieldBorderStyle");
        text.Style = (Style)host.Window.FindResource("EmbeddedFieldTextBoxStyle");
        host.Window.UpdateLayout(); Pump();
        Assert.NotEqual(((SolidColorBrush)group.Background).Color, ((SolidColorBrush)field.Background).Color);
        Assert.Equal(default(Thickness), text.Padding); Assert.Equal(14, text.FontSize);
        Assert.Null(field.Effect); Assert.Null(group.Effect);
        Assert.Equal(0, group.BorderThickness.Left); Assert.Equal(new CornerRadius(14), group.CornerRadius);
        field.Style = (Style)host.Window.FindResource("ChromeInsetFieldBorderStyle");
        Assert.NotEqual(((SolidColorBrush)host.Window.FindResource("CardBackground")).Color, ((SolidColorBrush)field.Background).Color);
    });
}
