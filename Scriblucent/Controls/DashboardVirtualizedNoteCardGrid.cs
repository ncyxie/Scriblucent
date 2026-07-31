using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WpfToolkit.Controls;

namespace Scriblucent.Controls;

/// <summary>
/// Virtualized notes grid/list with estimated per-card heights for layout and scrolling.
/// </summary>
public sealed class DashboardVirtualizedNoteCardGrid : DashboardVirtualizedCardGrid
{
    private const double GridCellWidth = 244;
    private const double GridCellHeight = 164;
    private const int PageVirtualizedUniformThreshold = 48;

    public static readonly DependencyProperty IsListLayoutProperty = DependencyProperty.Register(
        nameof(IsListLayout),
        typeof(bool),
        typeof(DashboardVirtualizedNoteCardGrid),
        new PropertyMetadata(false, OnIsListLayoutChanged));

    private NoteCardItemSizeProvider _sizeProvider = new(isListLayout: false);
    private double _cachedExtentWidth = double.NaN;
    private int _cachedExtentItemCount = -1;
    private double _cachedExtentHeight;
    private bool? _wrapPanelUniformMode;
    private bool _wrapPanelConfigScheduled;

    public bool IsListLayout
    {
        get => (bool)GetValue(IsListLayoutProperty);
        set => SetValue(IsListLayoutProperty, value);
    }

    public DashboardVirtualizedNoteCardGrid()
    {
        ApplyNoteLayout(isListLayout: false);
        Loaded += (_, _) => EnsureWrapPanelConfigured();
        ItemContainerGenerator.StatusChanged += (_, _) => ScheduleWrapPanelConfiguration();
    }

    private static void OnIsListLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DashboardVirtualizedNoteCardGrid grid)
            grid.ApplyNoteLayout((bool)e.NewValue);
    }

    private void ApplyNoteLayout(bool isListLayout)
    {
        _sizeProvider = new NoteCardItemSizeProvider(isListLayout);
        Layout = isListLayout ? DashboardCardGridLayout.VerticalList : DashboardCardGridLayout.WrapGrid;
        _wrapPanelUniformMode = null;

        if (!isListLayout)
            EnsureWrapPanelConfigured();
    }

    private void ScheduleWrapPanelConfiguration()
    {
        if (_wrapPanelConfigScheduled)
            return;

        _wrapPanelConfigScheduled = true;
        Dispatcher.BeginInvoke(() =>
        {
            _wrapPanelConfigScheduled = false;
            EnsureWrapPanelConfigured();
        }, DispatcherPriority.Background);
    }

    private void EnsureWrapPanelConfigured()
    {
        if (IsListLayout || ResolveItemsPanel() is not VirtualizingWrapPanel wrapPanel)
            return;

        var shouldUseUniform = ShouldUseUniformWrapLayout();
        if (_wrapPanelUniformMode == shouldUseUniform)
            return;

        ConfigureWrapPanel(wrapPanel, shouldUseUniform);
        _wrapPanelUniformMode = shouldUseUniform;
    }

    private void ConfigureWrapPanel(VirtualizingWrapPanel wrapPanel, bool useUniformLayout)
    {
        wrapPanel.SpacingMode = SpacingMode.None;
        wrapPanel.FallbackItemSize = new Size(GridCellWidth, GridCellHeight);

        if (useUniformLayout)
        {
            wrapPanel.AllowDifferentSizedItems = false;
            wrapPanel.ItemSizeProvider = null;
            wrapPanel.ItemSize = new Size(GridCellWidth, GridCellHeight);
            return;
        }

        wrapPanel.AllowDifferentSizedItems = true;
        wrapPanel.ItemSizeProvider = _sizeProvider;
        wrapPanel.ItemSize = new Size(GridCellWidth, GridCellHeight);
    }

    private bool ShouldUseUniformWrapLayout()
        => !IsListLayout
            && (Items.Count >= LargeListItemThreshold
                || Items.Count >= PageVirtualizedUniformThreshold && IsHostedInPageVirtualizedDashboard());

    internal override bool UsesUniformPanelLayout()
        => ShouldUseUniformWrapLayout() || base.UsesUniformPanelLayout();

    private bool IsHostedInPageVirtualizedDashboard()
    {
        var ancestor = Parent;
        while (ancestor is not null)
        {
            if (ancestor is DashboardVirtualizationHost { HostMode: DashboardVirtualizationHostMode.PageVirtualized })
                return true;

            ancestor = LogicalTreeHelper.GetParent(ancestor) ?? VisualTreeHelper.GetParent(ancestor);
        }

        return false;
    }

    internal double EstimateLayoutExtent(double width)
    {
        if (Items.Count == 0 || width <= 0)
            return 0;

        if (!double.IsNaN(_cachedExtentWidth)
            && Math.Abs(_cachedExtentWidth - width) < 0.5
            && _cachedExtentItemCount == Items.Count)
            return _cachedExtentHeight;

        const double listRowHeight = 96;

        double extent;
        if (IsListLayout)
        {
            extent = Items.Count * listRowHeight;
        }
        else if (ShouldUseUniformWrapLayout())
        {
            // Only take the cheap, uniform-row-height shortcut here when the panel's real
            // arrangement is also going to be uniform (ShouldUseUniformWrapLayout, which for a
            // Static/group host requires >= LargeListItemThreshold, not just this class's lower
            // PageVirtualizedUniformThreshold - that lower bound only applies when this grid is
            // actually hosted in a PageVirtualized dashboard). Using the flat 48-item threshold
            // unconditionally here - as this used to do - made the estimate switch to a uniform
            // GridCellHeight-per-row approximation well before a Static-mode group's real layout
            // did (which still uses each card's true per-item height up to 200 items), so any
            // group between 48 and 199 items containing taller-than-average cards got an
            // underestimated contentExtent. DashboardVirtualizationHost.ComputeVisibleSlice
            // clamps the renderable slice to that estimate, so the shortfall between the
            // estimate and the group's real (larger) measured extent became a permanent blank
            // gap at the tail of the group that scrolling could never reach. Confirmed via
            // hostMeasureState showing a committed extent (from real per-item measurement)
            // hundreds of pixels taller than the estimate driving measureSliceStatic's clamp.
            var perRow = Math.Max(1, (int)Math.Floor(width / GridCellWidth));
            var rows = (int)Math.Ceiling(Items.Count / (double)perRow);
            extent = rows * GridCellHeight;
        }
        else
        {
            var perRow = Math.Max(1, (int)Math.Floor(width / GridCellWidth));
            var rowHeights = new double[(int)Math.Ceiling(Items.Count / (double)perRow)];
            for (var i = 0; i < Items.Count; i++)
            {
                var row = i / perRow;
                rowHeights[row] = Math.Max(rowHeights[row], _sizeProvider.GetSizeForItem(Items[i]!).Height);
            }

            extent = rowHeights.Sum();
        }

        _cachedExtentWidth = width;
        _cachedExtentItemCount = Items.Count;
        _cachedExtentHeight = extent;
        return extent;
    }

    protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _cachedExtentItemCount = -1;

        var previousUniform = _wrapPanelUniformMode;
        var nextUniform = ShouldUseUniformWrapLayout();
        if (previousUniform != nextUniform)
            _wrapPanelUniformMode = null;

        base.OnItemsChanged(e);
        ScheduleWrapPanelConfiguration();
    }
}
