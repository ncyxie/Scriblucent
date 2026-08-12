using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace Scriblucent.Icons;

/// <summary>
/// Thin Lucide vector icon for app chrome. Prefer this over emoji / system symbol fonts.
/// Each kind's path data is parsed once into a frozen <see cref="StreamGeometry"/> (see
/// <see cref="LucideGeometryCache"/>) and drawn with WPF vector rendering in OnRender, so
/// icons stay crisp at any DPI, layout size, or ancestor scale (editor zoom) — no bitmaps,
/// no Skia, no pixel snapping required.
/// </summary>
public class AppIcon : Control
{
    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(
            nameof(Kind),
            typeof(AppIconKind),
            typeof(AppIcon),
            new FrameworkPropertyMetadata(AppIconKind.None, FrameworkPropertyMetadataOptions.AffectsRender, OnKindChanged));

    public static readonly DependencyProperty LucideKindProperty =
        DependencyProperty.Register(
            nameof(LucideKind),
            typeof(PackIconLucideKind),
            typeof(AppIcon),
            new FrameworkPropertyMetadata(PackIconLucideKind.None, FrameworkPropertyMetadataOptions.AffectsRender, OnLucideKindChanged));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(AppIcon),
            new FrameworkPropertyMetadata(1.75, FrameworkPropertyMetadataOptions.AffectsRender));

    static AppIcon()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(AppIcon),
            new FrameworkPropertyMetadata(typeof(AppIcon)));

        FocusableProperty.OverrideMetadata(
            typeof(AppIcon),
            new FrameworkPropertyMetadata(false));

        WidthProperty.OverrideMetadata(
            typeof(AppIcon),
            new FrameworkPropertyMetadata(16.0));

        HeightProperty.OverrideMetadata(
            typeof(AppIcon),
            new FrameworkPropertyMetadata(16.0));
    }

    public AppIcon()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public AppIconKind Kind
    {
        get => (AppIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public PackIconLucideKind LucideKind
    {
        get => (PackIconLucideKind)GetValue(LucideKindProperty);
        set => SetValue(LucideKindProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty)
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var geometry = LucideGeometryCache.Get(LucideKind);
        if (geometry is null)
            return;

        var bounds = geometry.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        // Small rasters make the 1.75-unit stroke land at ~1.1-1.3px, which anti-aliases into
        // a soft gray smear (thin/thick-looking strokes). Grow the glyph optically below 18 DIPs
        // so the stroke stays >= ~1.4px; the icon's internal padding absorbs the growth.
        var minSize = Math.Min(width, height);
        var opticalGrow = minSize < 18 ? Math.Min(1.15, 18.0 / minSize) : 1.0;
        var scale = (minSize / Math.Max(bounds.Width, bounds.Height)) * opticalGrow;

        // The Lucide path data is authored Y-up; WPF is Y-down, so mirror the Y axis while
        // scaling, then translate so the scaled bounds center in the control.
        var matrix = new Matrix(
            scale, 0, 0, -scale,
            width / 2 - (bounds.Left + bounds.Width / 2) * scale,
            height / 2 + (bounds.Top + bounds.Height / 2) * scale);

        dc.PushTransform(new MatrixTransform(matrix));
        dc.DrawGeometry(Foreground, null, geometry);
        dc.Pop();
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AppIcon icon)
            icon.UpdateLucideKind();
    }

    private static void OnLucideKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AppIcon icon)
            icon.InvalidateVisual();
    }

    private void UpdateLucideKind()
    {
        LucideKind = AppIconKindMap.ToLucide(Kind);
    }
}

/// <summary>
/// Markup: Content="{icons:Icon Search}" or Content="{icons:Icon Kind=Search, Size=14}"
/// </summary>
public sealed class IconExtension : System.Windows.Markup.MarkupExtension
{
    public IconExtension()
    {
    }

    public IconExtension(AppIconKind kind)
    {
        Kind = kind;
    }

    public AppIconKind Kind { get; set; }

    public double Size { get; set; } = 15;

    public double StrokeThickness { get; set; } = 1.75;

    public Brush? Foreground { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var icon = new AppIcon
        {
            Kind = Kind,
            Width = Size,
            Height = Size,
            StrokeThickness = StrokeThickness
        };

        if (Foreground is not null)
            icon.Foreground = Foreground;

        return icon;
    }
}
