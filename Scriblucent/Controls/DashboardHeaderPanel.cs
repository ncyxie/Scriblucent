using System.Windows;
using System.Windows.Controls;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>Keeps the shared icon toolbar aligned and bounds search to the available width.</summary>
public sealed class DashboardHeaderPanel : Panel
{
    private static readonly DependencyPropertyKey SearchWidthKey = DependencyProperty.RegisterReadOnly(nameof(SearchWidth), typeof(double), typeof(DashboardHeaderPanel), new PropertyMetadata(320d));
    public static readonly DependencyProperty SearchWidthProperty = SearchWidthKey.DependencyProperty;
    public double SearchWidth => (double)GetValue(SearchWidthProperty);
    private double _brandWidth, _actionsWidth, _utilitiesWidth, _gap;
    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count != 3) return default;
        var brand = InternalChildren[0]; var actions = (Panel)InternalChildren[1];
        var utilityHost = (Border)InternalChildren[2]; var utilities = (Panel)utilityHost.Child;
        var unlimited = new Size(double.PositiveInfinity, 40);
        brand.Measure(unlimited);
        actions.Measure(unlimited);
        utilityHost.Measure(unlimited);
        double fixedUtilities = utilities.Children.OfType<Button>().Sum(button => button.DesiredSize.Width) + utilityHost.Padding.Left + utilityHost.Padding.Right;
        bool searchVisible = utilities.Children.OfType<LayoutRevealHost>().Any(host => host.Child?.Visibility == Visibility.Visible);
        double width = double.IsFinite(available.Width) ? available.Width : brand.DesiredSize.Width + actions.DesiredSize.Width + utilityHost.DesiredSize.Width + 20;
        _gap = actions.Visibility == Visibility.Collapsed ? 0 : 8;
        _actionsWidth = actions.DesiredSize.Width;
        double reserveSearch = searchVisible ? 100 : 0;
        _brandWidth = Math.Min(brand.DesiredSize.Width, Math.Max(0, width - _actionsWidth - fixedUtilities - _gap - 20 - reserveSearch));
        double searchWidth = Math.Clamp(width - _brandWidth - _actionsWidth - fixedUtilities - _gap - 20, 0, 320);
        if (Math.Abs(SearchWidth - searchWidth) > .5) SetValue(SearchWidthKey, searchWidth);
        utilityHost.Measure(unlimited); _utilitiesWidth = utilityHost.DesiredSize.Width;
        _brandWidth = Math.Max(0, Math.Min(_brandWidth, width - _actionsWidth - _utilitiesWidth - _gap - 12));
        brand.Measure(new Size(_brandWidth, 40));
        return new Size(width, Math.Max(40, Math.Max(actions.DesiredSize.Height, utilities.DesiredSize.Height)));
    }
    protected override Size ArrangeOverride(Size available)
    {
        if (InternalChildren.Count != 3) return available;
        InternalChildren[0].Arrange(new Rect(0, 0, _brandWidth, available.Height));
        double right = available.Width - _utilitiesWidth;
        InternalChildren[2].Arrange(new Rect(right, 0, _utilitiesWidth, available.Height));
        InternalChildren[1].Arrange(new Rect(Math.Max(_brandWidth, right - _gap - _actionsWidth), 0, _actionsWidth, available.Height));
        return available;
    }
}
