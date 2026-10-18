using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Scriblucent.Converters;

/// <summary>A one-based item position and count give only the final item overlay clearance, including after reorder.</summary>
public sealed class CollectionTailMarginConverter : IMultiValueConverter
{
    public Thickness BaseMargin { get; set; }
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool last = values.Length >= 3 && values[0] is int position && values[1] is int count && count > 0 && position == count;
        double clearance = last && values[2] is double number ? number : 0;
        return new Thickness(BaseMargin.Left, BaseMargin.Top, BaseMargin.Right, BaseMargin.Bottom + clearance);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
