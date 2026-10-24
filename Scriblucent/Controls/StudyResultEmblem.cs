using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>Small retained score/check graphic. Arc geometry is built once; clocks reveal its stroke.</summary>
public sealed class StudyResultEmblem : Grid
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(nameof(Ratio),
        typeof(double), typeof(StudyResultEmblem), new PropertyMetadata(1d, GraphicChanged));
    public static readonly DependencyProperty IsCompletionProperty = DependencyProperty.Register(nameof(IsCompletion),
        typeof(bool), typeof(StudyResultEmblem), new PropertyMetadata(false, GraphicChanged));
    private readonly Path _arc = new() { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Path _check = new() { StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round, Data = Geometry.Parse("M 51,72 L 65,86 L 96,55") };
    private readonly TextBlock _number = new() { FontSize = 29, FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    public double Ratio { get => (double)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }
    public bool IsCompletion { get => (bool)GetValue(IsCompletionProperty); set => SetValue(IsCompletionProperty, value); }
    private static void GraphicChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var emblem = (StudyResultEmblem)target;
        SurfaceMotion.Stop(emblem._arc); SurfaceMotion.Stop(emblem._check);
        emblem.UpdateGraphic();
    }
    public StudyResultEmblem()
    {
        Width = Height = 144; IsHitTestVisible = false; RenderTransformOrigin = new Point(.5, .5);
        var track = new Ellipse { Margin = new Thickness(8), StrokeThickness = 4 };
        track.SetResourceReference(Shape.StrokeProperty, "IllustrationLayer");
        _arc.SetResourceReference(Shape.StrokeProperty, "IllustrationInk");
        _check.SetResourceReference(Shape.StrokeProperty, "IllustrationInk");
        _number.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
        Children.Add(track); Children.Add(_arc); Children.Add(_number); Children.Add(_check);
        var halo = new Ellipse(); halo.SetResourceReference(Shape.FillProperty, "ShowcaseHalo");
        var plate = new Ellipse { Margin = new Thickness(18) }; plate.SetResourceReference(Shape.FillProperty, "IllustrationPaper");
        var rim = new Ellipse { Margin = new Thickness(18), StrokeThickness = 1 };
        rim.SetResourceReference(Shape.StrokeProperty, "IllustrationPaperEdge");
        foreach (var layer in new[] { halo, plate, rim }) { Panel.SetZIndex(layer, -1); Children.Add(layer); }
        void Refresh() { foreach (var layer in new[] { halo, plate, rim }) layer.Visibility = MotionPolicy.Spatial ? Visibility.Visible : Visibility.Hidden; }
        Loaded += (_, _) => { UpdateGraphic(); MotionPolicy.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => MotionPolicy.Changed -= Refresh;
        Unloaded += (_, _) => { SurfaceMotion.Stop(this); SurfaceMotion.Stop(_arc); SurfaceMotion.Stop(_check); };
    }
    private double UpdateGraphic()
    {
        var ratio = Math.Clamp(double.IsFinite(Ratio) ? Ratio : 0, 0, 1);
        const double radius = 62;
        if (ratio >= .9999) _arc.Data = new EllipseGeometry(new Point(72, 72), radius, radius) { Transform = new RotateTransform(-90, 72, 72) };
        else if (ratio <= 0) _arc.Data = Geometry.Empty;
        else
        {
            var angle = ratio * Math.PI * 2;
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                drawing.BeginFigure(new Point(72, 10), false, false);
                drawing.ArcTo(new Point(72 + Math.Sin(angle) * radius, 72 - Math.Cos(angle) * radius),
                    new Size(radius, radius), 0, ratio > .5, SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze(); _arc.Data = geometry;
        }
        var length = Math.Max(.01, ratio * Math.PI * 2 * radius / _arc.StrokeThickness);
        _arc.StrokeDashArray = new DoubleCollection { length, length };
        _arc.StrokeDashOffset = 0;
        _check.StrokeDashArray = new DoubleCollection { 18, 18 }; _check.StrokeDashOffset = 0;
        _check.Visibility = IsCompletion ? Visibility.Visible : Visibility.Collapsed;
        _number.Visibility = IsCompletion ? Visibility.Collapsed : Visibility.Visible;
        _number.Text = ratio.ToString("P0", CultureInfo.CurrentCulture);
        return length;
    }
    public void Play()
    {
        SurfaceMotion.Stop(this); SurfaceMotion.Stop(_arc); SurfaceMotion.Stop(_check);
        var length = UpdateGraphic();
        var (scale, translate) = UiMotion.EnsureScaleTranslateGroup(this);
        scale.ScaleX = scale.ScaleY = MotionPolicy.Spatial ? .88 : 1;
        translate.Y = MotionPolicy.Spatial ? 16 : 0; Opacity = 0;
        SurfaceMotion.AnimateTracks(this, MotionChannel.Showcase,
            [new(this, OpacityProperty, 1, Profile: MotionProfiles.Readable),
             new(translate, TranslateTransform.YProperty, 0, true, Profile: MotionProfiles.Arrival),
             new(scale, ScaleTransform.ScaleXProperty, 1, true, 1.025, .997, MotionProfiles.Showcase),
             new(scale, ScaleTransform.ScaleYProperty, 1, true, 1.025, .997, MotionProfiles.Showcase)], 520, UiMotion.InOutSine, cache: true);
        _arc.StrokeDashOffset = MotionPolicy.ExpressiveExtras ? length : 0;
        if (MotionPolicy.ExpressiveExtras)
            SurfaceMotion.AnimateTracks(_arc, MotionChannel.Showcase,
                [new(_arc, Shape.StrokeDashOffsetProperty, 0, true)], 520, UiMotion.OutSine);
        if (IsCompletion && MotionPolicy.ExpressiveExtras)
        {
            _check.StrokeDashOffset = MotionPolicy.Spatial ? 18 : 0;
            SurfaceMotion.AnimateTracks(_check, MotionChannel.Showcase,
                [new(_check, Shape.StrokeDashOffsetProperty, 0, true)], 240, UiMotion.OutSine, startDelayMs: 100);
        }
    }
}
