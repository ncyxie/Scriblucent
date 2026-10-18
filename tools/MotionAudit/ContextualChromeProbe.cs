using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Models;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using Scriblucent.Views;

internal static partial class Program
{
    private static void ProbeContextualChrome()
    {
        AppSettingsService.SettingsDirectoryOverride = Path.Combine(_output, "settings");
        Directory.CreateDirectory(AppSettingsService.SettingsDirectoryOverride);
        var settings = new AppSettings { EnableAutoSave = false, EnableAiFeatures = false };
        typeof(AppSettingsService).GetField("_cachedSettings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme);
            var shell = new MainWindow(settings); // Never shown: no persisted-content startup.
            var vm = (MainViewModel)shell.DataContext;
            vm.PersistenceDirectoryOverride = Path.Combine(_output, "stores", theme);
            SetLoadingField(vm, "_isNotesDashboardReady", true);
            var search = (Grid)shell.FindName("TopSearchPanel");
            var searchHost = (LayoutRevealHost)search.Parent;
            var searchGrid = (Grid)searchHost.Parent;
            var searchBand = (Border)searchGrid.Parent;
            ((Grid)searchBand.Parent).Children.Remove(searchBand);
            var overlay = (FrameworkElement)shell.FindName("DashboardSelectionDock");
            var overlayHost = (LayoutRevealHost)overlay.Parent;
            ((Grid)overlayHost.Parent).Children.Remove(overlayHost);
            var root = new Grid { Margin = new Thickness(30), Resources = shell.Resources, Background = (Brush)_app.FindResource("WindowBackground") };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(searchBand, 0); searchBand.HorizontalAlignment = HorizontalAlignment.Right; root.Children.Add(searchBand);
            Grid.SetRow(overlayHost, 1); root.Children.Add(overlayHost);
            var window = new Window { Content = root, DataContext = vm, Width = 1040, Height = 420, ShowInTaskbar = false, ShowActivated = false };
            _app.MainWindow = window;
            void Invoke(string method, params object[] args) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, args);
            try
            {
                window.Show(); Pump(80); MotionPolicy.Configure(MotionPreference.Automatic, false);
                var rightEdges = new List<double>(); var partialSearch = false;
                var frames = CaptureFeedback(root, () => Invoke("ExpandTopSearchPanel"), 36,
                    new() { [12] = () => Invoke("CollapseTopSearchPanel"), [14] = () => Invoke("ExpandTopSearchPanel"),
                        [25] = () => Invoke("CollapseTopSearchPanel") },
                    afterFrame: () =>
                    {
                        if (search.IsVisible && search.ActualWidth > 0)
                        {
                            rightEdges.Add(search.TranslatePoint(new Point(search.ActualWidth, 0), root).X);
                            partialSearch |= search.Opacity is > .01 and < .99;
                        }
                    });
                SaveGif(frames, $"search-anchored-{theme}.gif");
                Require(partialSearch && rightEdges.Max() - rightEdges.Min() < 1,
                    $"Search did not fade locally or its button-side edge drifted: fade={partialSearch}, right={rightEdges.Min()}..{rightEdges.Max()}.");
                Require(searchHost.Visibility == Visibility.Collapsed, "Search did not finish closing.");
                bool partialOverlay = false; var collectionSize = root.RenderSize;
                frames = CaptureFeedback(root, () => vm.DashboardSelection.Begin(), 36,
                    new() { [12] = () => vm.DashboardSelection.End(), [14] = () => vm.DashboardSelection.Begin(),
                        [25] = () => vm.DashboardSelection.End() },
                    afterFrame: () =>
                    {
                        if (!overlay.IsVisible) return;
                        partialOverlay |= overlay.Opacity is > .01 and < .99;
                        Require(root.RenderSize == collectionSize, "Selection island changed collection viewport size.");
                    });
                WaitLoading(() => overlayHost.Visibility == Visibility.Collapsed);
                Require(partialOverlay, "Floating selection island did not fade.");
                SaveGif(frames, $"multiselect-anchored-{theme}.gif");
                foreach (var preference in new[] { MotionPreference.Reduced, MotionPreference.Off })
                {
                    MotionPolicy.Configure(preference, false);
                    vm.DashboardSelection.Begin(); Pump(110);
                    var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(overlay);
                    Require(scale.ScaleX == 1 && translate.Y == 0 && overlay.Opacity == 1, "Reduced multiselect retained spatial movement.");
                    vm.DashboardSelection.End(); Pump(110);
                    Require(overlay.Visibility == Visibility.Collapsed, "Reduced multiselect did not close.");
                    Invoke("ExpandTopSearchPanel"); window.UpdateLayout();
                    Require(double.IsNaN(searchHost.Extent) && search.Opacity == 1, "Reduced search retained width animation.");
                    Invoke("CollapseTopSearchPanel");
                }
            }
            finally { window.Close(); shell.Close(); }
            ProbeTrashBounds(theme, settings);
            checks.Add($"PASS {theme}: compiled search keeps its button-side edge stationary through open/close/reversal; shared selection island fades without resizing its viewport and reverses cleanly; Reduced/Off settle without spatial motion; actual Trash keeps its bounds across empty/1/40/empty items and adapts to 1020x780 and 820x560 owners.");
            File.WriteAllLines(Path.Combine(_output, "context-checks.txt"), checks);
            Console.WriteLine(checks[^1]);
        }
    }

    private static void ProbeTrashBounds(string theme, AppSettings settings)
    {
        MotionPolicy.Configure(MotionPreference.Off, false);
        var vm = new MainViewModel(settings, false, false) { PersistenceDirectoryOverride = Path.Combine(_output, "stores", theme + "-trash") };
        var trash = new TrashPanel { DataContext = vm };
        var window = new Window { Content = trash, Width = 1020, Height = 780, ShowInTaskbar = false, ShowActivated = false };
        _app.MainWindow = window;
        try
        {
            window.Show(); Pump(60);
            typeof(TrashPanel).GetMethod("PopulatePanelContent", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(trash, null);
            var surface = (Grid)trash.FindName("TrashSurface");
            foreach (var size in new[] { new Size(1020, 780), new Size(820, 560) })
            {
                window.Width = size.Width; window.Height = size.Height; Pump(40); window.UpdateLayout();
                var bounds = surface.RenderSize;
                foreach (int count in new[] { 0, 1, 40, 0 })
                {
                    vm.TrashedNotes.Clear();
                    for (int i = 0; i < count; i++) vm.TrashedNotes.Add(new NoteCardViewModel(new NoteDocument
                        { Title = $"Archived idea {i + 1}", IsDeleted = true, DeletedAt = DateTime.UtcNow }, _ => { }));
                    vm.RefreshTrashedItems(); window.UpdateLayout();
                    Require(surface.RenderSize == bounds, "Trash resized when its content changed.");
                    Require(surface.ActualHeight <= trash.ActualHeight - 39 && surface.ActualWidth <= trash.ActualWidth - 39,
                        "Trash escaped its owner insets.");
                    SaveLocalControlCapture(surface, $"trash-{theme}-{size.Height}-{count}");
                }
            }
        }
        finally { window.Close(); }
    }
}
