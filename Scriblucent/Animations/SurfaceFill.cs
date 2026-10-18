using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using Scriblucent.Controls;

namespace Scriblucent.Animations;

/// <summary>State fills paint beneath content, without replacing theme brushes or changing layout.</summary>
internal static class SurfaceFill
{
    internal enum Role { Selection, Hover, Pressed }
    private static readonly ConditionalWeakTable<Border, Dictionary<Role, Layer>> Layers = new();

    internal static Layer? Get(Border surface, Role role, bool create)
    {
        var layers = Layers.GetValue(surface, _ => new());
        if (layers.TryGetValue(role, out var existing)) return existing;
        if (!create) return null;
        var layer = new Layer { Opacity = 0, IsHitTestVisible = false, PaintMaterialEdge = surface is not InteractionSurface };
        if (role == Role.Selection) layer.SetValue(Layer.KindProperty, SurfaceMaterialKind.Tinted);
        else layer.SetBinding(Layer.KindProperty, new Binding { Source = surface, Path = new PropertyPath(SurfaceMaterial.KindProperty) });
        layer.SetBinding(Layer.ForegroundProperty, new Binding { Source = surface, Path = new PropertyPath(SurfaceMaterial.ForegroundProperty) });
        layer.SetResourceReference(Layer.DarkProperty, "SurfaceMaterialIsDark");
        if (!layer.Attach(surface, role == Role.Pressed ? -1 : role == Role.Hover ? -2 : -3)) return null;
        layers.Add(role, layer);
        return layer;
    }

    internal static void Select(Border surface, bool selected, bool animate, string resourceKey = "NoteCardSelection")
    {
        if (Get(surface, Role.Selection, selected) is not { } layer) return;
        layer.SetResourceReference(Layer.BrushProperty, resourceKey);
        // A freshly inserted layer has not loaded yet. Own its clock on the
        // loaded surface, independently of hover and item presence.
        if (animate) SurfaceMotion.AnimateTracks(surface, MotionChannel.Selection,
            [new(layer, UIElement.OpacityProperty, selected ? 1 : 0)], 140, UiMotion.OutSine);
        else { SurfaceMotion.Stop(surface, MotionChannel.Selection); layer.SetCurrentValue(UIElement.OpacityProperty, selected ? 1d : 0d); }
    }

    internal static void ClearSelection(Border surface)
    {
        if (!Layers.TryGetValue(surface, out var layers) || !layers.Remove(Role.Selection, out var layer)) return;
        SurfaceMotion.Stop(surface, MotionChannel.Selection);
        (layer.Parent as Panel)?.Children.Remove(layer);
    }

    internal sealed class Layer : SurfacePaint
    {
        internal bool PaintMaterialEdge { get; init; } = true;
        internal static readonly DependencyProperty KindProperty = DependencyProperty.Register("Kind", typeof(SurfaceMaterialKind), typeof(Layer),
            new FrameworkPropertyMetadata(SurfaceMaterialKind.None, FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register("Foreground", typeof(Brush), typeof(Layer),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty DarkProperty = DependencyProperty.Register("Dark", typeof(bool), typeof(Layer),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
            "Brush", typeof(Brush), typeof(Layer), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        protected override void DrawSurface(DrawingContext drawing)
        {
            var contour = Contour(); var brush = GetValue(BrushProperty) as Brush;
            var material = SurfaceMaterial.Paint(brush, (SurfaceMaterialKind)GetValue(KindProperty), (bool)GetValue(DarkProperty), GetValue(ForegroundProperty) as Brush);
            brush = material?.Fill ?? brush;
            if (OwnerClipsContour) drawing.DrawRectangle(brush, null, contour.Bounds);
            else drawing.DrawGeometry(brush, null, contour);
            if (material is not null && PaintMaterialEdge) drawing.DrawGeometry(material.Edge, null, Rim(1));
        }
    }
}
