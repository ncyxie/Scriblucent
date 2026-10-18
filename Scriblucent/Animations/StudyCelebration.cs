using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Scriblucent.Animations;

/// <summary>A bounded, one-shot celebration. One drawing surface and one procedural timeline per overlay.</summary>
public static class StudyCelebration
{
    public enum Intensity { Full, Subtle }
    private static readonly ConditionalWeakTable<Panel, ConfettiVisual> Surfaces = new();
    public static void PlayConfetti(Panel? overlay, Intensity intensity = Intensity.Full, FrameworkElement? origin = null)
    {
        if (overlay is null) return;
        Stop(overlay);
        if (!MotionPolicy.Celebration) return;
        overlay.Visibility = Visibility.Visible;
        overlay.UpdateLayout();
        var colors = overlay.Children.OfType<FrameworkElement>()
            .Select(e => e is Shape s ? s.Fill : (e as Border)?.Background)
            .Where(b => b is not null).Take(36).Cast<Brush>().ToArray();
        foreach (FrameworkElement child in overlay.Children) child.Opacity = 0;
        var surface = new ConfettiVisual(colors, Math.Min(MotionPolicy.ParticleBudget, intensity == Intensity.Full ? 36 : 20))
        {
            Width = Math.Max(1, overlay.ActualWidth),
            Height = Math.Max(1, overlay.ActualHeight),
            IsHitTestVisible = false
        };
        surface.Origin = origin is null ? new Point(surface.Width * .5, surface.Height * .35)
            : origin.TranslatePoint(new Point(origin.ActualWidth * .5, origin.ActualHeight * .5), overlay);
        surface.OriginRadius = origin is null ? 0 : Math.Min(90, origin.ActualWidth * .5 + 12);
        Surfaces.Add(overlay, surface);
        overlay.Children.Add(surface);
        overlay.Unloaded += OnUnloaded;
        surface.Window = Window.GetWindow(overlay);
        if (surface.Window is { } window) window.Deactivated += surface.Deactivated = (_, _) => Stop(overlay);
        MotionPolicy.Changed += surface.PolicyChanged = () => Stop(overlay);
        SurfaceMotion.AnimateTracks(overlay, MotionChannel.Showcase,
            [new(surface, ConfettiVisual.ProgressProperty, 1, true)], 1100, new LinearEase(), () => Stop(overlay));
    }
    private sealed class LinearEase : System.Windows.Media.Animation.EasingFunctionBase
    {
        protected override double EaseInCore(double t) => t;
        protected override Freezable CreateInstanceCore() => new LinearEase();
    }
    private static void OnUnloaded(object sender, RoutedEventArgs e) => Stop(sender as Panel);
    public static void Stop(Panel? overlay)
    {
        if (overlay is null) return;
        UiMotion.Stop(UiMotion.Channel(overlay, MotionChannel.Showcase));
        SurfaceMotion.Stop(overlay, MotionChannel.Showcase);
        overlay.Unloaded -= OnUnloaded;
        if (Surfaces.TryGetValue(overlay, out var surface))
        {
            if (surface.Window is { } window) window.Deactivated -= surface.Deactivated;
            MotionPolicy.Changed -= surface.PolicyChanged;
            overlay.Children.Remove(surface);
            Surfaces.Remove(overlay);
        }
        overlay.Visibility = Visibility.Collapsed;
        foreach (var child in overlay.Children.OfType<FrameworkElement>()) child.Opacity = 0;
    }
    public static void RevealScore(params FrameworkElement?[] elements) => RevealScore(0, elements);
    public static void RevealScore(int startDelayMs, params FrameworkElement?[] elements)
    {
        var index = 0;
        foreach (var element in elements)
        {
            if (element is null) continue;
            UiMotion.Stop(element);
            var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(element);
            scale.ScaleX = scale.ScaleY = 1;
            translate.Y = MotionPolicy.Spatial ? 8 : 0; element.Opacity = 0;
            SurfaceMotion.AnimateTracks(element, MotionChannel.Showcase,
                [new(element, UIElement.OpacityProperty, 1), new(translate, TranslateTransform.YProperty, 0, true)],
                240, UiMotion.OutCubic, startDelayMs: Math.Min(90, startDelayMs + index * 30));
            index++;
        }
    }
    private sealed class ConfettiVisual : FrameworkElement
    {
        private readonly Brush[] _colors;
        private readonly MatrixTransform[] _transforms;
        internal static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress),
            typeof(double), typeof(ConfettiVisual), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
        internal double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
        internal Point Origin;
        internal double OriginRadius;
        private readonly RectangleGeometry _clip = new();
        internal Window? Window;
        internal EventHandler? Deactivated;
        internal Action? PolicyChanged;
        internal ConfettiVisual(Brush[] colors, int count)
        {
            _colors = colors.Length == 0 ? new Brush[] { Brushes.Coral, Brushes.Gold, Brushes.MediumTurquoise } : colors;
            _transforms = Enumerable.Range(0, count).Select(_ => new MatrixTransform()).ToArray();
        }
        protected override void OnRender(DrawingContext dc)
        {
            _clip.Rect = new Rect(RenderSize); dc.PushClip(_clip);
            for (var i = 0; i < Math.Min(_transforms.Length, MotionPolicy.ParticleBudget); i++)
            {
                var t = Math.Clamp((Progress - i % 6 * .012) / .90, 0, 1);
                if (t <= 0 || t >= 1) continue;
                var lane = i * .618033988749895 % 1;
                var angle = -Math.PI * .90 + lane * Math.PI * .80;
                var speed = Math.Clamp(ActualHeight * .55, 180, 420) * (.7 + i % 5 * .075);
                var x = Origin.X + Math.Cos(angle) * (OriginRadius + speed * t);
                var y = Origin.Y + Math.Sin(angle) * (OriginRadius + speed * t) + ActualHeight * .9 * t * t;
                if ((x - Origin.X) * (x - Origin.X) + (y - Origin.Y) * (y - Origin.Y) < OriginRadius * OriginRadius) continue;
                // Preserve a clear corridor for the result title, score, and continuation controls.
                if (Math.Abs(x - Origin.X) < 132 && y > Origin.Y + 64 && y < Origin.Y + 220) continue;
                var matrix = Matrix.Identity;
                matrix.Rotate(t * (i % 2 == 0 ? 280 : -280));
                matrix.Translate(x, y);
                _transforms[i].Matrix = matrix;
                dc.PushOpacity(Math.Min(1, t * 12) * Math.Min(1, (1 - t) * 8) * .9);
                dc.PushTransform(_transforms[i]);
                dc.DrawRoundedRectangle(_colors[i % _colors.Length], null, new Rect(-4, -6, 8, 12), 1, 1);
                dc.Pop();
                dc.Pop();
            }
            dc.Pop();
        }
    }
}
