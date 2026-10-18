using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Scriblucent.Behaviors;

/// <summary>Wrap the native string/display-member label without replacing custom item templates.</summary>
public static class DropdownLabelBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DropdownLabelBehavior), new PropertyMetadata(false, Changed));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ContentPresenter presenter) return;
        presenter.Loaded -= Loaded;
        if (args.NewValue is not true) return;
        presenter.Loaded += Loaded;
        if (presenter.IsLoaded) QueueWrap(presenter);
    }

    private static void Loaded(object sender, RoutedEventArgs args) => QueueWrap((ContentPresenter)sender);
    private static void QueueWrap(ContentPresenter presenter) => presenter.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
    {
        if (!presenter.IsLoaded || !GetIsEnabled(presenter) || presenter.Content is UIElement) return;
        presenter.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(presenter) == 1 && VisualTreeHelper.GetChild(presenter, 0) is TextBlock label
            && label.ReadLocalValue(TextBlock.TextWrappingProperty) == DependencyProperty.UnsetValue)
            label.SetCurrentValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
    }));
}
