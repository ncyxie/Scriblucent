using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Scriblucent.Converters;

/// <summary>Text inset includes the rest border and a fixed 32 DIP disclosure area.</summary>
public sealed class ComboBoxContentInsetConverter : IMultiValueConverter
{
    public static ComboBoxContentInsetConverter Instance { get; } = new();
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var padding = values.ElementAtOrDefault(0) is Thickness p ? p : new Thickness(10, 0, 10, 0);
        var border = values.ElementAtOrDefault(1) is Thickness b ? b : new Thickness();
        // Editable text already has TextBoxView's caret gutter; selected labels need the same inset.
        double gutter = values.ElementAtOrDefault(2) is true ? 0 : FieldContentInsetConverter.NativeTextGutter;
        return new Thickness(padding.Left + border.Left + gutter, padding.Top + border.Top,
            padding.Right + border.Right + 32 + gutter, padding.Bottom + border.Bottom);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
