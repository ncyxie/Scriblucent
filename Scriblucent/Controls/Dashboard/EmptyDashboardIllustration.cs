using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Scriblucent.Animations;
using Scriblucent.Icons;

namespace Scriblucent.Controls.Dashboard;

/// <summary>Purpose-specific vector artwork, with a single arrival and a quiet reduced treatment.</summary>
internal sealed class EmptyDashboardIllustration : Grid
{
    private readonly List<(FrameworkElement Layer, RotateTransform Rotate, TranslateTransform Move, double Angle, double X, double Y)> _planes = new();
    private readonly List<FrameworkElement> _decoration = new();
    private readonly List<(Path Stroke, double Length)> _strokes = new();
    internal EmptyDashboardIllustration(AppIconKind kind)
    {
        Width = 144; Height = 120; IsHitTestVisible = false;
        var halo = new Ellipse { Width = 140, Height = 110 };
        halo.SetResourceReference(Shape.FillProperty, "ShowcaseHalo"); Children.Add(halo); _decoration.Add(halo);
        if (kind == AppIconKind.MindMaps) BuildMap();
        else BuildPages(kind);
        Loaded += (_, _) => { MotionPolicy.Changed += RefreshPolicy; RefreshPolicy(); };
        Unloaded += (_, _) =>
        {
            MotionPolicy.Changed -= RefreshPolicy;
            foreach (var plane in _planes) SurfaceMotion.Stop(plane.Layer);
            foreach (var stroke in _strokes) SurfaceMotion.Stop(stroke.Stroke);
        };
    }
    private Border Plane(double width, double height, double angle, double x, double y, bool front)
    {
        var group = new Grid { Width = width, Height = height, RenderTransformOrigin = new Point(.5, .65),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var shadow = new CardShadowLayer { Opacity = front ? .8 : .35 }; group.Children.Add(shadow);
        var plane = new Border { CornerRadius = new CornerRadius(front ? 14 : 12), BorderThickness = new Thickness(1) };
        plane.SetResourceReference(Border.BackgroundProperty, front ? "CardBackground" : "AccentPrimarySubtle");
        plane.SetResourceReference(Border.BorderBrushProperty, front ? "FloatingSurfaceEdge" : "AccentPrimarySubtle");
        group.Children.Add(plane);
        var rotate = new RotateTransform(angle); var move = new TranslateTransform(x, y);
        group.RenderTransform = new TransformGroup { Children = { rotate, move } };
        _planes.Add((group, rotate, move, angle, x, y)); Children.Add(group);
        if (!front) _decoration.Add(group);
        return plane;
    }
    private static Border Line(double width, double height = 3)
    {
        var line = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(height / 2),
            HorizontalAlignment = HorizontalAlignment.Left };
        line.SetResourceReference(Border.BackgroundProperty, "AccentPrimarySubtle"); return line;
    }
    private Path Stroke(string geometry, double length, double thickness = 2.5)
    {
        var path = new Path { Data = Geometry.Parse(geometry), StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            StrokeDashArray = new DoubleCollection { length, length } };
        path.SetResourceReference(Shape.StrokeProperty, "AccentPrimary"); _strokes.Add((path, length)); return path;
    }
    private void BuildPages(AppIconKind kind)
    {
        var flash = kind == AppIconKind.Flashcards;
        Plane(flash ? 62 : 68, flash ? 84 : 86, -14, -23, 3, false);
        Plane(flash ? 62 : 68, flash ? 84 : 86, 11, 22, 0, false);
        var front = Plane(flash ? 68 : 76, flash ? 88 : 96, flash ? -3 : 0, 0, -1, true);
        var content = new Grid { Margin = new Thickness(12) }; front.Child = content;
        if (kind == AppIconKind.Quizzes)
        {
            var header = new Border { Width = 24, Height = 4, CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            header.SetResourceReference(Border.BackgroundProperty, "AccentPrimary"); content.Children.Add(header);
            for (var row = 0; row < 3; row++)
            {
                var top = 18 + row * 17;
                var box = new Border { Width = 10, Height = 10, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, top, 0, 0) };
                box.SetResourceReference(Border.BorderBrushProperty, "AccentPrimary"); content.Children.Add(box);
                var line = Line(row == 2 ? 20 : 29); line.VerticalAlignment = VerticalAlignment.Top;
                line.Margin = new Thickness(17, top + 4, 0, 0); content.Children.Add(line);
            }
            content.Children.Add(Stroke("M 2,23 L 5,26 L 10,19 M 2,40 L 5,43 L 10,36", 24, 2));
        }
        else
        {
            var icon = new AppIcon { Kind = kind, Width = flash ? 34 : 26, Height = flash ? 34 : 26,
                HorizontalAlignment = flash ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, flash ? 7 : 0, 0, 0) };
            icon.SetResourceReference(Control.ForegroundProperty, "AccentPrimary"); content.Children.Add(icon);
            var rows = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            foreach (var width in flash ? new[] { 38d, 27d } : new[] { 44d, 44d, 29d })
            { var line = Line(width); line.Margin = new Thickness(0, 0, 0, 6); rows.Children.Add(line); }
            if (flash) rows.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(rows);
        }
    }
    private void BuildMap()
    {
        var links = Stroke("M 72,50 C 72,30 49,21 30,21 M 90,60 C 108,60 114,40 114,35 M 80,78 C 80,100 100,100 106,96", 90);
        Children.Add(links); _decoration.Add(links);
        Plane(36, 26, -5, -42, -39, false);
        Plane(34, 26, 5, 42, -25, false);
        Plane(36, 24, 4, 34, 36, false);
        var center = Plane(54, 38, 0, 0, 0, true);
        var icon = new AppIcon { Kind = AppIconKind.MindMaps, Width = 27, Height = 27 };
        icon.SetResourceReference(Control.ForegroundProperty, "AccentPrimary"); center.Child = icon;
    }
    private void RefreshPolicy()
    {
        foreach (var layer in _decoration) layer.Visibility = MotionPolicy.Spatial ? Visibility.Visible : Visibility.Hidden;
    }
    internal void Play()
    {
        RefreshPolicy();
        foreach (var (layer, rotate, move, angle, x, y) in _planes)
        {
            SurfaceMotion.Stop(layer);
            rotate.Angle = MotionPolicy.Spatial ? 0 : angle;
            move.X = MotionPolicy.Spatial ? 0 : x; move.Y = MotionPolicy.Spatial ? 0 : y;
            SurfaceMotion.AnimateTracks(layer, MotionChannel.Showcase,
                [new(rotate, RotateTransform.AngleProperty, angle, true), new(move, TranslateTransform.XProperty, x, true),
                 new(move, TranslateTransform.YProperty, y, true)], 480, UiMotion.InOutSine);
        }
        foreach (var (stroke, length) in _strokes)
        {
            SurfaceMotion.Stop(stroke); stroke.StrokeDashOffset = MotionPolicy.Spatial ? length : 0;
            SurfaceMotion.AnimateTracks(stroke, MotionChannel.Showcase,
                [new(stroke, Shape.StrokeDashOffsetProperty, 0, true)], 420, UiMotion.OutSine, startDelayMs: 60);
        }
    }
}
