using System.Collections.Specialized;
using System.ComponentModel;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using WpfToolkit.Controls;

namespace Scriblucent.Controls;

public enum DashboardCardGridLayout
{
    WrapGrid,
    VerticalList
}

/// <summary>
/// Virtualized dashboard listing grid backed by <see cref="VirtualizingWrapPanel"/>
/// or <see cref="VirtualizingStackPanel"/> when hosted inside <see cref="DashboardVirtualizationHost"/>.
/// </summary>
public class DashboardVirtualizedCardGrid : ItemsControl
{
    internal const int LargeListItemThreshold = 200;
    private const double VirtualizationCacheBeforePixels = 360;
    private const double VirtualizationCacheAfterPixels = 540;
    private const double CompactVirtualizationCacheBeforePixels = 220;
    private const double CompactVirtualizationCacheAfterPixels = 320;
    private const int CompactVirtualizationItemThreshold = 48;
    private const double StablePanelExtentEpsilonPixels = 1;

    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout),
        typeof(DashboardCardGridLayout),
        typeof(DashboardVirtualizedCardGrid),
        new PropertyMetadata(DashboardCardGridLayout.WrapGrid, OnLayoutChanged));

    public static readonly DependencyProperty ItemCellSizeProperty = DependencyProperty.Register(
        nameof(ItemCellSize),
        typeof(Size),
        typeof(DashboardVirtualizedCardGrid),
        new PropertyMetadata(Size.Empty, OnItemCellSizeChanged));

    private double _pageVerticalOffset;
    private double _pageViewportHeight;
    private int _lastMeasuredItemCount = -1;
    private double _lastPanelExtentHeight = double.NaN;
    private MainViewModel? _mainViewModel;
    private bool _isSyncingMassSelectChrome;
    private bool _addMotionLayoutActive;
    private bool _interactiveSweepDeferred;
    private VirtualizationMode _savedVirtualizationMode = VirtualizationMode.Recycling;
    private DashboardMotionScope? _cachedMotionScope;
    private bool _motionScopeResolved;

    public DashboardCardGridLayout Layout
    {
        get => (DashboardCardGridLayout)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public Size ItemCellSize
    {
        get => (Size)GetValue(ItemCellSizeProperty);
        set => SetValue(ItemCellSizeProperty, value);
    }

    public DashboardVirtualizedCardGrid()
    {
        OverridesDefaultStyle = true;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        Focusable = false;
        IsTabStop = false;
        ClipToBounds = false;
        KeyboardNavigation.SetDirectionalNavigation(this, KeyboardNavigationMode.Once);
        ItemContainerStyle = CreateItemContainerStyle();

        ApplyVirtualizationTemplate();
        ApplyLayoutPanel(Layout);

        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            DetachMainViewModel();
            _motionScopeResolved = false;
            _cachedMotionScope = null;
        };
        ItemContainerGenerator.StatusChanged += OnItemContainerGeneratorStatusChanged;
        SizeChanged += OnGridSizeChanged;
    }

    internal void ApplyPageViewport(double verticalOffset, double viewportHeight)
    {
        _pageVerticalOffset = Math.Max(0, verticalOffset);
        _pageViewportHeight = Math.Max(0, viewportHeight);
        ApplyPageViewportToPanel();
    }

    internal void ResetPageViewport()
    {
        _pageVerticalOffset = 0;
        _pageViewportHeight = 0;
        ApplyPageViewportToPanel();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureItemsPanelMaterialized(availableSize);

        var width = double.IsPositiveInfinity(availableSize.Width) ? ActualWidth : availableSize.Width;
        if (width <= 0)
            width = Math.Max(ActualWidth, 1);

        var height = _pageViewportHeight > 0
            ? _pageViewportHeight
            : double.IsPositiveInfinity(availableSize.Height) ? 0 : availableSize.Height;

        if (height <= 0)
            height = 1;

        // VirtualizingWrapPanel.SetVerticalOffset clamps against the panel's OWN ExtentHeight/
        // ViewportHeight, but those fields are only refreshed inside its MeasureOverride AFTER
        // RealizeAndVirtualizeItems. On a pass where the item count just jumped (duplicate-all),
        // a single Apply-then-Measure cycle realizes the wrong band against the stale extent;
        // Arrange then re-applies the true offset and shifts that wrong band off-screen - blank
        // reserved space. Two-phase measure when a non-zero page offset is requested:
        // 1) measure once so ExtentHeight reflects the current item count
        // 2) re-apply the target offset against the fresh extent
        // 3) measure again so realization uses the real offset
        ApplyPageViewportToPanel();
        var result = base.MeasureOverride(new Size(width, height));

        if (_pageVerticalOffset > 0.5)
        {
            var itemCount = Items.Count;
            var panel = ResolveItemsPanel() as IScrollInfo;
            var panelExtent = panel?.ExtentHeight ?? double.NaN;
            var offsetMatches = panel is not null
                && Math.Abs(panel.VerticalOffset - _pageVerticalOffset) <= 0.5;
            var countStable = itemCount == _lastMeasuredItemCount;
            var extentStable = !double.IsNaN(_lastPanelExtentHeight)
                && !double.IsNaN(panelExtent)
                && Math.Abs(panelExtent - _lastPanelExtentHeight) <= StablePanelExtentEpsilonPixels;
            var skipPhase2 = offsetMatches && countStable && extentStable;

            if (!skipPhase2)
            {
                if (ScrollPerfDiag.Enabled && !offsetMatches && panel is not null)
                {
                    ScrollPerfDiag.Log(
                        $"panelOffsetClampCorrected\trequested={_pageVerticalOffset:F0}\tgotClampedTo={panel.VerticalOffset:F0}\titems={itemCount}\tscope={ResolveHostingMotionScope()}");
                }

                ApplyPageViewportToPanel();
                result = base.MeasureOverride(new Size(width, height));

                if (ScrollPerfDiag.Enabled
                    && ResolveItemsPanel() is IScrollInfo afterTwoPhase
                    && Math.Abs(afterTwoPhase.VerticalOffset - _pageVerticalOffset) > 0.5)
                {
                    ScrollPerfDiag.Log(
                        $"panelOffsetMismatchAfterTwoPhase\trequested={_pageVerticalOffset:F0}\tgot={afterTwoPhase.VerticalOffset:F0}\textent={afterTwoPhase.ExtentHeight:F0}\tviewport={afterTwoPhase.ViewportHeight:F0}\titems={Items.Count}\tscope={ResolveHostingMotionScope()}");
                }
            }
        }

        if (HasItems && height > 1 && !HasRealizedContainers())
            result = RemeasureForRealization(width, height);

        _lastMeasuredItemCount = Items.Count;
        if (ResolveItemsPanel() is IScrollInfo measuredPanel)
            _lastPanelExtentHeight = measuredPanel.ExtentHeight;

        return result;
    }

    private Size RemeasureForRealization(double width, double height)
    {
        if (_pageVerticalOffset < 1)
            ApplyPageViewport(0, height);
        else
            ApplyPageViewport(_pageVerticalOffset, height);

        ApplyPageViewportToPanel();
        return base.MeasureOverride(new Size(width, height));
    }

    internal bool HasRealizedContainers()
    {
        if (ResolveItemsPanel() is not Panel panel)
            return false;

        return panel.Children.Count > 0;
    }

    internal void SyncLayoutForMotion()
        => UpdateLayout();

    internal void SyncVirtualizationCache()
    {
        var compact = Items.Count >= CompactVirtualizationItemThreshold;
        var fallbackBefore = compact ? CompactVirtualizationCacheBeforePixels : VirtualizationCacheBeforePixels;
        var fallbackAfter = compact ? CompactVirtualizationCacheAfterPixels : VirtualizationCacheAfterPixels;
        var before = ResolveColumnAwareCachePixels(fallbackBefore);
        var after = ResolveColumnAwareCachePixels(fallbackAfter);
        VirtualizingPanel.SetCacheLength(this, new VirtualizationCacheLength(before, after));
    }

    // Notes never sets ItemCellSize (it configures VirtualizingWrapPanel.ItemSize directly from
    // its own private 244/164 constants), so this mirrors the same fallback idiom already used
    // for it in DashboardMotionEngine.ResolveGridReflowMetrics.
    private double ResolveEffectiveCellWidth()
        => this is DashboardVirtualizedNoteCardGrid ? 244 : ItemCellSize.Width > 0 ? ItemCellSize.Width : 244;

    private double ResolveEffectiveCellHeight()
        => this is DashboardVirtualizedNoteCardGrid ? 164 : ItemCellSize.Height > 0 ? ItemCellSize.Height : 164;

    /// <summary>
    /// A maximized window packs more columns per row, so a fixed pixel cache margin would
    /// realize proportionally more off-screen cards as it widens. Scaling the margin down as
    /// columns increase keeps compositing cost roughly window-size-invariant (mirrors
    /// DashboardVirtualizationHost.ResolveViewportCachePixels).
    /// </summary>
    private double ResolveColumnAwareCachePixels(double fallbackPixels)
    {
        if (Layout != DashboardCardGridLayout.WrapGrid)
            return fallbackPixels;

        var cellWidth = ResolveEffectiveCellWidth();
        var cellHeight = ResolveEffectiveCellHeight();
        if (cellWidth <= 0 || cellHeight <= 0)
            return fallbackPixels;

        var width = ActualWidth;
        if (width <= 0 && Parent is FrameworkElement parent)
            width = parent.ActualWidth;
        if (width <= 0)
            return fallbackPixels;

        var columns = Math.Max(1, Math.Floor(width / cellWidth));
        var targetExtraCards = fallbackPixels / cellHeight;
        var rows = Math.Max(1, targetExtraCards / columns);
        var budgeted = rows * cellHeight;
        return Math.Clamp(budgeted, fallbackPixels * 0.4, fallbackPixels * 2.0);
    }

    private void OnGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || Layout != DashboardCardGridLayout.WrapGrid)
            return;

        var cellWidth = ResolveEffectiveCellWidth();
        if (cellWidth <= 0)
            return;

        var previousColumns = Math.Max(1, Math.Floor(e.PreviousSize.Width / cellWidth));
        var newColumns = Math.Max(1, Math.Floor(e.NewSize.Width / cellWidth));
        if (previousColumns == newColumns)
            return;

        SyncVirtualizationCache();
    }

    /// <summary>Stop container recycling during add so sorted inserts bind to the correct slot.</summary>
    internal void BeginAddMotionLayout()
    {
        if (_addMotionLayoutActive)
            return;

        _addMotionLayoutActive = true;
        _savedVirtualizationMode = VirtualizingPanel.GetVirtualizationMode(this);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Standard);
        InvalidateMeasure();
    }

    internal void EndAddMotionLayout()
    {
        if (!_addMotionLayoutActive)
            return;

        _addMotionLayoutActive = false;
        VirtualizingPanel.SetVirtualizationMode(this, _savedVirtualizationMode);
        InvalidateMeasure();
    }

    private void EnsureItemsPanelMaterialized(Size availableSize)
    {
        if (ResolveItemsPanel() is not null)
            return;

        ApplyTemplate();

        var width = double.IsPositiveInfinity(availableSize.Width) ? Math.Max(ActualWidth, 1) : availableSize.Width;
        if (width <= 0)
            width = 1;

        base.MeasureOverride(new Size(width, 1));
    }

    private void ApplyPageViewportToPanel()
    {
        if (ResolveItemsPanel() is not IScrollInfo scrollInfo)
            return;

        // Hybrid contract: page ScrollViewer owns scroll; this panel should not have a
        // ScrollOwner. If one appears, nested scroll or template regress is fighting the host.
        if (ScrollPerfDiag.Enabled
            && scrollInfo is VirtualizingPanelBase { ScrollOwner: not null } owned)
        {
            ScrollPerfDiag.Log(
                $"panelUnexpectedScrollOwner\townerType={owned.ScrollOwner.GetType().Name}\trequestedOffset={_pageVerticalOffset:F0}\titems={Items.Count}\tscope={ResolveHostingMotionScope()}");
        }

        scrollInfo.SetVerticalOffset(_pageVerticalOffset);

        if (ResolveItemsPanel() is VirtualizingPanelBase panelBase)
        {
            panelBase.CanVerticallyScroll = true;
            panelBase.CanHorizontallyScroll = false;
        }
    }

    internal double GetExtentHeight()
    {
        var estimatedExtent = EstimateVariableLayoutExtent();
        var uniformExtent = ComputeUniformExtentFallback();
        var panelExtent = ResolvePanelExtentHeight();

        if (Items.Count >= LargeListItemThreshold && UsesUniformPanelLayout())
            return Math.Max(estimatedExtent, Math.Max(uniformExtent, panelExtent));

        return Math.Max(panelExtent, Math.Max(uniformExtent, estimatedExtent));
    }

    internal double GetPanelExtentHeight()
        => ResolvePanelExtentHeight();

    internal void ResetVirtualizationLayoutCache()
    {
        if (ResolveItemsPanel() is VirtualizingWrapPanel wrapPanel)
            wrapPanel.ClearItemSizeCache();
    }

    internal virtual bool UsesUniformPanelLayout()
    {
        if (this is DashboardVirtualizedNoteCardGrid { IsListLayout: false } && Items.Count >= LargeListItemThreshold)
            return true;

        return ResolveItemsPanel() is VirtualizingWrapPanel { AllowDifferentSizedItems: false };
    }

    private double ResolvePanelExtentHeight()
        => ResolveItemsPanel() switch
        {
            VirtualizingWrapPanel wrapPanel => wrapPanel.ExtentHeight,
            VirtualizingStackPanel stackPanel => ((IScrollInfo)stackPanel).ExtentHeight,
            _ => 0
        };

    private double EstimateVariableLayoutExtent()
    {
        if (this is not DashboardVirtualizedNoteCardGrid noteGrid)
            return 0;

        var width = ActualWidth;
        if (width <= 0 && Parent is FrameworkElement parent)
            width = parent.ActualWidth;

        return width > 0 ? noteGrid.EstimateLayoutExtent(width) : 0;
    }

    private double ComputeUniformExtentFallback()
    {
        if (ItemCellSize is not { Width: > 0, Height: > 0 } cellSize || Items.Count == 0)
            return 0;

        if (ResolveItemsPanel() is VirtualizingWrapPanel { AllowDifferentSizedItems: true })
            return 0;

        var width = ActualWidth;
        if (width <= 0 && Parent is FrameworkElement parent)
            width = parent.ActualWidth;

        if (width <= 0)
            return 0;

        var perRow = Math.Max(1, (int)Math.Floor(width / cellSize.Width));
        var rows = (int)Math.Ceiling(Items.Count / (double)perRow);
        return rows * cellSize.Height;
    }

    internal Panel? ResolveItemsPanel()
    {
        var count = VisualTreeHelper.GetChildrenCount(this);
        for (var i = 0; i < count; i++)
        {
            if (FindItemsHostPanel(VisualTreeHelper.GetChild(this, i)) is { } panel)
                return panel;
        }

        return null;
    }

    private static Panel? FindItemsHostPanel(DependencyObject node)
    {
        if (node is Panel { IsItemsHost: true } panel)
            return panel;

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            if (FindItemsHostPanel(VisualTreeHelper.GetChild(node, i)) is { } nested)
                return nested;
        }

        return null;
    }

    internal void EnsureRealizedCardsInteractive()
    {
        if (Items.Count == 0)
            return;

        if (ResolveItemsPanel() is not { } panel)
            return;

        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is DependencyObject container)
                EnsureContainerCardInteractive(container);
        }
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);

        if (DashboardMotionEngine.IsPinReorderActive)
            return;

        if (e.Action is not NotifyCollectionChangedAction.Move)
            SyncVirtualizationCache();

        if (e.Action is NotifyCollectionChangedAction.Move)
        {
            if (Items.Count == 0)
                ResetPageViewport();

            return;
        }

        if (e.Action is NotifyCollectionChangedAction.Reset)
        {
            NotifyHostMutationRefresh();
            return;
        }

        if (Items.Count == 0)
            ResetPageViewport();

        NotifyHostMutationRefresh();

        if (DashboardMotionEngine.ShouldDeferVirtualizationRefresh(ResolveHostingMotionScope()))
            return;

        if (DashboardScrollRenderGate.IsSmoothScrollRendering(this))
        {
            _interactiveSweepDeferred = true;
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            EnsureRealizedCardsInteractive();
            if (Window.GetWindow(this)?.DataContext is MainViewModel { IsMassSelectMode: true })
                SyncMassSelectChrome();
        }, DispatcherPriority.Loaded);
    }

    internal void FlushDeferredInteractiveSweep()
    {
        if (!_interactiveSweepDeferred)
            return;

        _interactiveSweepDeferred = false;
        EnsureRealizedCardsInteractive();

        if (IsMassSelectModeActive())
            SyncMassSelectChrome();
    }

    private DashboardMotionScope? ResolveHostingMotionScope()
    {
        if (_motionScopeResolved)
            return _cachedMotionScope;

        var ancestor = Parent;
        while (ancestor is not null)
        {
            if (ancestor is DashboardVirtualizationHost host)
            {
                _cachedMotionScope = host.MotionScope;
                _motionScopeResolved = true;
                return _cachedMotionScope;
            }

            ancestor = LogicalTreeHelper.GetParent(ancestor) ?? VisualTreeHelper.GetParent(ancestor);
        }

        return null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachMainViewModel();

        if (ResolveItemsPanel() is VirtualizingWrapPanel wrapPanel)
        {
            if (!ItemCellSize.IsEmpty)
                wrapPanel.ItemSize = ItemCellSize;

            // Set the virtualization attached properties on the PANEL itself (not just the
            // ItemsControl, which the toolkit panel does not consult): the panel then reuses
            // containers instead of regenerating them, so scroll passes only rebind and
            // re-measure existing containers instead of paying a full container load per item.
            VirtualizingPanel.SetIsVirtualizing(wrapPanel, true);
            VirtualizingPanel.SetVirtualizationMode(wrapPanel, VirtualizationMode.Recycling);
            VirtualizingPanel.SetScrollUnit(wrapPanel, ScrollUnit.Pixel);
            VirtualizingPanel.SetCacheLengthUnit(wrapPanel, VirtualizationCacheLengthUnit.Pixel);
        }

        EnsureRealizedCardsInteractive();
        if (IsMassSelectModeActive())
            SyncMassSelectChrome();
    }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        if (!ScrollPerfDiag.Enabled)
        {
            PrepareContainerForItemCore(element, item);
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        PrepareContainerForItemCore(element, item);
        if (ScrollPerfDiag.Enabled)
            ScrollPerfDiag.LogContainerPrepare(stopwatch.Elapsed.TotalMilliseconds);
    }

    private void PrepareContainerForItemCore(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        DashboardScrollRenderGate.TryStripContainerShadow(element);
        MassSelectHighlightBehavior.PrepareContainer(element);
        DashboardMassSelectCheckBoxBehavior.SyncContainer(element);
        IndicatorRevealMotionBehavior.SyncContainer(element);
        if (!DashboardScrollRenderGate.IsSmoothScrollRendering(element))
        {
            Dispatcher.BeginInvoke(
                () => IndicatorRevealMotionBehavior.SyncContainer(element),
                DispatcherPriority.Loaded);
        }

        EnsureContainerCardInteractive(element);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        ResetContainerMotion(element);
        MassSelectHighlightBehavior.ResetContainer(element);
        DashboardMassSelectCheckBoxBehavior.HideAllInContainer(element);
        IndicatorRevealMotionBehavior.ResetContainer(element);
        base.ClearContainerForItemOverride(element, item);
    }

    private void AttachMainViewModel()
    {
        if (Window.GetWindow(this)?.DataContext is not MainViewModel mainViewModel)
            return;

        if (ReferenceEquals(_mainViewModel, mainViewModel))
            return;

        DetachMainViewModel();
        _mainViewModel = mainViewModel;
        _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
    }

    private void DetachMainViewModel()
    {
        if (_mainViewModel is null)
            return;

        _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        _mainViewModel = null;
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsMassSelectMode))
            SyncMassSelectChrome();
    }

    internal void SyncMassSelectChrome()
    {
        if (_isSyncingMassSelectChrome || !IsMassSelectModeActive())
            return;

        _isSyncingMassSelectChrome = true;
        try
        {
            SyncMassSelectChromeForRealizedContainers();
        }
        finally
        {
            _isSyncingMassSelectChrome = false;
        }
    }

    private void SyncMassSelectChromeForRealizedContainers()
    {
        if (ResolveItemsPanel() is not { } panel)
            return;

        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is DependencyObject container)
                SyncMassSelectContainer(container);
        }
    }

    internal void SyncMassSelectContainer(DependencyObject container)
    {
        MassSelectHighlightBehavior.SyncContainer(container);
        DashboardMassSelectCheckBoxBehavior.SyncContainer(container);
    }

    private bool IsMassSelectModeActive()
        => _mainViewModel?.IsMassSelectMode == true
           || Window.GetWindow(this)?.DataContext is MainViewModel { IsMassSelectMode: true };

    private void OnItemContainerGeneratorStatusChanged(object? sender, EventArgs e)
    {
        if (ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
            return;

        EnsureRealizedCardsInteractive();

        if (Window.GetWindow(this)?.DataContext is MainViewModel { IsMassSelectMode: true })
            SyncMassSelectChrome();
    }

    private void NotifyHostMutationRefresh()
    {
        if (TryFindVirtualizationHost() is { } host)
            host.RequestMutationRefresh();
    }

    private DashboardVirtualizationHost? TryFindVirtualizationHost()
    {
        var ancestor = Parent;
        while (ancestor is not null)
        {
            if (ancestor is DashboardVirtualizationHost host)
                return host;

            ancestor = LogicalTreeHelper.GetParent(ancestor) ?? VisualTreeHelper.GetParent(ancestor);
        }

        return null;
    }

    private void EnsureContainerCardInteractive(DependencyObject container)
    {
        var scope = ResolveHostingMotionScope();

        if (DashboardScrollRenderGate.ResolveMotionCard(container) is { } card
            && CardSurfaceMotionBehavior.GetIsEnabled(card))
        {
            switch (CardSurfaceMotionBehavior.GetProfile(card))
            {
                case CardSurfaceMotionProfile.Listing:
                    CardSurfaceMotionBehavior.PrepareRecycledListingCard(card);
                    break;
                case CardSurfaceMotionProfile.NoteCard:
                    CardSurfaceMotionBehavior.PrepareRecycledNoteCard(card);
                    break;
                default:
                    CardSurfaceMotionBehavior.EnsureCardVisible(card);
                    break;
            }

            IndicatorRevealMotionBehavior.SyncContainer(card);
            DashboardMotionEngine.NotifyItemContainerRealized(card, scope);
            return;
        }

        if (container is FrameworkElement containerElement)
        {
            CardSurfaceMotionBehavior.EnsureCardVisible(containerElement);
            DashboardMotionEngine.NotifyItemContainerRealized(containerElement, scope);
        }
    }

    private static void ResetContainerMotion(DependencyObject container)
    {
        if (DashboardScrollRenderGate.ResolveMotionCard(container) is FrameworkElement card
            && CardSurfaceMotionBehavior.GetIsEnabled(card))
            CardSurfaceMotionBehavior.Cleanup(card);
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DashboardVirtualizedCardGrid grid)
            grid.ApplyLayoutPanel((DashboardCardGridLayout)e.NewValue);
    }

    private static void OnItemCellSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DashboardVirtualizedCardGrid grid && grid.ResolveItemsPanel() is VirtualizingWrapPanel wrapPanel)
            wrapPanel.ItemSize = (Size)e.NewValue;
    }

    private static Style CreateItemContainerStyle()
    {
        var style = new Style(typeof(ContentPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch));
        return style;
    }

    private void ApplyVirtualizationTemplate()
    {
        const string template = """
            <ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                             xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                             TargetType='ItemsControl'>
                <Border Background='{TemplateBinding Panel.Background}'
                        BorderBrush='{TemplateBinding Border.BorderBrush}'
                        BorderThickness='{TemplateBinding Border.BorderThickness}'
                        Padding='{TemplateBinding Control.Padding}'>
                    <ItemsPresenter SnapsToDevicePixels='{TemplateBinding UIElement.SnapsToDevicePixels}'/>
                </Border>
            </ControlTemplate>
            """;

        Template = (ControlTemplate)XamlReader.Parse(template);

        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLengthUnit(this, VirtualizationCacheLengthUnit.Pixel);
        SyncVirtualizationCache();
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(this, true);
        ScrollViewer.SetCanContentScroll(this, true);
    }

    private void ApplyLayoutPanel(DashboardCardGridLayout layout)
    {
        if (layout == DashboardCardGridLayout.VerticalList)
        {
            var stackFactory = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
            stackFactory.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Vertical);
            ItemsPanel = new ItemsPanelTemplate(stackFactory);
            return;
        }

        var wrapFactory = new FrameworkElementFactory(typeof(VirtualizingWrapPanel));
        wrapFactory.SetValue(VirtualizingWrapPanel.OrientationProperty, Orientation.Horizontal);
        wrapFactory.SetValue(VirtualizingWrapPanel.SpacingModeProperty, SpacingMode.None);
        wrapFactory.SetValue(VirtualizingWrapPanel.StretchItemsProperty, false);

        if (!ItemCellSize.IsEmpty)
            wrapFactory.SetValue(VirtualizingWrapPanel.ItemSizeProperty, ItemCellSize);

        ItemsPanel = new ItemsPanelTemplate(wrapFactory);
    }
}
