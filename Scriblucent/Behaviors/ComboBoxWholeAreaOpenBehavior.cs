using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Scriblucent.Behaviors;

/// <summary>
/// Toggles the combo dropdown when any part of the closed chrome is clicked, not only the chevron.
/// </summary>
public static class ComboBoxWholeAreaOpenBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ComboBoxWholeAreaOpenBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo)
            return;

        if (e.NewValue is true)
        {
            combo.PreviewMouseLeftButtonDown += Combo_PreviewMouseLeftButtonDown;
            combo.Unloaded += Combo_Unloaded;
        }
        else
        {
            Detach(combo);
        }
    }

    private static void Combo_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox combo)
            Detach(combo);
    }

    private static void Detach(ComboBox combo)
    {
        combo.PreviewMouseLeftButtonDown -= Combo_PreviewMouseLeftButtonDown;
        combo.Unloaded -= Combo_Unloaded;
    }

    private static void Combo_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBox combo || !combo.IsEnabled)
            return;

        if (e.OriginalSource is not DependencyObject source)
            return;

        if (IsInsideOpenDropdown(source))
            return;

        if (combo.IsEditable && IsInsideEditableTextBox(source, combo))
        {
            if (!combo.IsDropDownOpen)
                combo.IsDropDownOpen = true;

            return;
        }

        combo.IsDropDownOpen = !combo.IsDropDownOpen;
        e.Handled = true;
    }

    private static bool IsInsideOpenDropdown(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ComboBoxItem)
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsInsideEditableTextBox(DependencyObject source, ComboBox combo)
    {
        var current = source;
        while (current is not null)
        {
            if (current is TextBox textBox
                && combo.Template?.FindName("PART_EditableTextBox", combo) is TextBox templateTextBox
                && ReferenceEquals(textBox, templateTextBox))
            {
                return true;
            }

            if (current is ComboBox)
                return false;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
