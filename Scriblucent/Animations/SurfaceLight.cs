using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Scriblucent.Animations;

/// <summary>A painted highlight inside the moving surface. No effect, adorner tracking, or layout-sized border.</summary>
internal static class SurfaceLight
{
    private static readonly ConditionalWeakTable<FrameworkElement, Light> Lights = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Light> ArrivalLights = new();
    static SurfaceLight()
    {
        MotionPolicy.Changed += () =>
        {
            foreach (var entry in Lights.Concat(ArrivalLights))
            {
                if (!entry.Key.Dispatcher.CheckAccess()) continue;
                // Visibility is independent of a native opacity track settling to its target.
                entry.Value.Visibility = MotionPolicy.Spatial ? Visibility.Visible : Visibility.Hidden;
                if (!MotionPolicy.Spatial) entry.Value.SetCurrentValue(UIElement.OpacityProperty, 0d);
            }
            if (!MotionPolicy.ExpressiveExtras)
                foreach (var entry in ArrivalLights)
                    if (entry.Key.Dispatcher.CheckAccess())
                    { SurfaceMotion.Stop(entry.Value); entry.Value.Opacity = 0; }
        };
    }
    internal static void AddTracks(FrameworkElement surface, List<NativeMotionTrack> tracks, bool hover,
        double intensity = 1, bool neutral = false, Brush? wash = null, Brush? edge = null)
    {
        if (!Lights.TryGetValue(surface, out var light))
        {
            if (!hover) return;
            light = new Light { Opacity = 0, IsHitTestVisible = false,
                Visibility = MotionPolicy.Spatial ? Visibility.Visible : Visibility.Hidden };
            if (!light.Attach(surface, 40)) return;
            light.SetResourceReference(Light.WashProperty, neutral ? "ControlHoverSurfaceWash" : "HoverSurfaceWash");
            light.SetResourceReference(Light.EdgeProperty, neutral ? "ControlHoverSurfaceEdge" : "HoverSurfaceEdge");
            Lights.Add(surface, light);
            surface.Unloaded += (_, _) => Reset(surface);
        }
        if (wash is not null) light.SetCurrentValue(Light.WashProperty, wash);
        if (edge is not null) light.SetCurrentValue(Light.EdgeProperty, edge);
        tracks.Add(new(light, UIElement.OpacityProperty, hover && MotionPolicy.Spatial ? Math.Clamp(intensity, 0, 1) : 0));
    }
    internal static void Reset(FrameworkElement surface)
    {
        if (Lights.TryGetValue(surface, out var light)) light.SetCurrentValue(UIElement.OpacityProperty, 0d);
    }
    internal static bool HasHighlight(FrameworkElement surface)
        => Lights.TryGetValue(surface, out var light) && light.Opacity > .001;
    internal static void PulseArrival(FrameworkElement surface, bool compact, int delayMs)
    {
        if (!MotionPolicy.ExpressiveExtras) return;
        if (!ArrivalLights.TryGetValue(surface, out var light))
        {
            light = new Light { Opacity = 0, IsHitTestVisible = false };
            if (!light.Attach(surface, 39)) return;
            light.SetResourceReference(Light.WashProperty, "HoverSurfaceWash");
            light.SetResourceReference(Light.EdgeProperty, "HoverSurfaceEdge");
            ArrivalLights.Add(surface, light);
        }
        light.Visibility = Visibility.Visible;
        light.SetCurrentValue(UIElement.OpacityProperty, 0d);
        // One soft accent breath identifies the new item. It has its own clock,
        // so it neither holds the layout gate nor fights the pointer highlight.
        void Start()
        {
            if (!MotionPolicy.ExpressiveExtras) return;
            SurfaceMotion.AnimateTracks(light, MotionChannel.Presence,
                [new(light, UIElement.OpacityProperty, 0, Overshoot: compact ? .45 : .85,
                    Profile: new(UiMotion.InOutSine, PeakAt: .38))],
                compact ? 300 : 440, UiMotion.InOutSine, startDelayMs: delayMs);
        }
        if (light.IsLoaded) Start();
        else
        {
            RoutedEventHandler? loaded = null;
            loaded = (_, _) => { light.Loaded -= loaded; Start(); };
            light.Loaded += loaded;
        }
    }
    private sealed class Light : SurfacePaint
    {
        internal static readonly DependencyProperty WashProperty = DependencyProperty.Register("Wash", typeof(Brush), typeof(Light),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty EdgeProperty = DependencyProperty.Register("Edge", typeof(Brush), typeof(Light),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        protected override void DrawSurface(DrawingContext drawing)
        {
            if (ActualWidth < 2 || ActualHeight < 2) return;
            var edge = GetValue(EdgeProperty) as Brush;
            var contour = Contour(); var wash = GetValue(WashProperty) as Brush;
            if (OwnerClipsContour) drawing.DrawRectangle(wash, null, contour.Bounds);
            else drawing.DrawGeometry(wash, null, contour);
            if (edge is not null && edge is not SolidColorBrush { Color.A: 0 })
                drawing.DrawGeometry(edge, null, Rim(1));
        }
    }
}
