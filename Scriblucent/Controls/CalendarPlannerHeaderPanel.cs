using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using Scriblucent.Behaviors;
using Scriblucent.ViewModels;

namespace Scriblucent.Controls;

/// <summary>Keep three mode choices and the type filter readable at narrow agenda widths.</summary>
public sealed class CalendarPlannerHeaderPanel : Panel
{
    internal const double ModeWidth = 244, WrapExtraHeight = 40;
    private double _filterWidth;
    private static readonly Dictionary<(string Culture, string Family, double Size, Thickness Padding, Thickness Border, double Dpi, FlowDirection Flow), double> FilterWidths = [];
    internal static bool NeedsWrap(double width, double filterWidth) => width < ModeWidth + 12 + filterWidth;
    internal static bool NeedsWrap(double width, FrameworkElement host)
        => NeedsWrap(width, FilterWidth(host));
    internal static double FilterWidth(FrameworkElement host)
    {
        var family = host.TryFindResource("FontFamily.UI") as FontFamily ?? new FontFamily("Segoe UI");
        double size = host.TryFindResource("Type.Control.Form") is double fontSize ? fontSize : 14;
        var padding = host.TryFindResource("Field.ContentPadding") is Thickness p ? p : new Thickness(10, 0, 10, 0);
        var border = host.TryFindResource("Field.RestBorderThickness") is Thickness b ? b : new Thickness();
        double dpi = VisualTreeHelper.GetDpi(host).PixelsPerDip;
        var key = (CultureInfo.CurrentUICulture.Name, family.Source, size, padding, border, dpi, host.FlowDirection);
        if (!FilterWidths.TryGetValue(key, out double filterWidth))
        {
            var metrics = new ComboBox { FontFamily = family, FontSize = size, Padding = padding, BorderThickness = border, FlowDirection = host.FlowDirection };
            filterWidth = DropdownChoiceWidthBehavior.MeasureWidth(CalendarPlannerViewModel.FilterLabels, metrics, dpi);
            FilterWidths[key] = filterWidth;
        }
        return filterWidth;
    }
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 400 : Math.Max(0, available.Width);
        if (InternalChildren.Count > 1)
        {
            InternalChildren[1].Measure(new Size(double.PositiveInfinity, 32));
            _filterWidth = InternalChildren[1].DesiredSize.Width;
        }
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Measure(new Size(i == 0 ? Math.Min(ModeWidth, width) : NeedsWrap(width, _filterWidth) ? width : width - ModeWidth - 12, 32));
        return new(width, NeedsWrap(width, _filterWidth) ? 72 : 32);
    }
    protected override Size ArrangeOverride(Size available)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(i == 0 ? new Rect(0, 0, Math.Min(ModeWidth, available.Width), 32)
                : NeedsWrap(available.Width, _filterWidth) ? new Rect(0, 40, available.Width, 32)
                : new Rect(ModeWidth + 12, 0, available.Width - ModeWidth - 12, 32));
        return available;
    }
}
