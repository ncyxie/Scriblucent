using System.Windows;
using System.Windows.Media;

namespace Scriblucent.Controls;

/// <summary>A selection plate whose painted width follows the edge without changing the navigation layout.</summary>
public sealed class SidebarSelectionSurface : FrameworkElement
{
    public static readonly DependencyProperty PaintedWidthProperty = DependencyProperty.Register(nameof(PaintedWidth), typeof(double), typeof(SidebarSelectionSurface),
        new FrameworkPropertyMetadata(48d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(SidebarSelectionSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EdgeProperty = DependencyProperty.Register(nameof(Edge), typeof(Brush), typeof(SidebarSelectionSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public double PaintedWidth { get => (double)GetValue(PaintedWidthProperty); set => SetValue(PaintedWidthProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Edge { get => (Brush?)GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }
    private Pen? _pen;
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualHeight < 2 || PaintedWidth < 2) return;
        if (_pen?.Brush != Edge) _pen = Edge is null ? null : new Pen(Edge, 1);
        dc.DrawRoundedRectangle(Fill, _pen, new Rect(.5, .5, PaintedWidth - 1, ActualHeight - 1), 14, 14);
    }
}
