using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Scriblucent.Animations;

/// <summary>Selection/drop feedback is paint only: the rim never consumes layout space.</summary>
internal static class AccentMotion
{
    private static readonly ConditionalWeakTable<FrameworkElement, Outline> Outlines = new();
    internal static void Set(FrameworkElement element, bool selected, Brush? brush, double radius = 10,
        double thickness = 2, bool animate = true)
    {
        if (!Outlines.TryGetValue(element, out var outline))
        {
            if (!selected) return;
            outline = new Outline { IsHitTestVisible = false, Opacity = 0 };
            if (!outline.Attach(element, 50)) return;
            Outlines.Add(element, outline);
            element.Unloaded += OnUnloaded;
        }
        var resource = new[] { "NoteCardSelectionBorder", "AccentRimBrush", "MindMapSearchMatchBorderBrush",
            "MindMapSearchActiveBorderBrush" }
            .FirstOrDefault(key => brush is not null && ReferenceEquals(brush, element.TryFindResource(key)));
        if (resource is not null) outline.SetResourceReference(Outline.BrushProperty, resource);
        else outline.SetValue(Outline.BrushProperty, brush);
        outline.Thickness = thickness;
        outline.InvalidateVisual();
        if (animate) SurfaceMotion.Animate(outline, selected ? 1 : 0, 140, UiMotion.OutSine, false);
        else { SurfaceMotion.Stop(outline); outline.Opacity = selected ? 1 : 0; }
    }
    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element) Clear(element);
    }
    internal static void Clear(FrameworkElement element)
    {
        element.Unloaded -= OnUnloaded;
        if (!Outlines.TryGetValue(element, out var outline)) return;
        SurfaceMotion.Stop(outline);
        (outline.Parent as Panel)?.Children.Remove(outline);
        Outlines.Remove(element);
    }
    private sealed class Outline : SurfacePaint
    {
        internal static readonly DependencyProperty BrushProperty = DependencyProperty.Register("Brush", typeof(Brush), typeof(Outline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        internal double Thickness;
        protected override void DrawSurface(DrawingContext dc)
        {
            IsHitTestVisible = false;
            dc.DrawGeometry(GetValue(BrushProperty) as Brush, null, Rim(Thickness));
        }
    }
}
