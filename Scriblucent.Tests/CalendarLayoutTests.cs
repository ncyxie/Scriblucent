using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Scriblucent.Controls;

namespace Scriblucent.Tests;

public class CalendarLayoutTests
{
    private sealed class MeasuredChoice : FrameworkElement
    {
        internal int Count;
        protected override Size MeasureOverride(Size available) { Count++; return new Size(140, 32); }
    }

    [Fact]
    public void Resizing_the_agenda_measures_each_choice_once_per_width() => Sta(() =>
    {
        var header = new CalendarPlannerHeaderPanel();
        var choice = new MeasuredChoice();
        header.Children.Add(new Border { Width = 244, Height = 32 }); header.Children.Add(choice);
        for (var width = 440d; width <= 560; width += 4)
        {
            header.Measure(new Size(width, double.PositiveInfinity));
            header.Arrange(new Rect(0, 0, width, header.DesiredSize.Height));
        }
        Assert.Equal(31, choice.Count);
    });

    [Fact]
    public void Calendar_reserves_the_real_wrapped_header_height_when_metrics_change() => Sta(() =>
    {
        var header = new CalendarPlannerHeaderPanel();
        header.Children.Add(new Border { Width = 244, Height = 32 });
        var choice = new Border { Width = 200, Height = 32 }; header.Children.Add(choice);
        var agenda = new Grid();
        agenda.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); agenda.RowDefinitions.Add(new RowDefinition());
        agenda.Children.Add(header);
        var body = new DashboardCalendarPanel { Width = 720 };
        body.Children.Add(new Border { Width = 256, Height = 288 }); body.Children.Add(agenda);
        var host = new Window { Width = 780, Height = 700, Content = new StackPanel { Children = { body } },
            Opacity = 0, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            host.Show();
            foreach (var choiceWidth in new[] { 200d, 100, 200 })
            {
                choice.Width = choiceWidth;
                host.UpdateLayout();
                var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
                Assert.Equal(choiceWidth == 200 ? 336 : 296, body.DesiredSize.Height);
                Assert.Equal(choiceWidth == 200 ? 72 : 32, header.ActualHeight);
                Assert.True(agenda.TranslatePoint(new Point(0, agenda.ActualHeight), body).Y <= body.ActualHeight);
            }
        }
        finally { host.Close(); }
    });

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Calendar layout check timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
