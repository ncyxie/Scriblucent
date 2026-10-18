using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>Retains a group's layout slot without rendering its distant header and decoration.</summary>
public sealed class DashboardGroupCard : Border
{
    private bool _viewportActive = true;
    internal bool IsViewportActive => _viewportActive;
    static DashboardGroupCard() => VisibilityProperty.OverrideMetadata(typeof(DashboardGroupCard),
        new FrameworkPropertyMetadata(Visibility.Visible, null, (d, value) =>
            value is Visibility.Visible && !((DashboardGroupCard)d)._viewportActive ? Visibility.Hidden : value));
    internal void SetViewportActive(bool active)
    {
        if (_viewportActive == active) return;
        _viewportActive = active;
        CoerceValue(VisibilityProperty);
        if (active) InvalidateVisual();
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_viewportActive) base.OnRender(drawingContext);
    }
}

/// <summary>Measures distant group bodies from their data extents, keeping header layout local to the viewport.</summary>
public sealed class DashboardGroupLayoutPanel : Panel
{
    private ScrollViewer? _viewer;
    private DashboardVirtualizationHost? _bodyHost;
    private DashboardGroupCard? _card;
    private bool _arranged, _active, _refreshQueued;
    private double _headerWidth = double.NaN, _headerHeight;

    public DashboardGroupLayoutPanel()
    {
        Loaded += (_, _) => { EnsureViewer(); QueueRefresh(); };
        Unloaded += (_, _) =>
        {
            DetachViewer();
            _card = null;
            _arranged = false;
            if (_bodyHost is not null) _bodyHost.SetGroupViewportOwner(null);
        };
    }

    private void EnsureViewer()
    {
        if (_card is null)
            for (var ancestor = VisualTreeHelper.GetParent(this); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is DashboardGroupCard card) { _card = card; break; }
        var viewer = DashboardMotionViewport.FindScrollViewer(this);
        if (!ReferenceEquals(viewer, _viewer))
        {
            DetachViewer(); _viewer = viewer;
            if (_viewer is not null) { _viewer.ScrollChanged += Scrolled; _viewer.SizeChanged += Sized; }
        }
        if (InternalChildren.Count == 2 && InternalChildren[1] is LayoutRevealHost { Child: DashboardVirtualizationHost host })
        {
            _bodyHost = host;
            host.SetGroupViewportOwner(this);
        }
    }
    private void DetachViewer()
    {
        if (_viewer is not null) { _viewer.ScrollChanged -= Scrolled; _viewer.SizeChanged -= Sized; }
        _viewer = null;
    }
    private void Scrolled(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, _viewer)) RefreshViewport();
    }
    private void Sized(object sender, SizeChangedEventArgs e) => QueueRefresh();
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() => { _refreshQueued = false; if (IsLoaded) RefreshViewport(); }, DispatcherPriority.Render);
    }

    internal void RefreshViewport()
    {
        bool active = false;
        if (_arranged && _viewer is { ViewportHeight: > 0, Content: UIElement content })
        {
            try
            {
                var top = TranslatePoint(new Point(), content).Y;
                var offset = _viewer.VerticalOffset;
                if (SmoothScrollCompositor.TryGetVerticalDrift(_viewer, out var drift)) offset += drift;
                active = top + Math.Max(ActualHeight, DesiredSize.Height) >= offset - 360
                    && top <= offset + _viewer.ViewportHeight + 600;
            }
            catch (InvalidOperationException) { /* The viewer's old content can be detaching. */ }
        }
        _card?.SetViewportActive(active);
        _bodyHost?.SetGroupViewportActive(active);
        if (_active == active) return;
        _active = active;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count != 2) return default;
        EnsureViewer(); RefreshViewport();
        var header = InternalChildren[0];
        var width = double.IsFinite(available.Width) ? Math.Max(0, available.Width) : 800;
        // Hidden titles still react to changed content, but do not reflow per pixel.
        // Crossing the compact/two-row threshold must update their reserved height.
        var measureWidth = _active || double.IsNaN(_headerWidth) || (_headerWidth < DashboardGroupHeaderPanel.CompactWidth) != (width < DashboardGroupHeaderPanel.CompactWidth)
            ? width : _headerWidth;
        header.Measure(new Size(measureWidth, double.PositiveInfinity));
        _headerWidth = measureWidth; _headerHeight = header.DesiredSize.Height;
        InternalChildren[1].Measure(new Size(width, double.PositiveInfinity));
        return new Size(width, _headerHeight + InternalChildren[1].DesiredSize.Height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        if (_active) InternalChildren[0].Arrange(new Rect(0, 0, finalSize.Width, _headerHeight));
        InternalChildren[1].Arrange(new Rect(0, _headerHeight, finalSize.Width, InternalChildren[1].DesiredSize.Height));
        if (!_arranged) { _arranged = true; QueueRefresh(); }
        return finalSize;
    }
}
