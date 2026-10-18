using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;

internal static partial class Program
{
    private static void ProbeButtonHoverEdge(bool corners = false)
    {
        var checks = new List<string>();
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var before in new[] { true, false })
        {
            LoadNativeControlResources(theme); MotionPolicy.Configure(MotionPreference.Automatic, false);
            var root = new Grid { Width = corners ? 240 : 160, Height = 100, HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Background = theme == "Light" ? Brushes.White : (Brush)_app.FindResource("WindowBackground") };
            var button = new Button { Width = corners ? 180 : 100, Height = 40, Content = corners ? "Add flashcard" : "Close", FontSize = 14,
                Style = (Style)_app.FindResource("DialogPrimaryButtonStyle") };
            root.Children.Add(button);
            var window = HostSurfaceFixture(root, corners ? 320 : 240, 180);
            try
            {
                Pump(80); window.UpdateLayout();
                var face = (InteractionSurface)button.Template.FindName("ButtonBorder", button);
                if (before)
                {
                    var oldEdge = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
                    oldEdge.GradientStops.Add(new(Color.FromArgb(corners ? (byte)0x80 : (byte)0xBB, 255, 255, 255), 0));
                    if (corners)
                    {
                        oldEdge.GradientStops.Add(new(Color.FromArgb(0x30, 255, 255, 255), .18));
                        oldEdge.GradientStops.Add(new(Colors.Transparent, .42));
                    }
                    oldEdge.GradientStops.Add(new(corners ? Colors.Transparent : Color.FromArgb(0x28, 255, 255, 255), 1)); oldEdge.Freeze();
                    face.HighlightEdge = oldEdge;
                    var oldWash = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
                    oldWash.GradientStops.Add(new(Color.FromArgb(0xA0, 255, 255, 255), 0));
                    oldWash.GradientStops.Add(new(Color.FromArgb(0, 255, 255, 255), .16));
                    oldWash.GradientStops.Add(new(Color.FromArgb(0, 255, 255, 255), 1)); oldWash.Freeze();
                    face.HighlightWash = oldWash;
                }
                face.AnimateState(true, false, false); window.UpdateLayout();
                SaveControlCapture(root, $"{(corners ? "hover-corners" : "hover-edge")}-{theme}-{(before ? "before" : "after")}");
                checks.Add($"{theme}/{(before ? "original light" : "current light")}: production {button.Width} x 40 DIP {button.Content} button, settled hover, 100/125/150/200% captures.");
            }
            finally { window.Close(); Pump(40); }
        }
        Require(!RenderTransitionClock.IsAnyRunning, "Hover edge fixture left motion observers behind.");
        File.WriteAllLines(Path.Combine(_output, "checks.txt"), checks);
        foreach (var check in checks) Console.WriteLine(check);
    }
}
