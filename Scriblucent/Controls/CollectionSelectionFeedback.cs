using System.Windows;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>One live wash and fixed contour, painted above cached card images.</summary>
public sealed class CollectionSelectionFeedback : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(CollectionSelectionFeedback),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(CollectionSelectionFeedback),
        new FrameworkPropertyMetadata(new CornerRadius(14), FrameworkPropertyMetadataOptions.AffectsRender));
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(nameof(SelectionBrush), typeof(Brush), typeof(CollectionSelectionFeedback),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    internal void Select(bool value, bool animate)
    {
        if (!animate || !IsLoaded || MotionPolicy.Preference == MotionPreference.Off)
        { SurfaceMotion.Stop(this); SetCurrentValue(ProgressProperty, value ? 1d : 0d); return; }
        SurfaceMotion.AnimateTracks(this, MotionChannel.Selection,
            [new(this, ProgressProperty, value ? 1d : 0d)], MotionPolicy.Spatial ? 160 : 100, UiMotion.OutCubic);
    }
    public CollectionSelectionFeedback()
    {
        IsHitTestVisible = false;
        SetResourceReference(SelectionBrushProperty, "NoteCardSelectionBorder");
        Unloaded += (_, _) => SurfaceMotion.Stop(this);
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (Progress <= 0 || ActualWidth <= 2 || ActualHeight <= 2) return;
        double radius = CornerRadius.TopLeft;
        var brush = SelectionBrush;
        dc.PushOpacity(Progress);
        dc.PushOpacity(.055);
        dc.DrawRoundedRectangle(brush, null, new Rect(RenderSize), radius, radius);
        dc.Pop();
        var pen = new Pen(brush, 2); pen.Freeze();
        dc.DrawRoundedRectangle(null, pen, new Rect(1, 1, ActualWidth - 2, ActualHeight - 2), Math.Max(0, radius - 1), Math.Max(0, radius - 1));
        dc.Pop();
    }
}
