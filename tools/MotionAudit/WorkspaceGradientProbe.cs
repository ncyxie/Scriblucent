using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Rendering;

internal static partial class Program
{
    private static void ProbeWorkspaceGradient()
    {
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        {
            LoadNativeControlResources(theme); MotionPolicy.Configure(MotionPreference.Off, false);
            var source = (SolidColorBrush)_app.FindResource("WindowBackground");
            var palette = SurfaceMaterialPalette.Get(source.Color, SurfaceMaterialKind.Workspace, theme == "Dark");
            var parent = new Grid { Width = 832, Height = 760 };
            parent.ColumnDefinitions.Add(new()); parent.ColumnDefinitions.Add(new());
            var before = new Border { Background = palette.Fill, CornerRadius = new(18), Margin = new Thickness(8, 0, 8, 0) };
            var after = new SurfaceMaterialBorder { Background = source, CornerRadius = new(18), Margin = new Thickness(8, 0, 8, 0) };
            SurfaceMaterial.SetKind(after, SurfaceMaterialKind.Workspace);
            Grid.SetColumn(after, 1); parent.Children.Add(before); parent.Children.Add(after);
            var window = new Window { Content = parent, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                var started = Stopwatch.GetTimestamp(); window.Show(); window.UpdateLayout(); Pump(80);
                var dpi = VisualTreeHelper.GetDpi(after);
                var capture = Snapshot(parent, dpi.DpiScaleY);
                var data = new byte[capture.PixelWidth * capture.PixelHeight * 4]; capture.CopyPixels(data, capture.PixelWidth * 4, 0);
                double MaxStep(Border face)
                {
                    var origin = face.TranslatePoint(new Point(), parent);
                    var start = (int)Math.Round((origin.X + 24) * dpi.DpiScaleX);
                    var end = (int)Math.Round((origin.X + face.ActualWidth - 24) * dpi.DpiScaleX);
                    var top = (int)Math.Round((origin.Y + 24) * dpi.DpiScaleY);
                    var bottom = (int)Math.Round((origin.Y + face.ActualHeight * .38) * dpi.DpiScaleY);
                    var maximum = 0d; var previous = double.NaN;
                    for (var y = top; y < bottom; y++)
                    {
                        var sum = 0d;
                        for (var x = start; x < end; x++) sum += data[(y * capture.PixelWidth + x) * 4];
                        var mean = sum / (end - start);
                        if (!double.IsNaN(previous)) maximum = Math.Max(maximum, Math.Abs(mean - previous));
                        previous = mean;
                    }
                    return maximum;
                }
                var oldStep = MaxStep(before); var newStep = MaxStep(after);
                Require(oldStep > .8 && newStep < .12, $"{theme}: rendered row step {oldStep:F4} before, {newStep:F4} after.");
                SaveControlCapture(parent, $"gradient-comparison-{theme}");
                checks.Add($"{theme}: native old/new workspace comparison, row brightness jump {oldStep:F4} -> {newStep:F4} code values at live DPI {dpi.DpiScaleY:F2}; 100/125/150/200% captures.");
                // Measure creation and reuse separately from layout/capture or presented-frame performance.
                var textureColors = palette.Colors with { Top = Color.FromRgb((byte)(palette.Colors.Top.R + 1), palette.Colors.Top.G, palette.Colors.Top.B) };
                started = Stopwatch.GetTimestamp();
                var state = new WorkspaceGradientTexture.Fill(); var first = state.Get(textureColors, 763, dpi);
                var cold = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                started = Stopwatch.GetTimestamp();
                for (var i = 0; i < 10000; i++) Require(ReferenceEquals(first, state.Get(textureColors, 763, dpi)), "Workspace fill rebuilt during reuse.");
                var warm = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                checks.Add($"{theme}: 64-pixel-wide cached texture, measured cold creation {cold:F3} ms; 10,000 identical fill reuses {warm:F3} ms; frozen static brush. Host timings, not presented FPS.");
            }
            finally { window.Close(); Pump(30); }
        }
        Require(!RenderTransitionClock.IsAnyRunning, "Gradient fixture left active motion work.");
        File.WriteAllLines(Path.Combine(_output, "gradient-checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
}
