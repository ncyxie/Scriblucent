using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeSmoothCaret()
    {
        // A native clock begins on its next composition tick. Keep this finite fixture's
        // dispatcher pump active while observing clocks; this is not a production timer.
        _wake16 = true;
        SmoothCaretBehavior.Register();
        var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme);
            foreach (var mode in Enum.GetValues<MotionPreference>())
            {
                MotionPolicy.Configure(mode, false);
                var normal = new TextBox { Text = "Pleasant writing", FontSize = 18 };
                var embedded = new TextBox { Text = "An editor title", Style = (Style)_app.FindResource("EmbeddedFieldTextBoxStyle"), Height = 36 };
                var multiline = new TextBox { Text = "A reminder description", AcceptsReturn = true, Height = 90 };
                var combo = new ComboBox { IsEditable = true, Style = (Style)_app.FindResource("EditableComboBoxStyle"), Text = "Editable choice", ItemsSource = new[] { "Editable choice", "Another choice" } };
                var date = new DatePicker { SelectedDate = DateTime.Today };
                var stack = new StackPanel { Margin = new Thickness(24) };
                foreach (var field in new FrameworkElement[] { normal, embedded, multiline, combo, date })
                { field.Margin = new Thickness(0, 8, 0, 8); stack.Children.Add(field); }
                var window = new Window { Width = 720, Height = 520, Content = new AdornerDecorator { Child = stack }, ShowInTaskbar = false };
                window.SetResourceReference(Control.BackgroundProperty, "WindowBackground");
                window.Show(); window.Activate(); Pump(100); window.UpdateLayout();
                try
                {
                    combo.ApplyTemplate(); date.ApplyTemplate();
                    var comboText = (TextBox)combo.Template.FindName("PART_EditableTextBox", combo);
                    var dateText = (DatePickerTextBox)date.Template.FindName("PART_TextBox", date);
                    foreach (var field in new TextBox[] { normal, embedded, multiline, comboText, dateText })
                    {
                        CaretFocus(field); field.CaretIndex = field.Text.Length; CaretDrain();
                        CaretType(field, "a"); CaretDrain();
                        var state = SmoothCaretBehavior.Inspect(field);
                        Require(state.Tracking, "App-wide caret registration missed " + field.GetType().Name);
                        if (mode == MotionPreference.Automatic)
                        {
                            Require(state.Moving, $"Caret did not glide in {field.GetType().Name}: {state}");
                            var native = field.GetRectFromCharacterIndex(field.CaretIndex);
                            Require(Math.Abs(state.Target.X - native.X) < .01 && Math.Abs(state.Target.Y - native.Y) < .01, "Caret target differs from native geometry.");
                            PauseCaretForCapture(field, 30);
                            SaveControlCapture(window, $"fields-{theme}-{field.Name}-{field.GetType().Name}-{stack.Children.IndexOf(field)}");
                        }
                        else Require(!state.NativeHidden && !state.Moving, "Reduced/Off replaced the native caret.");
                        field.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                        Require(!SmoothCaretBehavior.Inspect(field).NativeHidden, "Wheel scrolling retained the visual caret.");
                    }
                    if (mode == MotionPreference.Automatic) ProbeNativeCaretInput(normal);
                }
                finally { window.Close(); Pump(35); }
                checks.Add($"PASS {theme}/{mode}: native, embedded, multiline, editable-dropdown and date-picker fields; exact caret target; native wheel fallback; focus/window cleanup.");
            }
            ProbeCompiledEditorCarets(theme, settings, checks);
        }
        File.WriteAllLines(Path.Combine(_output, "smooth-caret-checks.txt"), checks);
        Console.WriteLine(string.Join(Environment.NewLine, checks));
    }

    private static void ProbeCompiledEditorCarets(string theme, AppSettings settings, List<string> checks)
    {
        MotionPolicy.Configure(MotionPreference.Off, false);
        var store = Path.Combine(_output, "stores", theme); Directory.CreateDirectory(store);
        var vm = new MainViewModel(settings, false, false) { PersistenceDirectoryOverride = store };
        var dashboard = new Window { DataContext = vm, Width = 700, Height = 500, ShowInTaskbar = false };
        _app.MainWindow = dashboard; dashboard.Show();
        var service = new EditorWorkspaceService(dashboard, vm);
        var note = vm.AddNoteFromDocument(new NoteDocument { Title = "Writing notes", Content = string.Join("\n", Enumerable.Range(1, 80).Select(i => $"Line {i}: an idea worth keeping.")) });
        service.OpenNote(note);
        service.OpenFlashcards(new FlashcardSetDocument { Title = "Review cards", Cards = [new FlashcardItem { Question = "Recall", Answer = "Practice" }] });
        service.OpenQuiz(new QuizDocument { Title = "Knowledge check", Questions = [new QuizQuestion { Question = "A question", Type = QuizQuestionType.SingleChoice,
            Options = [new QuizOption { Text = "First", IsCorrect = true }, new QuizOption { Text = "Second" }] }] });
        service.OpenMindMap(new MindMapDocument { Title = "Connections", Root = new MindMapNode { Text = "Central idea" } });
        var workspace = service.Window!; workspace.ShowInTaskbar = false; Pump(200);
        try
        {
            MotionPolicy.Configure(MotionPreference.Automatic, false);
            foreach (var session in workspace.Sessions.ToArray())
            {
                workspace.FocusDocument(session.Key); Pump(300);
                if (session.Editor is QuizPreviewWindow quiz) { quiz.IsEditMode = true; Pump(300); }
                var title = (TextBox)session.Content.FindName("TitleTextBox");
                CaretFocus(title); title.CaretIndex = Math.Min(2, title.Text.Length); CaretDrain();
                CaretType(title, "x"); CaretDrain(); Pump(250); // Let dirty-status layout settle before the next character.
                CaretType(title, "y"); CaretDrain();
                Require(SmoothCaretBehavior.Inspect(title).Moving, $"Compiled editor title caret did not glide: {session.Key.Kind}, {SmoothCaretBehavior.Inspect(title)}");
                PauseCaretForCapture(title, 35);
                SaveControlCapture(workspace, $"editor-title-{session.Key.Kind}-{theme}");
                title.SelectAll(); Require(!SmoothCaretBehavior.Inspect(title).NativeHidden, "Selection retained an animated title caret.");
            }
            var noteSession = workspace.Sessions.Single(s => s.Key.Kind == EditorDocumentKind.Note);
            workspace.FocusDocument(noteSession.Key); Pump(300);
            var rich = (RichTextBox)noteSession.Content.FindName("ContentTextBox");
            var controller = (NoteEditorWindow)noteSession.Editor;
            foreach (var zoom in new[] { 1d, 1.25, 1.5, 2d })
            {
                typeof(NoteEditorWindow).GetField("_zoomLevel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, zoom);
                typeof(NoteEditorWindow).GetMethod("ApplyZoom", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null);
                Pump(120); CaretFocus(rich);
                // The previous case deliberately scrolls the document. A glide case needs
                // measured, visible input geometry; scrolling/invalid layout stays native.
                var editorViewport = ControlTree(rich).OfType<ScrollViewer>().First();
                SmoothScrollBehavior.Interrupt(editorViewport);
                rich.CaretPosition = rich.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetPositionAtOffset(6)!;
                editorViewport.ScrollToHorizontalOffset(0);
                editorViewport.ScrollToVerticalOffset(0);
                CaretDrain(); Pump(400);
                rich.UpdateLayout();
                var beforeInput = rich.CaretPosition.GetCharacterRect(rich.CaretPosition.LogicalDirection);
                var inputViewport = ControlTree(rich).OfType<ScrollContentPresenter>().First();
                var inputPoint = rich.TranslatePoint(new Point(beforeInput.X, beforeInput.Y + beforeInput.Height / 2), inputViewport);
                Require(rich.CaretPosition.HasValidLayout && new Rect(inputViewport.RenderSize).Contains(inputPoint),
                    $"Caret zoom fixture is not ready for a visible glide: zoom={zoom}, point={inputPoint}, viewport={inputViewport.RenderSize}");
                CaretType(rich, "x"); CaretDrain();
                var moving = SmoothCaretBehavior.Inspect(rich);
                Require(moving.Moving, $"Compiled note caret did not glide at zoom {zoom}: {moving}; before input={beforeInput}; viewport point={inputPoint}; viewport={inputViewport.RenderSize}");
                var native = rich.CaretPosition.GetCharacterRect(rich.CaretPosition.LogicalDirection);
                Require(Math.Abs(moving.Target.X - native.X) < .01 && Math.Abs(moving.Target.Y - native.Y) < .01, "Rich caret lost native geometry under zoom.");
                if (zoom == 1) ProbeRapidCaretInput(rich, nativeInput: false);
                PauseCaretForCapture(rich, 35);
                SaveControlCapture(workspace, $"note-caret-{theme}-zoom{zoom * 100:0}");
                rich.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Require(!SmoothCaretBehavior.Inspect(rich).NativeHidden, "Note wheel scrolling kept the animated caret.");
                Pump(450);
            }
            rich.SelectAll();
            typeof(NoteEditorWindow).GetMethod("HoldSelectionForScroll", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null);
            Require(SmoothCaretBehavior.GetIsSuspended(rich) && rich.CaretBrush == Brushes.Transparent, "Parked note selection exposed a caret.");
            typeof(NoteEditorWindow).GetMethod("MaterializeSelectionForAction", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null);
            Require(!SmoothCaretBehavior.GetIsSuspended(rich) && !rich.Selection.IsEmpty, "Parked selection restoration failed.");
            checks.Add($"PASS {theme}: four compiled editor titles; native RichTextBox at 100/125/150/200 percent zoom; rapid interleaved typing; exact local geometry; wheel fallback; large parked selection restored; no autosave/user-store writes.");
        }
        finally
        {
            // Release native rich-text focus before disposing the synthetic window's HWND.
            if (workspace.Sessions.FirstOrDefault(s => s.Content.IsLoaded) is { } active
                && active.Content.FindName("TitleTextBox") is TextBox title)
            { title.Focus(); Keyboard.Focus(title); CaretDrain(); }
            workspace.CloseAfterPreflight(); dashboard.Close(); Pump(250);
        }
        Require(!RenderTransitionClock.IsAnyRunning, "Caret fixture left motion running after close.");
    }

    private static void CaretFocus(TextBoxBase field)
    { Window.GetWindow(field)!.Activate(); field.Focus(); Keyboard.Focus(field); Pump(30); Require(field.IsKeyboardFocused, "Caret fixture did not receive keyboard focus."); }
    private static void CaretType(TextBoxBase field, string text)
        => TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, field, text, TextCompositionAutoComplete.On));
    private static void CaretDrain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private static void ProbeNativeCaretInput(TextBox field)
    {
        CaretFocus(field); field.CaretIndex = field.Text.Length; CaretDrain();
        var before = field.Text; var moves = SmoothCaretBehavior.Inspect(field).Moves;
        var handle = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(field)).Handle;
        Require(PostMessage(handle, 0x0102, (IntPtr)'z', (IntPtr)1), "Native WM_CHAR could not be posted to the fixture window.");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (field.Text == before && elapsed.ElapsedMilliseconds < 1000) Pump(1);
        CaretDrain();
        Require(field.Text == before + "z" && SmoothCaretBehavior.Inspect(field).Moves > moves,
            "Native keyboard character input did not reach the smooth caret.");
        Pump(150);
        ProbeRapidCaretInput(field, nativeInput: true);
        Pump(200);
        Require(!SmoothCaretBehavior.Inspect(field).NativeHidden, $"Rapid native typing did not restore native blinking: {SmoothCaretBehavior.Inspect(field)}");
    }
    private static void ProbeRapidCaretInput(TextBoxBase field, bool nativeInput)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(field)).Handle;
        for (var i = 0; i < 20; i++)
        {
            if (nativeInput)
            {
                var text = (TextBox)field; var before = text.Text;
                Require(PostMessage(handle, 0x0102, (IntPtr)'W', (IntPtr)1), "Rapid WM_CHAR posting failed.");
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                while (text.Text == before && elapsed.ElapsedMilliseconds < 1000) Pump(1);
                Require(text.Text == before + "W", "Rapid native input delayed or lost a character.");
            }
            else CaretType(field, "W");
            CaretDrain();
            var state = SmoothCaretBehavior.Inspect(field);
            Require(state.Moving && state.NativeHidden, $"Rapid caret dropped its glide at letter {i}: {state}");
            // Interleave real input, layout and composition turns; a single queued burst misses this regression.
            if (i < 19) Pump(10);
        }
    }
    private static void PauseCaretForCapture(TextBoxBase field, int milliseconds)
    {
        var caret = AdornerLayer.GetAdornerLayer(field)!.GetAdorners(field)!.Single(a => a.GetType().Name == "CaretAdorner");
        var table = typeof(NativeSurfaceMotion).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        object?[] args = [caret, null]; table.GetType().GetMethod("TryGetValue")!.Invoke(table, args);
        var state = ((System.Collections.IDictionary)args[1]!)[MotionChannel.Interaction]!;
        var tracks = (IReadOnlyList<(NativeMotionTrack, AnimationClock)>)state.GetType().GetField("Tracks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        foreach (var (_, clock) in tracks)
        { clock.Controller!.Pause(); clock.Controller.SeekAlignedToLastTick(TimeSpan.FromMilliseconds(milliseconds), TimeSeekOrigin.BeginTime); }
    }
}
