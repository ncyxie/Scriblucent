using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Scriblucent.Animations;

/// <summary>Measures content at its full size while revealing only its layout slot.</summary>
public class LayoutRevealHost : Decorator
{
    private double _contentSizeTarget = double.NaN;
    private bool _retargetQueued;
    private bool _revealQueued;
    public LayoutRevealHost()
    {
        Loaded += (_, _) => ApplyReveal(animate: false);
        Unloaded += (_, _) =>
        {
            SurfaceMotion.Stop(this, MotionChannel.Layout);
            _contentSizeTarget = double.NaN;
            Extent = double.NaN;
        };
        NaturalExtentChanged += () =>
        {
            if (_retargetQueued || double.IsNaN(_contentSizeTarget)
                || Math.Abs(NaturalExtent - _contentSizeTarget) < .5
                || !NativeSurfaceMotion.IsRunning(this, MotionChannel.Layout)) return;
            _retargetQueued = true;
            Dispatcher.BeginInvoke(() =>
            {
                _retargetQueued = false;
                if (!IsLoaded || double.IsNaN(_contentSizeTarget)
                    || !NativeSurfaceMotion.IsRunning(this, MotionChannel.Layout)
                    || Math.Abs(NaturalExtent - _contentSizeTarget) < .5) return;
                SurfaceMotion.Stop(this, MotionChannel.Layout);
                AnimateContentExtent(Extent);
            }, DispatcherPriority.Render);
        };
    }

    /// <summary>Optional bound visibility for the whole section, independent of its inner disclosure.</summary>
    public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
        nameof(IsRevealed), typeof(bool?), typeof(LayoutRevealHost),
        new PropertyMetadata(null, (target, _) => ((LayoutRevealHost)target).QueueReveal()));
    public bool? IsRevealed { get => (bool?)GetValue(IsRevealedProperty); set => SetValue(IsRevealedProperty, value); }

    private void QueueReveal()
    {
        if (!IsLoaded || _revealQueued) return;
        _revealQueued = true;
        // Collection refreshes often clear and refill in one dispatcher turn.
        // Resolve their final state instead of briefly collapsing a populated section.
        Dispatcher.BeginInvoke(() =>
        {
            _revealQueued = false;
            if (IsLoaded) ApplyReveal(animate: true);
        }, DispatcherPriority.DataBind);
    }

    private void ApplyReveal(bool animate)
    {
        if (IsRevealed is bool show && Child is FrameworkElement content)
            UiMotion.AnimateLayoutReveal(content, show, () => OnRevealCompleted(animate), animate: animate, soften: true);
    }

    protected virtual void OnRevealCompleted(bool stateChanged) { }

    /// <summary>Retargets a stationary row after a content swap, keeping its controls interactive.</summary>
    internal void ChangeContent(Action change)
    {
        SurfaceMotion.Stop(this, MotionChannel.Layout);
        _contentSizeTarget = double.NaN;
        double from = double.IsNaN(Extent) ? ActualHeight : Extent;
        change();
        if (!IsLoaded || !IsVisible || !MotionPolicy.Spatial)
        {
            Extent = double.NaN;
            return;
        }
        Extent = Math.Max(0, from);
        Child?.InvalidateMeasure();
        InvalidateMeasure();
        double width = ActualWidth > 0 ? ActualWidth : (Parent as FrameworkElement)?.ActualWidth ?? 0;
        Measure(new Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
        AnimateContentExtent(from);
    }

    private void AnimateContentExtent(double from)
    {
        _contentSizeTarget = NaturalExtent;
        if (Math.Abs(from - _contentSizeTarget) < .5)
        {
            _contentSizeTarget = double.NaN;
            Extent = double.NaN;
            return;
        }
        SurfaceMotion.AnimateTracks(this, MotionChannel.Layout,
            [new(this, ExtentProperty, _contentSizeTarget, true)], MotionTokens.Layout, UiMotion.InOutSine,
            () => { _contentSizeTarget = double.NaN; Extent = double.NaN; });
    }

    internal Orientation Axis { get; set; } = Orientation.Vertical;
    public static readonly DependencyProperty AnimateLargeExtentProperty = DependencyProperty.Register(
        nameof(AnimateLargeExtent), typeof(bool), typeof(LayoutRevealHost), new PropertyMetadata(false));
    /// <summary>Keep the measured child fixed while its layout slot changes, including for large content.</summary>
    public bool AnimateLargeExtent { get => (bool)GetValue(AnimateLargeExtentProperty); set => SetValue(AnimateLargeExtentProperty, value); }
    internal double NaturalExtent { get; private set; }
    internal event Action? NaturalExtentChanged;

    public static readonly DependencyProperty RevealFromEndProperty = DependencyProperty.Register(
        nameof(RevealFromEnd), typeof(bool), typeof(LayoutRevealHost),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));
    public bool RevealFromEnd { get => (bool)GetValue(RevealFromEndProperty); set => SetValue(RevealFromEndProperty, value); }

    internal static readonly DependencyProperty ExtentProperty = DependencyProperty.Register(
        "Extent", typeof(double), typeof(LayoutRevealHost),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    internal double Extent
    {
        get => (double)GetValue(ExtentProperty);
        set => SetValue(ExtentProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return default;
        Child.Measure(Axis == Orientation.Vertical
            ? new Size(constraint.Width, double.PositiveInfinity)
            : new Size(double.PositiveInfinity, constraint.Height));
        var size = Child.DesiredSize;
        // A wrapping child needs the same cross-axis width during measure and
        // arrange. Using its longest line as the host width can repeatedly wrap
        // that line again and restart the reveal at a different height.
        if (Axis == Orientation.Vertical && double.IsFinite(constraint.Width)
            && Child is FrameworkElement { HorizontalAlignment: HorizontalAlignment.Stretch } element
            && double.IsNaN(element.Width))
            size.Width = Math.Min(constraint.Width, element.MaxWidth + element.Margin.Left + element.Margin.Right);
        double natural = Axis == Orientation.Vertical ? size.Height : size.Width;
        if (Math.Abs(natural - NaturalExtent) > .5)
        {
            NaturalExtent = natural;
            NaturalExtentChanged?.Invoke();
        }
        if (!double.IsNaN(Extent))
            size = Axis == Orientation.Vertical ? new Size(size.Width, Extent) : new Size(Extent, size.Height);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not null)
        {
            var size = Axis == Orientation.Vertical
                ? new Size(finalSize.Width, Math.Max(finalSize.Height, Child.DesiredSize.Height))
                : new Size(Math.Max(finalSize.Width, Child.DesiredSize.Width), finalSize.Height);
            var origin = !RevealFromEnd ? new Point() : Axis == Orientation.Horizontal
                ? new Point(finalSize.Width - size.Width, 0) : new Point(0, finalSize.Height - size.Height);
            Child.Arrange(new Rect(origin, size));
        }
        return finalSize;
    }

    protected override Geometry? GetLayoutClip(Size layoutSlotSize)
    {
        var original = base.GetLayoutClip(layoutSlotSize);
        if (double.IsNaN(Extent)) return original;
        // Fixed Grid slots may arrange a stretched host beyond its desired size.
        // The reveal must still follow the animated extent in those slots.
        var reveal = new RectangleGeometry(new Rect(0, 0,
            Axis == Orientation.Horizontal ? Math.Min(RenderSize.Width, Extent) : RenderSize.Width,
            Axis == Orientation.Vertical ? Math.Min(RenderSize.Height, Extent) : RenderSize.Height));
        return original is null ? reveal : new CombinedGeometry(GeometryCombineMode.Intersect, original, reveal);
    }
}
