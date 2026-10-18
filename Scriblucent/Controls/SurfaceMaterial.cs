using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Rendering;

namespace Scriblucent.Controls;

public enum SurfaceMaterialKind { None, Filled, Tinted, Marker, Data, Neutral, Raised, Metric, Workspace }

/// <summary>Opt-in resting material; never changes a native control's input, layout or background binding.</summary>
public static class SurfaceMaterial
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(SurfaceMaterialKind),
        typeof(SurfaceMaterial), new FrameworkPropertyMetadata(SurfaceMaterialKind.None, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.RegisterAttached("Foreground", typeof(Brush),
        typeof(SurfaceMaterial), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    // A connected header continues into its tab above. An internal top rim would divide that face.
    public static readonly DependencyProperty OpenTopProperty = DependencyProperty.RegisterAttached("OpenTop", typeof(bool),
        typeof(SurfaceMaterial), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    internal static readonly DependencyProperty DarkProperty = DependencyProperty.RegisterAttached("Dark", typeof(bool),
        typeof(SurfaceMaterial), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    private static readonly ConditionalWeakTable<Border, MaterialPaint> Layers = new();
    public static void SetKind(DependencyObject target, SurfaceMaterialKind value) => target.SetValue(KindProperty, value);
    public static SurfaceMaterialKind GetKind(DependencyObject target) => (SurfaceMaterialKind)target.GetValue(KindProperty);
    public static void SetForeground(DependencyObject target, Brush? value) => target.SetValue(ForegroundProperty, value);
    public static Brush? GetForeground(DependencyObject target) => (Brush?)target.GetValue(ForegroundProperty);
    public static void SetOpenTop(DependencyObject target, bool value) => target.SetValue(OpenTopProperty, value);
    public static bool GetOpenTop(DependencyObject target) => (bool)target.GetValue(OpenTopProperty);
    internal static bool IsDark(DependencyObject target) => (bool)target.GetValue(DarkProperty);
    internal static SurfaceMaterialPalette.Paint? Paint(Brush? background, SurfaceMaterialKind kind, bool dark, Brush? foreground = null)
        => kind != SurfaceMaterialKind.None && background is SolidColorBrush { Color.A: > 0 } solid
            ? SurfaceMaterialPalette.Get(solid.Color, kind, dark, (foreground as SolidColorBrush)?.Color) : null;
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is InteractionSurface face) { face.RefreshMaterial(); return; }
        if (target is SurfaceMaterialBorder direct) { direct.InvalidateVisual(); return; }
        if (target is not Border border) return;
        border.Loaded -= Loaded;
        if (GetKind(border) != SurfaceMaterialKind.None) border.Loaded += Loaded;
        if (border.IsLoaded) Attach(border);
        if (Layers.TryGetValue(border, out var layer)) layer.InvalidateVisual();
    }
    private static void Loaded(object sender, RoutedEventArgs e) => Attach((Border)sender);
    private static void Attach(Border border)
    {
        if (GetKind(border) == SurfaceMaterialKind.None)
        { if (Layers.TryGetValue(border, out var existing)) existing.Visibility = Visibility.Collapsed; return; }
        if (!Layers.TryGetValue(border, out var layer))
        {
            layer = new MaterialPaint(border);
            layer.SetBinding(MaterialPaint.BackgroundProperty, new Binding(nameof(Border.Background)) { Source = border });
            layer.SetBinding(MaterialPaint.KindProperty, new Binding { Source = border, Path = new PropertyPath(KindProperty) });
            layer.SetBinding(MaterialPaint.ForegroundProperty, new Binding { Source = border, Path = new PropertyPath(ForegroundProperty) });
            layer.SetResourceReference(MaterialPaint.DarkProperty, "SurfaceMaterialIsDark");
            if (!layer.Attach(border, -4)) return;
            Layers.Add(border, layer);
        }
        layer.Visibility = Visibility.Visible;
    }
    private sealed class MaterialPaint : SurfacePaint
    {
        private readonly Border _face;
        private readonly WorkspaceGradientTexture.Fill _workspace = new();
        internal MaterialPaint(Border face) => _face = face;
        internal static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register("Background", typeof(Brush), typeof(MaterialPaint),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty KindProperty = DependencyProperty.Register("Kind", typeof(SurfaceMaterialKind), typeof(MaterialPaint),
            new FrameworkPropertyMetadata(SurfaceMaterialKind.None, FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register("Foreground", typeof(Brush), typeof(MaterialPaint),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty DarkProperty = DependencyProperty.Register("Dark", typeof(bool), typeof(MaterialPaint),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        protected override void DrawSurface(DrawingContext drawing)
        {
            var paint = Paint(GetValue(BackgroundProperty) as Brush, (SurfaceMaterialKind)GetValue(KindProperty),
                (bool)GetValue(DarkProperty), GetValue(ForegroundProperty) as Brush);
            if (paint is null) return;
            var fill = (SurfaceMaterialKind)GetValue(KindProperty) == SurfaceMaterialKind.Workspace
                ? _workspace.Get(paint.Colors, _face.ActualHeight, VisualTreeHelper.GetDpi(_face)) : paint.Fill;
            var frame = SurfaceContour.EffectiveFrame(_face);
            var inset = Math.Max(Math.Max(frame.Left, frame.Right), Math.Max(frame.Top, frame.Bottom));
            var contour = Contour();
            // Preserve the native border stroke. Material is entirely inside its frame.
            if (inset > 0) drawing.PushClip(Contour(inset));
            if (OwnerClipsContour) drawing.DrawRectangle(fill, null, contour.Bounds);
            else drawing.DrawGeometry(fill, null, contour);
            if ((SurfaceMaterialKind)GetValue(KindProperty) != SurfaceMaterialKind.Data)
            {
                drawing.DrawGeometry(paint.Edge, null, Rim(1, inset, GetOpenTop(_face)));
            }
            if (inset > 0) drawing.Pop();
        }
    }
}

/// <summary>Frozen shape fill, constructed when its owning theme dictionary loads.</summary>
public sealed class SurfaceMaterialBrushExtension : MarkupExtension
{
    public Color Color { get; set; }
    public Color Foreground { get; set; }
    public bool Dark { get; set; }
    public SurfaceMaterialKind Kind { get; set; } = SurfaceMaterialKind.Marker;
    public override object ProvideValue(IServiceProvider serviceProvider)
        => SurfaceMaterialPalette.Get(Color, Kind, Dark, Foreground.A == 0 ? null : Foreground).Fill;
}
