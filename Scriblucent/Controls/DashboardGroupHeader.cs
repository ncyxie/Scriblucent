using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;

namespace Scriblucent.Controls;

/// <summary>A shared collection heading; actions are content so native handlers stay at the dashboard.</summary>
public sealed class DashboardGroupHeader : HeaderedContentControl
{
    public static readonly RoutedUICommand ToggleExpansionCommand = new("Toggle group", nameof(ToggleExpansionCommand), typeof(DashboardGroupHeader));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(DashboardGroupHeader),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public static readonly DependencyProperty ItemCountProperty = DependencyProperty.Register(
        nameof(ItemCount), typeof(int), typeof(DashboardGroupHeader), new PropertyMetadata(0));
    public static readonly DependencyProperty GroupBrushProperty = DependencyProperty.Register(
        nameof(GroupBrush), typeof(Brush), typeof(DashboardGroupHeader), new PropertyMetadata(null));
    public int ItemCount { get => (int)GetValue(ItemCountProperty); set => SetValue(ItemCountProperty, value); }
    public Brush? GroupBrush { get => (Brush?)GetValue(GroupBrushProperty); set => SetValue(GroupBrushProperty, value); }

    public DashboardGroupHeader()
    {
        CommandBindings.Add(new CommandBinding(ToggleExpansionCommand, (_, e) =>
        {
            SetCurrentValue(IsExpandedProperty, !IsExpanded);
            e.Handled = true;
        }));
    }
}

/// <summary>Measures the name within its real slot, with a second action row on compact surfaces.</summary>
public sealed class DashboardGroupHeaderPanel : Panel
{
    internal const double CompactWidth = 300;
    internal const double RowHeight = 36, Gap = 8, RowGap = 8;
    private double _actionsWidth;

    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count != 2) return default;
        InternalChildren[0].Measure(new Size(double.PositiveInfinity, RowHeight));
        _actionsWidth = InternalChildren[0].DesiredSize.Width;
        var width = double.IsFinite(available.Width) ? Math.Max(0, available.Width) : double.PositiveInfinity;
        var wrapped = width < CompactWidth;
        InternalChildren[1].Measure(new Size(wrapped ? width : Math.Max(0, width - _actionsWidth - Gap), RowHeight));
        return new Size(double.IsFinite(width) ? width : _actionsWidth + InternalChildren[1].DesiredSize.Width + Gap,
            wrapped ? RowHeight * 2 + RowGap : RowHeight);
    }

    protected override Size ArrangeOverride(Size available)
    {
        if (InternalChildren.Count != 2) return available;
        var wrapped = available.Width < CompactWidth;
        double toolsY = wrapped ? RowHeight + RowGap : 0;
        InternalChildren[0].Arrange(new Rect(0, toolsY, Math.Min(available.Width, _actionsWidth), RowHeight));
        double titleX = wrapped ? 0 : _actionsWidth + Gap;
        double titleWidth = wrapped ? available.Width : Math.Max(0, available.Width - _actionsWidth - Gap);
        InternalChildren[1].Arrange(new Rect(titleX, 0, titleWidth, RowHeight));
        return available;
    }
}

/// <summary>The title owns its wash: natural content width plus a bounded falloff, without a size observer.</summary>
public sealed class DashboardGroupTitlePanel : Panel
{
    private double _naturalWidth = double.NaN;
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(object), typeof(DashboardGroupTitlePanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, MetricsChanged));
    public static readonly DependencyProperty ItemCountProperty = DependencyProperty.Register(nameof(ItemCount), typeof(int), typeof(DashboardGroupTitlePanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure, MetricsChanged));
    public object? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public int ItemCount { get => (int)GetValue(ItemCountProperty); set => SetValue(ItemCountProperty, value); }
    private static void MetricsChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        => ((DashboardGroupTitlePanel)target)._naturalWidth = double.NaN;
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); _naturalWidth = double.NaN; InvalidateMeasure(); }
    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count != 2) return default;
        InternalChildren[0].Measure(default(Size));
        var title = InternalChildren[1];
        // A short title keeps the same content width while the sidebar resizes the
        // page. Do not reformat every group's text at each intermediate page width.
        // Descendant/content invalidation still remeasures its natural size; genuinely
        // constrained titles are measured in their available slot for native trimming.
        if (double.IsNaN(_naturalWidth) || !title.IsMeasureValid)
        {
            title.Measure(new Size(double.PositiveInfinity, available.Height));
            _naturalWidth = title.DesiredSize.Width;
        }
        double roundingRoom = UseLayoutRounding ? 1 / VisualTreeHelper.GetDpi(this).DpiScaleX : 0;
        title.Measure(new Size(Math.Min(Math.Max(0, available.Width - roundingRoom), _naturalWidth), available.Height));
        // Leave a device pixel for the outer slot's rounding; title columns retain
        // fractional text metrics so the natural name does not lose its last glyph.
        return new Size(title.DesiredSize.Width + roundingRoom, title.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size available)
    {
        if (InternalChildren.Count != 2) return available;
        double roundingRoom = UseLayoutRounding ? 1 / VisualTreeHelper.GetDpi(this).DpiScaleX : 0;
        var width = Math.Min(available.Width, InternalChildren[1].DesiredSize.Width + roundingRoom);
        InternalChildren[0].Arrange(new Rect(0, 0, Math.Min(available.Width, Math.Min(280, width + 32)), available.Height));
        InternalChildren[1].Arrange(new Rect(0, 0, width, available.Height));
        return available;
    }
}
