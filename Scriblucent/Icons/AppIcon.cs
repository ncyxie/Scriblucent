using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace Scriblucent.Icons;

/// <summary>
/// Thin Lucide vector icon for app chrome. Prefer this over emoji / system symbol fonts.
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
            new FrameworkPropertyMetadata(PackIconLucideKind.None, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateLucideKind();
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AppIcon icon)
            icon.UpdateLucideKind();
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
