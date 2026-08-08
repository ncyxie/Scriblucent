using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Scriblucent.Converters;

/// <summary>
/// Drives the icon/check gutter of leaf menu items: items that are checkable or carry an icon
/// reserve a fixed slot (so the checkmark and the icons line up, Word-style), while plain items
/// get no slot at all - no stray indent. Used for both the gutter's MinWidth and Margin.
/// </summary>
public sealed class MenuGutterGeometryConverter : IMultiValueConverter
{
    public const double SlotWidth = 20;
    public const double SlotMargin = 10;

    public static MenuGutterGeometryConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var hasIcon = values.Length > 0 && values[0] is not null;
        var isCheckable = values.Length > 1 && values[1] is true;
        if (!hasIcon && !isCheckable)
            return targetType == typeof(Thickness) ? new Thickness(0) : 0.0;

        return targetType == typeof(Thickness)
            ? new Thickness(0, 0, SlotMargin, 0)
            : SlotWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
