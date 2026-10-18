using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Scriblucent.Converters;

/// <summary>Only offer a section swap toward an existing, visible neighbouring section.</summary>
public sealed class DashboardSectionMoveConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 3 || values[0] is not int row || values[1] is not int peerRow
            || values[2] is not Visibility.Visible) return false;
        return (parameter as string) switch
        {
            "Up" => peerRow < row,
            "Down" => peerRow > row,
            _ => false
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
