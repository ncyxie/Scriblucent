using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>Context actions share navigation timing while their inner buttons retain press/hover motion.</summary>
public sealed class DashboardActionStrip : StackPanel
{
    public static readonly DependencyProperty DashboardProperty = DependencyProperty.Register(
        nameof(Dashboard), typeof(string), typeof(DashboardActionStrip), new PropertyMetadata("", DashboardChanged));
    public string Dashboard { get => (string)GetValue(DashboardProperty); set => SetValue(DashboardProperty, value); }
    private static readonly DependencyProperty SlotWidthProperty = DependencyProperty.Register(
        "SlotWidth", typeof(double), typeof(DashboardActionStrip), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));
    private FrameworkElement[] _surfaces = [];
    private TranslateTransform[] _poses = [];
    private double _naturalWidth;
    private int _version;
    private bool _watching;
    private bool HasActions => Dashboard is "Flashcards" or "Quizzes";
    private bool HasStatistics => Dashboard == "Flashcards";
    internal bool IsTransitioning => NativeSurfaceMotion.IsRunning(this, MotionChannel.Layout)
        || _surfaces.Any(surface => NativeSurfaceMotion.IsRunning(surface, MotionChannel.Presence) || NativeSurfaceMotion.IsRunning(surface, MotionChannel.Layout));

    public DashboardActionStrip()
    {
        Orientation = Orientation.Horizontal;
        Visibility = Visibility.Collapsed; IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            if (!_watching) { MotionPolicy.Changed += PolicyChanged; _watching = true; }
            Apply(false);
        };
        Unloaded += (_, _) =>
        {
            if (_watching) MotionPolicy.Changed -= PolicyChanged;
            _watching = false;
            ++_version; StopMotion(); Settle();
        };
    }
    private static void DashboardChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var strip = (DashboardActionStrip)target;
        if (strip.IsLoaded) strip.Apply(true);
    }
    private void PolicyChanged() => Apply(false);
    private bool EnsureSurfaces()
    {
        if (_surfaces.Length == 4) return true;
        if (Children.Count != 4) return false;
        _surfaces = Children.Cast<FrameworkElement>().ToArray();
        _poses = _surfaces.Select(surface =>
        {
            var pose = new TranslateTransform(); surface.RenderTransform = pose; return pose;
        }).ToArray();
        return true;
    }
    private void StopMotion()
    {
        SurfaceMotion.Stop(this, MotionChannel.Layout);
        foreach (var surface in _surfaces)
        {
            SurfaceMotion.Stop(surface, MotionChannel.Presence);
            SurfaceMotion.Stop(surface, MotionChannel.Layout);
        }
    }
    private void Settle()
    {
        if (!EnsureSurfaces()) return;
        SetValue(SlotWidthProperty, double.NaN);
        Visibility = HasActions ? Visibility.Visible : Visibility.Collapsed;
        IsHitTestVisible = HasActions;
        for (int i = 0; i < 4; i++)
        {
            bool show = HasActions && (i != 2 || HasStatistics);
            _surfaces[i].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _surfaces[i].Opacity = i == 3 ? .7 : 1;
            _surfaces[i].IsHitTestVisible = i != 3 && show;
            _poses[i].X = _poses[i].Y = 0;
        }
    }
    private void Apply(bool animate)
    {
        if (!EnsureSurfaces()) return;
        int version = ++_version;
        StopMotion();
        if (!animate || MotionPolicy.Quality == MotionQuality.Off) { Settle(); return; }
        if (!HasActions)
        {
            IsHitTestVisible = false;
            var departing = Enumerable.Range(0, 3).Where(i => _surfaces[i].Visibility == Visibility.Visible).Reverse().ToArray();
            if (departing.Length == 0) { Settle(); return; }
            for (int step = 0; step < departing.Length; step++)
            {
                int index = departing[step];
                Presence(index, false, MotionTokens.Exit, step * MotionTokens.Stagger,
                    step == departing.Length - 1 ? () => LayoutChange(() =>
                    {
                        foreach (var surface in _surfaces) surface.Visibility = Visibility.Collapsed;
                    }, version, collapse: true) : null);
            }
            Presence(3, false, MotionTokens.Exit);
            return;
        }

        IsHitTestVisible = true;
        bool freshGroup = Visibility != Visibility.Visible || _surfaces[0].Visibility != Visibility.Visible;
        if (freshGroup)
        {
            Settle();
            for (int i = 0; i < 4; i++)
            {
                if (_surfaces[i].Visibility != Visibility.Visible) continue;
                _surfaces[i].Opacity = 0;
                _poses[i].Y = MotionPolicy.Spatial && i != 3 ? 3 : 0;
                Presence(i, true, MotionTokens.Navigation, i == 3 ? 0 : i * MotionTokens.Stagger);
            }
            return;
        }

        Presence(0, true, MotionTokens.Navigation);
        Presence(1, true, MotionTokens.Navigation);
        Presence(3, true, MotionTokens.Navigation);
        if (HasStatistics)
        {
            bool freshStatistics = _surfaces[2].Visibility != Visibility.Visible;
            LayoutChange(() => _surfaces[2].Visibility = Visibility.Visible, version);
            if (freshStatistics)
            {
                _surfaces[2].Opacity = 0; _poses[2].Y = MotionPolicy.Spatial ? 3 : 0;
                // Give the shared buttons room before exposing a new hit target beside utilities.
                _surfaces[2].IsHitTestVisible = false;
            }
            Presence(2, true, freshStatistics ? MotionTokens.Exit : MotionTokens.Navigation,
                freshStatistics ? MotionTokens.Exit : 0,
                () => { if (version == _version && HasStatistics) _surfaces[2].IsHitTestVisible = true; });
        }
        else if (_surfaces[2].Visibility == Visibility.Visible)
        {
            _surfaces[2].IsHitTestVisible = false;
            Presence(2, false, MotionTokens.Exit, complete: () =>
                LayoutChange(() => _surfaces[2].Visibility = Visibility.Collapsed, version));
        }
        else LayoutChange(() => { }, version);
    }
    private void Presence(int index, bool show, int duration, int delay = 0, Action? complete = null)
    {
        var surface = _surfaces[index];
        double opacity = show ? index == 3 ? .7 : 1 : 0;
        double y = show || index == 3 || !MotionPolicy.Spatial ? 0 : -2;
        if (Math.Abs(surface.Opacity - opacity) < .001 && Math.Abs(_poses[index].Y - y) < .001)
        { complete?.Invoke(); return; }
        int version = _version;
        SurfaceMotion.AnimateTracks(surface, MotionChannel.Presence,
            [new(surface, OpacityProperty, opacity), new(_poses[index], TranslateTransform.YProperty, y, true)],
            duration, UiMotion.InOutSine, () => { if (version == _version) complete?.Invoke(); }, startDelayMs: delay);
    }
    private void LayoutChange(Action change, int version, bool collapse = false)
    {
        if (version != _version || !IsLoaded) return;
        var parent = Parent as FrameworkElement;
        if (parent is null) { change(); Settle(); return; }
        double width = ActualWidth;
        var before = _surfaces.Select(surface => surface.Visibility == Visibility.Visible
            ? (Point?)surface.TranslatePoint(new Point(), parent) : null).ToArray();
        SetValue(SlotWidthProperty, width);
        change();
        for (int i = 0; i < 4; i++)
        {
            _poses[i].X = 0;
            if (_surfaces[i].Visibility == Visibility.Collapsed)
            { _poses[i].Y = 0; _surfaces[i].Opacity = i == 3 ? .7 : 1; }
        }
        InvalidateMeasure(); parent.UpdateLayout();
        double target = _naturalWidth;
        for (int i = 0; i < 4; i++)
        {
            if (_surfaces[i].Visibility != Visibility.Visible) continue;
            if (before[i] is { } origin)
                _poses[i].X = origin.X - _surfaces[i].TranslatePoint(new Point(), parent).X;
            if (Math.Abs(_poses[i].X) > .01)
                SurfaceMotion.AnimateTracks(_surfaces[i], MotionChannel.Layout,
                    [new(_poses[i], TranslateTransform.XProperty, 0, true)], MotionTokens.Navigation, UiMotion.InOutSine);
        }
        if (Math.Abs(width - target) < .5)
        {
            SetValue(SlotWidthProperty, double.NaN);
            if (collapse) Settle();
            return;
        }
        SurfaceMotion.AnimateTracks(this, MotionChannel.Layout, [new(this, SlotWidthProperty, target, true)],
            MotionTokens.Navigation, UiMotion.InOutSine, () =>
            {
                if (version != _version) return;
                SetValue(SlotWidthProperty, double.NaN);
                if (collapse) Settle();
            });
    }
    protected override Size MeasureOverride(Size constraint)
    {
        var natural = base.MeasureOverride(constraint); _naturalWidth = natural.Width;
        return GetValue(SlotWidthProperty) is double width && double.IsFinite(width)
            ? new Size(Math.Max(0, width), natural.Height) : natural;
    }
}
