using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Behaviors;

/// <summary>Editor and quiz selection uses the same fixed-contour, paint-only rim as dashboard selection.</summary>
public static class SelectionRimBehavior
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(SelectionRimBehavior), new PropertyMetadata(false, Changed));
    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.Loaded -= Loaded; element.Loaded += Loaded;
        if (element.IsLoaded) Sync(element, true);
    }
    private static void Loaded(object sender, RoutedEventArgs e) => Sync((FrameworkElement)sender, false);
    private static void Sync(FrameworkElement element, bool animate)
        => AccentMotion.Set(element, GetIsSelected(element), element.TryFindResource("AccentRimBrush") as Brush,
            element is Border border ? border.CornerRadius.TopLeft : 10, 2, animate);
}
