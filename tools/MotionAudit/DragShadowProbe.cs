using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.ViewModels;

internal static partial class Program
{
    private static object? GhostCall(string name, params object?[] args) =>
        typeof(DashboardDragGhost).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    private static object GhostValue(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value)!;
    private static void WriteGhostImage(object value, string file)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create((BitmapSource)GhostValue(value, "Image")));
        using var stream = File.Create(Path.Combine(_output, file)); encoder.Save(stream);
        if (file.EndsWith("-ghost.png"))
        {
            var bitmap = (BitmapSource)GhostValue(value, "Image");
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                var bounds = new Rect(0, 0, bitmap.Width, bitmap.Height);
                context.DrawRectangle((Brush)_app.FindResource("WindowBackground"), null, bounds);
                context.DrawImage(bitmap, bounds);
            }
            var composite = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY, PixelFormats.Pbgra32);
            composite.Render(drawing);
            var preview = new PngBitmapEncoder(); preview.Frames.Add(BitmapFrame.Create(composite));
            using var previewStream = File.Create(Path.Combine(_output, file.Replace("-ghost.png", "-preview.png"))); preview.Save(previewStream);
        }
    }
    private static void ProbeDragShadows()
    {
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadResources(theme);
            // Supply real shell resources without showing the shell or loading personal data.
            var shell = new Scriblucent.MainWindow(new AppSettings { Theme = theme });
            _app.Resources.MergedDictionaries.Add(shell.Resources);
            var window = new Window { Width = 800, Height = 500, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            void CaptureGhost(FrameworkElement fixture, string tag, bool nestedEntrance = false, DpiScale? captureDpi = null)
            {
                if (fixture.Parent is Panel previousParent) previousParent.Children.Remove(fixture);
                var root = new Grid { Children = { fixture } }; root.SetResourceReference(Panel.BackgroundProperty, "WindowBackground"); fixture.HorizontalAlignment = HorizontalAlignment.Center;
                fixture.VerticalAlignment = VerticalAlignment.Center;
                window.Content = root; window.UpdateLayout(); Pump(450);
                fixture.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent }); Pump(220);
                if (nestedEntrance)
                {
                    var entryFace = (FrameworkElement)GhostCall("ResolveCaptureSurface", fixture)!;
                    entryFace.RenderTransform = new TransformGroup { Children =
                    {
                        new TransformGroup { Children = { new ScaleTransform(.88, .88), new TranslateTransform(0, 3) } },
                        new ScaleTransform(1, 1), new TranslateTransform()
                    } };
                }
                GhostCall("ResetRestVisualState", fixture); window.UpdateLayout();
                var label = tag + (nestedEntrance ? "-nested-entry" : "") + (captureDpi is { } requestedDpi ? $"-source-{requestedDpi.DpiScaleX * 100:F0}pct" : "");
                var surface = (FrameworkElement)GhostCall("ResolveCaptureSurface", fixture)!;
                Console.WriteLine($"{theme}/{label}: source {surface.GetType().Name}/{surface.Name}, {surface.ActualWidth}x{surface.ActualHeight}");
                if (surface.Name != tag && tag != "NoteCard") throw new InvalidOperationException("Ghost capture included the outer card and shadow.");
                var transformBeforeCapture = surface.RenderTransform;
                object snapshot;
                if (captureDpi is { } sourceDpi)
                {
                    object?[] args = { surface, surface.ActualWidth, surface.ActualHeight, sourceDpi, null };
                    var result = typeof(DashboardDragGhost).GetMethod("TryPaddedVisualCapture", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
                    if (result is not true) throw new InvalidOperationException("Cross-DPI face capture failed.");
                    snapshot = args[4]!;
                }
                else snapshot = GhostCall("CaptureSnapshot", fixture, surface, surface.ActualWidth, surface.ActualHeight)!;
                if (!ReferenceEquals(transformBeforeCapture, surface.RenderTransform))
                    throw new InvalidOperationException("Capture replaced the live motion transform tree.");
                Console.WriteLine($"  capture inset: {GhostValue(snapshot, "ContentOffsetX")}; live VisualBrush can freeze: {new VisualBrush(surface).CanFreeze}");
                if ((double)GhostValue(snapshot, "ContentOffsetX") != 16)
                    throw new InvalidOperationException("Face capture silently fell back to an offset direct capture.");
                WriteGhostImage(snapshot, $"{theme}-{label}-face.png");
                var ghost = GhostCall("BakeGhostVisual", snapshot, new Point(80, 60), window, surface as Border)!;
                WriteGhostImage(ghost, $"{theme}-{label}-ghost.png");
                var bitmap = (BitmapSource)GhostValue(ghost, "Image");
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                for (var y = 0; y < bitmap.PixelHeight; y++) for (var px = 0; px < bitmap.PixelWidth; px++)
                    if ((y == 0 || px == 0 || y == bitmap.PixelHeight - 1 || px == bitmap.PixelWidth - 1) &&
                        pixels[(y * bitmap.PixelWidth + px) * 4 + 3] != 0)
                        throw new InvalidOperationException("The baked shadow is cut off at its texture edge.");
                var sx = bitmap.DpiX / 96; var sy = bitmap.DpiY / 96;
                var faceBounds = new Rect(40 * sx, 49 * sy, surface.ActualWidth * sx, surface.ActualHeight * sy); faceBounds.Inflate(2 * sx, 2 * sy);
                // A shadow may be correctly bounded while a shrunken capture exposes its solid interior.
                // Sample away from rounded corners and text at both far sides of the opaque face.
                foreach (var point in new[] {
                    new Point((40 + surface.ActualWidth - 20) * sx, (49 + surface.ActualHeight / 2) * sy),
                    new Point((40 + surface.ActualWidth / 2) * sx, (49 + surface.ActualHeight - 20) * sy) })
                {
                    var alpha = pixels[((int)point.Y * bitmap.PixelWidth + (int)point.X) * 4 + 3];
                    if (alpha < 250) throw new InvalidOperationException($"{label}: face fails to cover its shadow (interior alpha {alpha}/255).");
                }
                var peak = 0;
                for (var y = 0; y < bitmap.PixelHeight; y++) for (var px = 0; px < bitmap.PixelWidth; px++)
                    if (!faceBounds.Contains(new Point(px + .5, y + .5))) peak = Math.Max(peak, pixels[(y * bitmap.PixelWidth + px) * 4 + 3]);
                if (peak > 44) throw new InvalidOperationException("Captured content or a heavy backing leaks beyond the face.");
                var profile = DashboardDragShadow.ForFace(surface.ActualWidth, surface.ActualHeight);
                if (profile.BottomReach > 24) throw new InvalidOperationException("Shadow falloff exceeds allocated padding.");
                checks.Add($"{theme}/{label}: capture {((BitmapSource)GhostValue(snapshot, "Image")).DpiX:F0} DPI; owner {bitmap.DpiX:F0} DPI; {surface.ActualWidth:F0}x{surface.ActualHeight:F0}; height {profile.Height:F2}; offset {profile.OffsetY:F2}; sigma {profile.CastSigma:F2}; reach top/side/bottom {profile.TopReach:F2}/{profile.SideReach:F2}/{profile.BottomReach:F2} DIP; outside alpha {peak}/255.");
            }
            var note = new NoteCardControl { Height = 140,
                DataContext = new NoteCardViewModel(new NoteDocument { Title = "Note drag preview", SearchablePreview = "An idea worth keeping." }, _ => { }) };
            CaptureGhost(note, "NoteCard");
            CaptureGhost(note, "NoteCard", captureDpi: new DpiScale(1, 1));
            CaptureGhost(note, "NoteCard", captureDpi: new DpiScale(2, 2));
            foreach (var dashboard in new[] { "Flashcards", "Quizzes", "MindMaps" })
            {
                var source = XDocument.Load($"Scriblucent/Views/Dashboards/{dashboard}DashboardPage.xaml");
                var cards = source.Descendants().Where(e => e.Name.LocalName == "Border" &&
                    e.Attributes().Any(a => a.Name.LocalName == "CardSurfaceMotionBehavior.ShadowElementName")).ToArray();
                foreach (var card in cards)
                {
                    var clone = new XElement(card); StripHandlers(clone);
                    foreach (var attr in clone.DescendantsAndSelf().Attributes().Where(a =>
                        new[] { "MouseMove", "PreviewMouseLeftButtonDown", "DragEnter", "DragOver", "DragLeave", "Drop", "Opened", "Closed" }.Contains(a.Name.LocalName)).ToArray()) attr.Remove();
                    var fixture = (Border)Fragment(clone, source.Root!);
                    fixture.DataContext = dashboard switch
                    {
                        "Flashcards" => (object)new FlashcardSetViewModel(new FlashcardSetDocument { Title = "Flashcard drag preview" }),
                        "Quizzes" => new QuizViewModel(new QuizDocument { Title = "Quiz drag preview" }),
                        _ => new MindMapViewModel(new MindMapDocument { Title = "Mind map drag preview" })
                    };
                    var tag = card.Attributes().Single(a => a.Name.LocalName == "CardSurfaceMotionBehavior.ShadowElementName").Value;
                    CaptureGhost(fixture, tag);
                    CaptureGhost(fixture, tag, nestedEntrance: true);
                    if (dashboard == "Flashcards" && tag == "FlashcardSetCardBorder")
                    {
                        CaptureGhost(fixture, tag, nestedEntrance: true, captureDpi: new DpiScale(1, 1));
                        CaptureGhost(fixture, tag, nestedEntrance: true, captureDpi: new DpiScale(2, 2));
                    }
                }
            }
            window.Close(); _app.Resources.MergedDictionaries.Remove(shell.Resources); shell.Close();
        }
        File.WriteAllLines(Path.Combine(_output, "checks.txt"), checks);
    }
}
