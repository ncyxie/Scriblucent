using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Scriblucent.Controls;
using Scriblucent.Models;

internal static partial class Program
{
    // Load the real compiled shell without showing it: ContentRendered normally loads
    // persisted documents. Explicit settings keep this probe separate from saved preferences.
    private static void ProbeCompiledShell()
    {
        typeof(Scriblucent.Services.AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new AppSettings { EnableAutoSave = false });
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadResources(theme);
            var window = new Scriblucent.MainWindow(new AppSettings { Theme = theme });
            var search = (Grid)window.FindName("TopSearchPanel");
            if (search.Children.OfType<BakedElevationLayer>().Any())
                throw new InvalidOperationException("Search fields must use flat field chrome.");
            window.Measure(new Size(1280, 800));
            window.Arrange(new Rect(0, 0, 1280, 800));
            window.UpdateLayout();
            if (window.FindName("TopSearchTextBox") is not TextBox)
                throw new InvalidOperationException("Compiled search field was not loaded.");
            var frame = (SurfaceMaterialBorder)window.FindName("ContentStage");
            Require(SurfaceMaterial.GetKind(frame) == SurfaceMaterialKind.Workspace && frame.CornerRadius == new CornerRadius(18),
                "Main window differs from the shared rounded workspace frame.");
            // An unshown Window has no native client size; lay out its compiled content explicitly.
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1280, 800)); content.Arrange(new Rect(0, 0, 1280, 800)); content.UpdateLayout();
            Require(content.ActualWidth > 1200 && frame.ActualWidth > 500, "Offline shell capture has no usable layout.");
            SaveControlCapture(content, $"main-workspace-{theme}");
            var result = $"{theme}: compiled MainWindow and four dashboard pages loaded and laid out; shared rounded workspace material. Window not shown; no persisted document startup.";
            checks.Add(result);
            Console.WriteLine(result);
            window.Close();
        }
        Require(typeof(Scriblucent.Services.AppSettingsService).GetField("_pendingJson", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) is null,
            "A compiled-shell fixture queued a preference write.");
        File.WriteAllLines(Path.Combine(_output, "compiled-shell-checks.txt"), checks);
    }
}
