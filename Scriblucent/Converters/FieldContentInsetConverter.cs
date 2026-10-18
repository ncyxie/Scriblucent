using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Scriblucent.Converters;

/// <summary>Align an external placeholder with its input's text, including the theme's rest border.</summary>
public sealed class FieldContentInsetConverter : IMultiValueConverter
{
    // TextBoxView reserves this leading/trailing caret gutter in addition to TextBox.Padding.
    internal const double NativeTextGutter = 2;
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var padding = values.ElementAtOrDefault(0) is Thickness p ? p : new Thickness();
        var border = values.ElementAtOrDefault(1) is Thickness b ? b : new Thickness();
        return new Thickness(padding.Left + border.Left + NativeTextGutter, padding.Top + border.Top,
            padding.Right + border.Right + NativeTextGutter, padding.Bottom + border.Bottom);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
