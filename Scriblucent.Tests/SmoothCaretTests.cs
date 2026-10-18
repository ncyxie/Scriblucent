using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class SmoothCaretTests
{
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var preference = MotionPolicy.Preference;
            try { SmoothCaretBehavior.Register(); MotionPolicy.Configure(MotionPreference.Automatic, false); action(); }
            catch (Exception e) { failure = e; }
            finally { MotionPolicy.Configure(preference, false); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        // Keep native WPF clocks receiving dispatcher turns while observing the
        // same wall-clock deadline; transparent fixture windows can otherwise idle.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Min(16, milliseconds)) };
        timer.Tick += (_, _) => { if (elapsed.ElapsedMilliseconds < milliseconds) return; timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private sealed class Host : IDisposable
    {
        internal Window Window { get; }
        internal Host(TextBoxBase box)
        {
            Window = new Window { Width = 640, Height = 360, ShowInTaskbar = false, Opacity = 0,
                Content = new AdornerDecorator { Child = new Grid { Children = { box } } } };
            foreach (var name in new[] { "Tokens", "LightTheme", "Generic", "Controls/Shared", "Controls/Chrome", "Controls/Buttons" })
                Window.Resources.MergedDictionaries.Add(ControlTestResources.Load(name));
            box.Margin = new Thickness(24); box.VerticalAlignment = VerticalAlignment.Top;
            Window.Show(); Window.Activate(); Window.UpdateLayout(); box.Focus(); Keyboard.Focus(box); Pump(35);
            Assert.True(box.IsKeyboardFocused);
            Assert.True(SmoothCaretBehavior.Inspect(box).Tracking);
        }
        public void Dispose() { Window.Close(); Pump(25); }
    }
    private static TextBoxBase Field(bool rich)
    {
        if (!rich) return new TextBox { Text = "Writing", FontSize = 18 };
        return new RichTextBox { FontSize = 18, Height = 150,
            Document = new FlowDocument(new Paragraph(new Run("Writing"))) };
    }
    private static void End(TextBoxBase box)
    {
        if (box is TextBox text) text.CaretIndex = text.Text.Length;
        else ((RichTextBox)box).CaretPosition = ((RichTextBox)box).Document.ContentEnd;
        Drain();
    }
    private static string Text(TextBoxBase box) => box is TextBox text ? text.Text
        : new TextRange(((RichTextBox)box).Document.ContentStart, ((RichTextBox)box).Document.ContentEnd).Text.TrimEnd('\r', '\n');
    private static void Type(TextBoxBase box, string text)
        => TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, box, text, TextCompositionAutoComplete.On));

    private static SmoothCaretBehavior.Snapshot ObserveTypedMove(TextBoxBase box, string text)
    {
        int previousMoves = SmoothCaretBehavior.Inspect(box).Moves;
        SmoothCaretBehavior.Snapshot? observed = null;
        var frame = new DispatcherFrame();
        // Sample the production layout update as it completes. Draining all the way to
        // ApplicationIdle can also complete a valid 85 ms glide on a busy dispatcher.
        DispatcherHookEventHandler completed = (_, _) =>
        {
            var state = SmoothCaretBehavior.Inspect(box);
            if (state.Moves <= previousMoves) return;
            observed = state; frame.Continue = false;
        };
        var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(2) };
        timeout.Tick += (_, _) => frame.Continue = false;
        box.Dispatcher.Hooks.OperationCompleted += completed;
        try
        {
            timeout.Start(); Type(box, text);
            if (observed is null) Dispatcher.PushFrame(frame);
            if (observed is not { } started)
                throw new Xunit.Sdk.XunitException($"Native input did not start a caret move: {SmoothCaretBehavior.Inspect(box)}");
            return started;
        }
        finally { timeout.Stop(); box.Dispatcher.Hooks.OperationCompleted -= completed; }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Native_input_moves_only_the_visual_caret_and_returns_to_native_blink(bool rich) => Sta(() =>
    {
        var box = Field(rich); using var host = new Host(box); End(box);
        Type(box, "x"); Drain();
        Assert.Equal("Writingx", Text(box));
        var moving = SmoothCaretBehavior.Inspect(box);
        Assert.True(moving.Moving, moving.ToString()); Assert.True(moving.NativeHidden); Assert.Equal(1, moving.Moves);
        Assert.True(moving.Target.X > moving.Visible.X);
        Pump(200);
        var settled = SmoothCaretBehavior.Inspect(box);
        Assert.False(settled.Moving); Assert.False(settled.NativeHidden);
        Assert.False(DependencyPropertyHelper.GetValueSource(box, TextBoxBase.CaretBrushProperty).IsAnimated);
        Assert.True(box.CanUndo); box.Undo();
        Assert.Equal("Writing", Text(box));
    });

    [Fact]
    public void Rapid_input_retargets_one_caret_without_delaying_text_or_leaving_a_clock() => Sta(() =>
    {
        var box = new TextBox { Text = "Start", FontSize = 18 }; using var host = new Host(box); End(box);
        Type(box, "a"); Drain();
        Assert.True(SmoothCaretBehavior.Inspect(box).Moving);
        var before = SmoothCaretBehavior.Inspect(box).Visible;
        Type(box, "b"); Drain();
        var retarget = SmoothCaretBehavior.Inspect(box);
        Assert.True(retarget.Moving); Assert.True(retarget.Target.X > before.X);
        for (var i = 0; i < 30; i++) Type(box, "c");
        Assert.Equal("Startab" + new string('c', 30), box.Text);
        Drain(); Pump(200);
        Assert.False(SmoothCaretBehavior.Inspect(box).NativeHidden);
        Assert.False(SmoothCaretBehavior.Inspect(box).Moving);
        Assert.True(SmoothCaretBehavior.Inspect(box).Moves <= 3);
    });

    [Theory]
    [InlineData(MotionPreference.Reduced)] [InlineData(MotionPreference.Off)]
    public void Reduced_and_off_use_native_caret_and_runtime_policy_change_cancels_motion(MotionPreference preference) => Sta(() =>
    {
        var box = new TextBox { Text = "Text" }; using var host = new Host(box); End(box);
        Type(box, "a"); Drain(); Assert.True(SmoothCaretBehavior.Inspect(box).Moving);
        MotionPolicy.Configure(preference, false);
        Assert.False(SmoothCaretBehavior.Inspect(box).NativeHidden);
        Type(box, "b"); Drain();
        Assert.Equal("Textab", box.Text); Assert.False(SmoothCaretBehavior.Inspect(box).Moving);
    });

    [Fact]
    public void Pending_input_keeps_the_previous_glide_running_until_native_layout_is_ready() => Sta(() =>
    {
        var box = new TextBox { Text = "Start", FontSize = 18 }; using var host = new Host(box); End(box);
        var started = ObserveTypedMove(box, "a");
        Assert.True(started.Moving);
        // Rich/native input can wait for a later dispatcher turn. Preview must not freeze
        // the already visible caret while waiting for that edit and its layout.
        box.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice,
            new TextComposition(InputManager.Current, box, "b", TextCompositionAutoComplete.On))
            { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
        Assert.True(SmoothCaretBehavior.Inspect(box).Moving);
        Assert.True(SmoothCaretBehavior.Inspect(box).NativeHidden);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Interleaved_rapid_letters_keep_gliding_even_when_the_visual_caret_trails_input(bool rich) => Sta(() =>
    {
        var box = Field(rich); using var host = new Host(box); End(box);
        for (var i = 0; i < 20; i++)
        {
            var state = ObserveTypedMove(box, "W");
            Assert.True(state.Moving, $"Letter {i}: {state}");
            Assert.True(state.NativeHidden);
            Assert.Equal(i + 1, state.Moves);
            Assert.True(state.Visible.X < state.Target.X);
            Pump(10); // Real dispatcher/composition turns between edits, rather than one queued burst.
        }
        Assert.Equal("Writing" + new string('W', 20), Text(box));
        Pump(200);
        Assert.False(SmoothCaretBehavior.Inspect(box).Moving);
        Assert.False(SmoothCaretBehavior.Inspect(box).NativeHidden);
    });

    [Fact]
    public void Selection_replacement_commands_undo_and_programmatic_changes_stay_native() => Sta(() =>
    {
        var box = new TextBox { Text = "Writing" }; using var host = new Host(box); End(box);
        Type(box, "x"); Drain(); Assert.True(SmoothCaretBehavior.Inspect(box).Moving);
        box.Select(0, 3); Assert.False(SmoothCaretBehavior.Inspect(box).NativeHidden);
        Type(box, "Z"); Drain(); Assert.False(SmoothCaretBehavior.Inspect(box).Moving);
        Assert.Equal("Ztingx", box.Text);
        ApplicationCommands.Undo.Execute(null, box); Drain();
        Assert.Equal("Writing", box.Text); Assert.False(SmoothCaretBehavior.Inspect(box).Moving);
        box.Text = "Loaded document"; Drain(); Assert.False(SmoothCaretBehavior.Inspect(box).Moving);
        End(box); Type(box, "y"); Drain();
        var command = new RoutedCommand();
        box.CommandBindings.Add(new CommandBinding(command, (_, args) => args.Handled = true));
        command.Execute(null, box);
        Assert.False(SmoothCaretBehavior.Inspect(box).NativeHidden);
    });

    [Fact]
    public void Composition_suspension_read_only_and_unload_restore_native_paint() => Sta(() =>
    {
        var box = new TextBox { Text = "Native" }; using var host = new Host(box); End(box);
        Type(box, "x"); Drain(); Assert.True(SmoothCaretBehavior.Inspect(box).Moving);
        var composition = new TextComposition(InputManager.Current, box, "", TextCompositionAutoComplete.Off);
        box.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, composition)
            { RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent });
        Assert.False(SmoothCaretBehavior.Inspect(box).NativeHidden);
        box.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, composition)
            { RoutedEvent = TextCompositionManager.PreviewTextInputEvent });
        SmoothCaretBehavior.SetIsSuspended(box, true); Type(box, "y"); Drain();
        Assert.False(SmoothCaretBehavior.Inspect(box).Moving);
        SmoothCaretBehavior.SetIsSuspended(box, false); Type(box, "z"); Drain();
        Assert.True(SmoothCaretBehavior.Inspect(box).Moving);
        box.IsReadOnly = true; Assert.False(SmoothCaretBehavior.Inspect(box).NativeHidden);
        box.IsReadOnly = false; End(box); Type(box, "k"); Drain();
        host.Window.Content = null; Drain();
        Assert.False(SmoothCaretBehavior.Inspect(box).Tracking);
        Assert.False(DependencyPropertyHelper.GetValueSource(box, TextBoxBase.CaretBrushProperty).IsAnimated);
    });

    [Fact]
    public void Caret_binding_survives_motion_and_uses_the_latest_theme_color() => Sta(() =>
    {
        var color = new Border { Background = Brushes.DarkBlue };
        var box = new TextBox { Text = "Bound" };
        box.SetBinding(TextBoxBase.CaretBrushProperty, new Binding(nameof(Border.Background)) { Source = color });
        using var host = new Host(box); End(box); Type(box, "x"); Drain();
        Assert.True(SmoothCaretBehavior.Inspect(box).NativeHidden);
        color.Background = Brushes.DarkRed; Pump(200);
        Assert.True(BindingOperations.IsDataBound(box, TextBoxBase.CaretBrushProperty));
        Assert.Same(Brushes.DarkRed, box.CaretBrush);
    });

    [Fact]
    public void Newlines_and_scrolling_snap_and_unicode_remains_native() => Sta(() =>
    {
        var box = new TextBox { Text = "Line", AcceptsReturn = true, Height = 120, FontSize = 18 };
        box.PreviewMouseWheel += (_, args) => args.Handled = true;
        using var host = new Host(box); End(box);
        Type(box, "\u0105"); Drain(); Assert.Equal("Line\u0105", box.Text);
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Assert.False(SmoothCaretBehavior.Inspect(box).NativeHidden);
        box.AppendText("\nNext"); End(box); Type(box, "x"); Drain();
        Assert.True(SmoothCaretBehavior.Inspect(box).Moving);
        box.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        Assert.False(SmoothCaretBehavior.Inspect(box).NativeHidden);
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Backspace_and_character_navigation_keep_native_commands_and_glide_backwards(bool rich) => Sta(() =>
    {
        var box = Field(rich); using var host = new Host(box); End(box);
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, Key.Back)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        EditingCommands.Backspace.Execute(null, box); Drain();
        Assert.Equal("Writin", Text(box)); Assert.True(SmoothCaretBehavior.Inspect(box).Moving);
        Pump(150);
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, Key.Left)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        EditingCommands.MoveLeftByCharacter.Execute(null, box); Drain();
        Assert.True(SmoothCaretBehavior.Inspect(box).Moving);
        Assert.True(SmoothCaretBehavior.Inspect(box).Target.X < SmoothCaretBehavior.Inspect(box).Visible.X);
        Assert.Equal("Writin", Text(box));
    });
}
