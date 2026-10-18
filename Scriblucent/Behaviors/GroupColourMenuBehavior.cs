using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace Scriblucent.Behaviors;

/// <summary>Colour choices show the saved value, including when the current choice is clicked again.</summary>
public static class GroupColourMenuBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(GroupColourMenuBehavior), new PropertyMetadata(false, Changed));
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ContextMenu menu) return;
        menu.Opened -= Opened;
        menu.RemoveHandler(MenuItem.ClickEvent, new RoutedEventHandler(Clicked));
        if (e.NewValue is true)
        {
            menu.Opened += Opened;
            menu.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler(Clicked), true);
        }
    }
    private static void Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Dispatcher.BeginInvoke(() =>
        {
            if (GetIsEnabled(menu))
                foreach (var item in menu.Items.OfType<MenuItem>()) Refresh(item);
        }, DispatcherPriority.Loaded);
    }
    private static void Clicked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem item)
            item.Dispatcher.BeginInvoke(() => Refresh(item), DispatcherPriority.DataBind);
    }
    private static void Refresh(MenuItem item)
        => BindingOperations.GetMultiBindingExpression(item, MenuItem.IsCheckedProperty)?.UpdateTarget();
}
