using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Scriblucent.Behaviors;

/// <summary>Closed choices use one readable line; popup item templates remain independent.</summary>
public static class ClosedDropdownLabelBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ClosedDropdownLabelBehavior), new PropertyMetadata(false, Changed));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static readonly DependencyProperty DisplayMemberPathProperty = DependencyProperty.RegisterAttached(
        "DisplayMemberPath", typeof(string), typeof(ClosedDropdownLabelBehavior), new PropertyMetadata(null));
    public static void SetDisplayMemberPath(DependencyObject target, string? value) => target.SetValue(DisplayMemberPathProperty, value);
    public static string? GetDisplayMemberPath(DependencyObject target) => (string?)target.GetValue(DisplayMemberPathProperty);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(LabelState), typeof(ClosedDropdownLabelBehavior));
    private static readonly DependencyPropertyDescriptor ContentDescriptor = DependencyPropertyDescriptor.FromProperty(ContentPresenter.ContentProperty, typeof(ContentPresenter));

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ContentPresenter presenter) return;
        presenter.Loaded -= Loaded; presenter.Unloaded -= Unloaded;
        Detach(presenter);
        if (args.NewValue is not true) return;
        presenter.Loaded += Loaded; presenter.Unloaded += Unloaded;
        if (presenter.IsLoaded) Attach(presenter);
    }
    private static void Loaded(object sender, RoutedEventArgs args) => Attach((ContentPresenter)sender);
    private static void Unloaded(object sender, RoutedEventArgs args) => Detach((ContentPresenter)sender);
    private static void Attach(ContentPresenter presenter)
    {
        if (presenter.GetValue(StateProperty) is not null) return;
        if (presenter.TemplatedParent is ComboBox combo && GetDisplayMemberPath(combo) is { Length: > 0 } path)
        {
            presenter.SetCurrentValue(ContentPresenter.ContentTemplateProperty, null);
            presenter.SetCurrentValue(ContentPresenter.ContentTemplateSelectorProperty, null);
            BindingOperations.SetBinding(presenter, ContentPresenter.ContentProperty, new Binding("SelectedItem." + path) { Source = combo });
        }
        var state = new LabelState(presenter);
        presenter.SetValue(StateProperty, state); ContentDescriptor.AddValueChanged(presenter, state.ContentChanged); state.Queue();
    }
    private static void Detach(ContentPresenter presenter)
    {
        if (presenter.GetValue(StateProperty) is not LabelState state) return;
        ContentDescriptor.RemoveValueChanged(presenter, state.ContentChanged); state.Pending?.Abort(); presenter.ClearValue(StateProperty);
    }
    private sealed class LabelState(ContentPresenter presenter)
    {
        internal DispatcherOperation? Pending;
        internal void ContentChanged(object? sender, EventArgs args) => Queue();
        internal void Queue()
        {
            if (Pending is { Status: DispatcherOperationStatus.Pending }) return;
            Pending = presenter.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                Pending = null;
                if (!presenter.IsLoaded || !GetIsEnabled(presenter)) return;
                presenter.ApplyTemplate();
                if (VisualTreeHelper.GetChildrenCount(presenter) == 1 && VisualTreeHelper.GetChild(presenter, 0) is TextBlock text)
                {
                    text.SetCurrentValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
                    text.SetCurrentValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
                }
            }));
        }
    }
}
