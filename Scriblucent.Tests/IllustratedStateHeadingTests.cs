using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

public class IllustratedStateHeadingTests
{
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception caught) { error = caught; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void Responsive_headlines_keep_their_line_count_and_width_when_height_is_limited() => Sta(() =>
    {
        foreach (var family in new[] { "Segoe UI Variable Display", "Segoe UI" })
        foreach (var text in new[] { "Your study story starts here", "Put your knowledge to the test", "Čia prasideda jūsų mokymosi istorija", "Išbandykite savo žinias" })
        {
            var heading = new IllustratedStateHeading { Text = text, FontFamily = new FontFamily(family), FontSize = 24, LineHeight = 30 };
            var ordinary = new TextBlock { Text = text, FontFamily = heading.FontFamily, FontSize = 24, LineHeight = 30, LineStackingStrategy = LineStackingStrategy.MaxHeight, TextWrapping = TextWrapping.Wrap };
            foreach (var width in new[] { 320d, 200, 140, 280, 320 })
            {
                ordinary.Measure(new Size(width, double.PositiveInfinity));
                heading.Measure(new Size(width, double.PositiveInfinity));
                var full = heading.DesiredSize;
                Assert.InRange(full.Width, 1, width);
                Assert.Equal(ordinary.DesiredSize.Height, full.Height, 1);
                heading.Arrange(new Rect(full));
                var rendered = (TextBlock)VisualTreeHelper.GetChild(heading, 0);
                Assert.Equal(text, rendered.Text);
                Assert.True(rendered.RenderSize.Height >= rendered.DesiredSize.Height - .5);
                heading.Measure(new Size(width, 20));
                Assert.Equal(full.Width, heading.DesiredSize.Width, 1);
            }
        }
    });

    [Fact]
    public void Short_empty_and_explicitly_split_titles_update_and_remain_accessible() => Sta(() =>
    {
        var heading = new IllustratedStateHeading { FontSize = 24, LineHeight = 30 };
        var peer = UIElementAutomationPeer.CreatePeerForElement(heading)!;
        foreach (var text in new[] { "A fresh page", "", "Your next idea\nstarts here", "Naujas puslapis" })
        {
            heading.Text = text;
            heading.Measure(new Size(320, double.PositiveInfinity));
            Assert.Equal(text, peer.GetName());
            Assert.Equal(AutomationControlType.Text, peer.GetAutomationControlType());
            Assert.False(heading.Focusable);
            if (text.Length > 0) Assert.InRange(heading.DesiredSize.Width, 1, 320);
        }
    });
}
