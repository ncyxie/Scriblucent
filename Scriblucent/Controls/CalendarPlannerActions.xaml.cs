using System.Windows;
using System.Windows.Controls;
using Scriblucent.ViewModels;
namespace Scriblucent.Controls;
public partial class CalendarPlannerActions : UserControl
{
    public CalendarPlannerActions() => InitializeComponent();
    private void Send(CalendarPlannerAction action) => RaiseEvent(new CalendarPlannerRequestEventArgs(action, null, (DataContext as CalendarPlannerViewModel)?.AddDate ?? DateTime.Today) { RoutedEvent = CalendarPlannerView.RequestEvent });
    private void Today_Click(object sender, RoutedEventArgs e) => Send(CalendarPlannerAction.Today);
    private void Add_Click(object sender, RoutedEventArgs e) => Send(CalendarPlannerAction.Add);
}
