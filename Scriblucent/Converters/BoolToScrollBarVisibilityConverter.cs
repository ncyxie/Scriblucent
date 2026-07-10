using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace Scriblucent.Converters;

public class BoolToScrollBarVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        return ScrollBarVisibility.Auto;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ScrollBarVisibility vis)
            return vis == ScrollBarVisibility.Auto || vis == ScrollBarVisibility.Visible;
        return true;
    }
}
