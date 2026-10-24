using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Media;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.Views;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static void ProbePaintingWorkspace()
    {
        var foreground = GetForegroundWindow(); GetCursorPos(out var pointer);
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "preferences");
        var report = new List<string>();
        try
        {
            foreach (var theme in new[] { "Light", "Dark" })
            foreach (var scenario in new[]
            {
                (Language: "en", Mode: MotionPreference.Full, Size: new Size(1050, 820)),
                (Language: "lt", Mode: MotionPreference.Full, Size: new Size(740, 620)),
                (Language: "en", Mode: MotionPreference.Reduced, Size: new Size(820, 700)),
                (Language: "lt", Mode: MotionPreference.Off, Size: new Size(1050, 820))
            })
            {
                LoadNativeControlResources(theme); ThemeManager.SetTheme(theme);
                LocalizationService.SetCulture(scenario.Language); MotionPolicy.Configure(scenario.Mode, false);
                var owner = new Window { Width = scenario.Size.Width, Height = scenario.Size.Height, Content = new Grid(), ShowInTaskbar = false };
                _app.MainWindow = owner; owner.Show(); Pump(40);
                var dialog = new SketchDialog { Owner = owner }; dialog.Show(); dialog.Activate(); Pump(450);
                T Field<T>(string name) => (T)typeof(SketchDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                void Call(string name, params object[] args) => typeof(SketchDialog).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, args);
                var drawing = Field<SketchDocument>("_drawing"); var history = Field<SketchHistory>("_history");
                var shell = (OverlayDialogShell)dialog.Content;
                var card = ControlTree(shell).OfType<Border>().Single(b => b.Name == "DialogCard");
                var ink = (InkCanvas)dialog.FindName("SketchCanvas");
                var cardPosition = card.TranslatePoint(new Point(), shell);
                var viewport = (Border)dialog.FindName("CanvasViewport");
                var footer = (FrameworkElement)dialog.FindName("SketchFooter");
                Require(card.ActualHeight <= dialog.ActualHeight - 47, "Paint card exceeds the owner height.");
                Require(viewport.ActualHeight >= 100, "Drawing viewport lost too much vertical space.");
                Require(viewport.ActualHeight <= LayoutInformation.GetLayoutSlot(viewport).Height + .8,
                    "The drawing viewport exceeds its actual body slot and clips the bottom corners.");
                Require(footer.TransformToAncestor(shell).TransformBounds(new Rect(footer.RenderSize)).Bottom <= shell.ActualHeight - 23, "Paint footer is clipped.");
                var scroll = (ScrollViewer)dialog.FindName("CanvasScroll");
                Require(scroll.ScrollableWidth <= 1 && scroll.ScrollableHeight <= 1, "Fit unnecessarily requires scrolling.");
                var paper = (FrameworkElement)dialog.FindName("Paper");
                var paperBounds = paper.TransformToAncestor(viewport).TransformBounds(new Rect(paper.RenderSize));
                Require(paperBounds.Left >= 8 && paperBounds.Top >= 8 && paperBounds.Right <= viewport.ActualWidth - 8 && paperBounds.Bottom <= viewport.ActualHeight - 8,
                    "Fit clips the paper corners or removes its viewport gutter.");
                Require(!((Button)dialog.FindName("AcceptButton")).IsEnabled, "Empty new sketches can be inserted.");
                Call("SelectTool", SketchTool.Marker);
                Require(ink.DefaultDrawingAttributes.IsHighlighter && ink.DefaultDrawingAttributes.Color != Colors.Black,
                    "Highlighter does not have its own usable color.");
                Call("SelectTool", SketchTool.Pen);
                if (scenario.Language == "en" && scenario.Mode == MotionPreference.Full)
                {
                    SaveControlCapture(shell, $"paint-open-{theme}");
                    var a = ink.PointToScreen(new Point(50, 70)); SetCursorPos((int)a.X, (int)a.Y); Pump(35);
                    Require(((System.Windows.Shapes.Ellipse)dialog.FindName("CursorOutline")).Visibility == Visibility.Visible,
                        "The brush footprint is missing over the drawing canvas.");
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); Pump(25);
                    for (var i = 1; i <= 10; i++)
                    {
                        var p = ink.PointToScreen(new Point(50 + i * 10, 70 + Math.Sin(i * .4) * 15));
                        SetCursorPos((int)p.X, (int)p.Y); Pump(12);
                    }
                    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); Pump(100);
                    Require(drawing.Strokes.Count > 0 && history.CanUndo, "Native freehand input failed to create an undoable stroke.");
                    history.Undo(); Require(drawing.Strokes.Count == 0, "One native freehand gesture was not one undo action."); history.Redo();
                    var originalInk = drawing.Strokes.ToArray();
                    Call("SelectTool", SketchTool.Eraser);
                    Drag(new Point(105, 45), new Point(105, 105));
                    Require(!drawing.Strokes.SequenceEqual(originalInk), "Native point eraser did not change the stroke.");
                    history.Undo(); Require(drawing.Strokes.SequenceEqual(originalInk), "Point erasing was not undone as one gesture.");
                    Call("SelectTool", SketchTool.Rectangle);
                    Drag(new Point(180, 90), new Point(320, 165));
                    Require(drawing.Strokes.Count == originalInk.Length + 1, "Native rectangle drag did not commit one shape.");
                    history.Undo(); Require(drawing.Strokes.SequenceEqual(originalInk), "Native shape gesture could not be undone.");
                    Call("SelectTool", SketchTool.Pen);

                    void Drag(Point start, Point end)
                    {
                        var first = ink.PointToScreen(start); SetCursorPos((int)first.X, (int)first.Y); Pump(30);
                        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); Pump(20);
                        for (var i = 1; i <= 8; i++)
                        {
                            var point = ink.PointToScreen(start + (end - start) * (i / 8d));
                            SetCursorPos((int)point.X, (int)point.Y); Pump(12);
                        }
                        if (ink.EditingMode == InkCanvasEditingMode.None)
                            Require(typeof(SketchInkPreview).GetField("_strokes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog.FindName("ShapePreview")) is StrokeCollection { Count: > 0 },
                                "Shape drag has no live preview before release.");
                        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); Pump(80);
                    }
                }
                foreach (var tool in new[] { SketchTool.Line, SketchTool.Arrow, SketchTool.Rectangle, SketchTool.Ellipse })
                {
                    Call("SelectTool", tool);
                    Require(ink.Cursor == System.Windows.Input.Cursors.Cross, $"{tool} has no visible shape cursor.");
                    ink.Cursor = System.Windows.Input.Cursors.Hand;
                    Call("ResetPan");
                    Require(ink.Cursor == System.Windows.Input.Cursors.Cross, $"Panning loses the {tool} cursor.");
                    history.Begin(); drawing.Strokes.Add(SketchShapes.Create(tool,
                        new Point(190, 50 + (int)tool * 35), new Point(450, 95 + (int)tool * 35), new DrawingAttributes { Color = Colors.SlateBlue, Width = 3, Height = 3 }, false)); history.Commit();
                }
                Call("SelectTool", SketchTool.Pen);
                Require(ink.Cursor == System.Windows.Input.Cursors.None, "Brush footprint has a duplicate system cursor.");
                Call("Help_Click", dialog, new RoutedEventArgs()); Pump(100);
                var help = Field<Popup>("_help");
                Require(help.IsOpen && ControlTree(help.Child).OfType<TextBlock>().Any(t => t.Text.Contains("Shift")),
                    "Drawing tips are not available from the help button.");
                var helpBounds = help.Child.PointToScreen(new Point());
                var helpButton = (Button)dialog.FindName("HelpButton");
                Require(helpBounds.Y < helpButton.PointToScreen(new Point()).Y, "Drawing tips did not open above the footer.");
                if (scenario.Mode == MotionPreference.Full)
                    SaveControlCapture(ControlTree(help.Child).OfType<SurfaceMaterialBorder>().Single(), $"paint-help-{theme}-{scenario.Language}");
                dialog.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(dialog)!, Environment.TickCount, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                Require(!help.IsOpen && dialog.IsVisible, "Escape does not dismiss drawing tips independently.");
                Call("Help_Click", dialog, new RoutedEventArgs()); Pump(50);
                help = Field<Popup>("_help");
                Call("InkColor_Click", dialog, new RoutedEventArgs()); Pump(50);
                Require(!help.IsOpen, "Opening a palette leaves the help popup on top.");
                Field<Popup>("_palette").IsOpen = false;
                Call("UpdateHistoryButtons"); Pump(50);
                Require((card.TranslatePoint(new Point(), shell) - cardPosition).Length < 1, "Inking moves or recenters the dialog card.");
                Require(((Button)dialog.FindName("AcceptButton")).IsEnabled, "Drawn sketches cannot be inserted.");
                var footerBefore = footer.TranslatePoint(new Point(), shell);
                Call("SetZoom", 2d, false); Pump(75);
                Require(scroll.ScrollableWidth > 0, "Canvas zoom does not expose panning space.");
                Require((footer.TranslatePoint(new Point(), shell) - footerBefore).Length < 1, "Zoom moves the footer or tools.");
                Call("Fit_Click", dialog, new RoutedEventArgs()); Pump(50);
                var bytes = SketchPngCodec.Encode(drawing);
                Require(SketchPngCodec.Open(bytes).Strokes.Count == drawing.Strokes.Count, "Paint data cannot be reopened.");
                if (scenario.Mode == MotionPreference.Full) SaveControlCapture(shell, $"paint-{theme}-{scenario.Language}");
                Call("InkColor_Click", dialog, new RoutedEventArgs()); Pump(100);
                var palette = Field<Popup>("_palette");
                Require(palette.IsOpen && ControlTree(palette.Child).OfType<ToggleButton>().Any(b => b.IsChecked == true), "Ink palette lost its selected-color ring.");
                palette.IsOpen = false;
                typeof(SketchDialog).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(dialog, true);
                dialog.Close(); Pump(60);
                var reopened = new SketchDialog(bytes) { Owner = owner }; reopened.Show(); Pump(250);
                Require(!reopened.HasChanges && reopened.SketchPngBytes is null, "Opening an existing sketch marks it changed.");
                typeof(SketchDialog).GetMethod("Insert_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reopened, new object[] { reopened, new RoutedEventArgs() });
                Pump(250);
                Require(!reopened.HasChanges && reopened.SketchPngBytes is null && !reopened.IsVisible,
                    "Unchanged acceptance re-encodes the picture or fails to close.");
                owner.Close(); Pump(80);
                var line = $"{theme}/{scenario.Language}/{scenario.Mode}: fit, pinned controls, history, shapes, zoom, palette, PNG reopening and layout passed ({drawing.Strokes.Count} strokes).";
                report.Add(line); Console.WriteLine(line);
            }
            ProbePaintingEditorBridge();
        }
        finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); SetCursorPos(pointer.X, pointer.Y); if (foreground != IntPtr.Zero) SetForegroundWindow(foreground); }
        File.WriteAllLines(Path.Combine(_output, "checks.txt"), report);
    }

    private static void ProbePaintingEditorBridge()
    {
        using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null, 45000, Timeout.Infinite);
        LoadNativeControlResources("Dark"); ThemeManager.SetTheme("Dark"); LocalizationService.SetCulture("en");
        MotionPolicy.Configure(MotionPreference.Off, false);
        var settings = new AppSettings { Theme = "Dark", EnableAutoSave = false, EnableAiFeatures = false, EnableSpellCheck = false };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        var store = Path.Combine(_output, "editor-store", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(store);
        var vm = new MainViewModel(settings, false, false) { PersistenceDirectoryOverride = store };
        Require(vm.TrySaveNotes(), "Could not create isolated painting store.");
        vm.EnsureEditorStoreLoaded(EditorDocumentKind.Note);
        var ready = System.Diagnostics.Stopwatch.StartNew();
        while (!vm.IsNotesDashboardReady && ready.ElapsedMilliseconds < 5000) Pump(20);
        Require(vm.IsNotesDashboardReady, "Isolated notes store did not become ready.");
        var owner = new Window { DataContext = vm, Width = 1000, Height = 800, ShowInTaskbar = false };
        _app.MainWindow = owner; owner.Show();
        var service = new EditorWorkspaceService(owner, vm);
        service.OpenNote(vm.AddNoteFromDocument(new NoteDocument { Title = "Editable drawing" }));
        var workspace = service.Window!; Pump(250);
        var session = workspace.Sessions.Single();
        var editor = (NoteEditorWindow)session.Editor;
        var coordinator = (FloatingImageCoordinator)typeof(NoteEditorWindow).GetField("_floatingImages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        coordinator.MediaDirectory = Path.Combine(store, "NotesMedia");
        void Paint(Guid? imageId, bool change)
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            timer.Tick += (_, _) =>
            {
                var dialog = _app.Windows.OfType<SketchDialog>().FirstOrDefault(w => w.IsLoaded && w.IsVisible);
                if (dialog is null) return;
                timer.Stop();
                if (change)
                {
                    var drawing = (SketchDocument)typeof(SketchDialog).GetField("_drawing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                    var history = (SketchHistory)typeof(SketchDialog).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                    history.Begin(); drawing.Strokes.Add(SketchShapes.Create(SketchTool.Arrow, new Point(40, 100), new Point(450, 230),
                        new DrawingAttributes { Color = Colors.MediumPurple, Width = 3, Height = 3 }, false)); history.Commit();
                }
                typeof(SketchDialog).GetMethod("Insert_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, new object[] { dialog, new RoutedEventArgs() });
            };
            timer.Start();
            var task = (Task)typeof(NoteEditorWindow).GetMethod("OpenSketchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, new object?[] { imageId })!;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.ElapsedMilliseconds < 5000) Pump(20);
            Require(task.IsCompleted, "Painting did not return to its editor."); task.GetAwaiter().GetResult(); Pump(80);
        }
        try
        {
            Paint(null, true);
            Require(coordinator.BuildAttachments().Count == 1, "New sketch did not enter the note.");
            var imageId = coordinator.BuildAttachments().Single().Id;
            foreach (var mode in new[] { NoteImageWrapMode.None, NoteImageWrapMode.Inline })
            {
                coordinator.SetWrapMode(imageId, mode); Pump(100);
                Require(workspace.SaveSession(session, false), "Could not save the initial drawing.");
                var before = coordinator.BuildAttachments().Single();
                var original = coordinator.GetImageBytes(imageId)!;
                Paint(imageId, false);
                Require(!session.IsDirty && original.SequenceEqual(coordinator.GetImageBytes(imageId)!), "Unchanged painting dirties the note or changes its bytes.");
                Paint(imageId, true);
                Require(session.IsDirty, "Painting an image did not mark the note changed.");
                var after = coordinator.BuildAttachments().Single();
                Require(before.Id == after.Id && before.Width == after.Width && before.Height == after.Height
                    && before.WrapMode == after.WrapMode && before.InlineAnchorOffset == after.InlineAnchorOffset,
                    "Painting changed the note image's frame or wrapping.");
                Require(SketchPngCodec.Open(coordinator.GetImageBytes(imageId)!).Strokes.Count > SketchPngCodec.Open(original).Strokes.Count,
                    "Painting replaced editable data with flattened pixels.");
                Require(workspace.SaveSession(session, false) && !session.IsDirty, "Painted note did not save cleanly.");
            }
            File.AppendAllText(Path.Combine(_output, "editor-checks.txt"), "Native Notes workspace: new sketch, save, floating/inline repaint, unchanged acceptance, geometry/wrap preservation and clean save passed.\n");
            Console.WriteLine("Native editor painting bridge passed.");
            var cancelDrawing = new SketchDialog { Owner = workspace }; cancelDrawing.Show(); Pump(50);
            var cancelDocument = (SketchDocument)typeof(SketchDialog).GetField("_drawing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cancelDrawing)!;
            var cancelHistory = (SketchHistory)typeof(SketchDialog).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cancelDrawing)!;
            cancelHistory.Begin(); cancelDocument.Strokes.Add(SketchShapes.Create(SketchTool.Line, new Point(10, 10), new Point(100, 100), new DrawingAttributes(), false)); cancelHistory.Commit();
            void AnswerClose(string buttonName)
            {
                var answered = false;
                var answer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                answer.Tick += (_, _) =>
                {
                    var message = _app.Windows.OfType<ModernMessageDialog>().FirstOrDefault(w => w.IsVisible);
                    if (message is null) return;
                    answer.Stop(); answered = true;
                    ((Button)message.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                answer.Start(); cancelDrawing.Close();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!answered && clock.ElapsedMilliseconds < 2000) Pump(20);
                Require(answered, "Dirty painting close did not ask before discarding."); Pump(80);
            }
            AnswerClose("CancelButton"); Require(cancelDrawing.IsVisible && cancelDocument.Strokes.Count > 0, "Keeping a drawing lost its content.");
            AnswerClose("PrimaryButton"); Require(!cancelDrawing.IsVisible && cancelDrawing.SketchPngBytes is null, "Discarding a drawing saved it or failed to close.");
            Console.WriteLine("Painting discard/keep and Off-motion close passed.");
        }
        finally { workspace.CloseAfterPreflight(); owner.Close(); Pump(100); }
    }
}
