using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Localization;

namespace Scriblucent.Controls;

/// <summary>Noninteractive measurements from the same styles and text metrics as dashboard chrome.</summary>
internal static class DashboardChromeMetrics
{
    internal static double Number(FrameworkElement host, string key, double fallback)
        => host.TryFindResource(key) is double value ? value : fallback;

    internal static T StyleValue<T>(FrameworkElement host, string key, DependencyProperty property, T fallback)
    {
        for (var style = host.TryFindResource(key) as Style; style is not null; style = style.BasedOn)
            foreach (var setter in style.Setters.OfType<Setter>())
                if (setter.Property == property && setter.Value is T value) return value;
        return fallback;
    }

    internal static Size Text(FrameworkElement host, string text, double size, FontWeight weight)
    {
        // A text-only measure supplies WPF's real line height, including its line gap.
        // No input controls, documents or dashboard trees are constructed here.
        var label = new TextBlock { Text = text, FontFamily = host.TryFindResource("FontFamily.UI") as FontFamily ?? SystemFonts.MessageFontFamily,
            FontSize = size, FontWeight = weight, FlowDirection = host.FlowDirection,
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentUICulture.IetfLanguageTag) };
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Ideal);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return label.DesiredSize;
    }

    internal static (double Today, double Add) CalendarActions(FrameworkElement host)
    {
        var todayPadding = StyleValue(host, "CalendarHeaderButtonStyle", Control.PaddingProperty, new Thickness(8, 0, 8, 0));
        var todaySize = Number(host, "Type.Control.Toolbar", 14);
        var addSize = Number(host, "Type.Body", 14);
        var minimum = StyleValue(host, "DialogPrimaryButtonStyle", FrameworkElement.MinWidthProperty, 100d);
        var dpi = VisualTreeHelper.GetDpi(host).DpiScaleX;
        double Round(double value) => Math.Round(value * dpi) / dpi;
        return (Round(Text(host, LocalizationService.GetString("PremiumToday"), todaySize, FontWeights.Normal).Width + todayPadding.Left + todayPadding.Right),
            Math.Max(minimum, Round(Text(host, LocalizationService.GetString("CalendarAddSchedule"), addSize, FontWeights.SemiBold).Width + 24)));
    }
}
