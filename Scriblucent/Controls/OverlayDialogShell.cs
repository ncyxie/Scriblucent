using System.Windows;
using System.Windows.Controls;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

public class OverlayDialogShell : Control
{
    static OverlayDialogShell()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(OverlayDialogShell),
            new FrameworkPropertyMetadata(typeof(OverlayDialogShell)));
    }

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(object), typeof(OverlayDialogShell));

    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(nameof(Body), typeof(object), typeof(OverlayDialogShell));

    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.Register(nameof(Footer), typeof(object), typeof(OverlayDialogShell));

    public static readonly DependencyProperty MaxCardWidthProperty =
        DependencyProperty.Register(nameof(MaxCardWidth), typeof(double), typeof(OverlayDialogShell),
            new PropertyMetadata(540d));

    public static readonly DependencyProperty CardPaddingProperty =
        DependencyProperty.Register(nameof(CardPadding), typeof(Thickness), typeof(OverlayDialogShell),
            new PropertyMetadata(new Thickness(30, 28, 30, 24)));

    public object Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public object Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public object Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public double MaxCardWidth
    {
        get => (double)GetValue(MaxCardWidthProperty);
        set => SetValue(MaxCardWidthProperty, value);
    }

    public Thickness CardPadding
    {
        get => (Thickness)GetValue(CardPaddingProperty);
        set => SetValue(CardPaddingProperty, value);
    }

    public FrameworkElement? OverlayRoot { get; private set; }
    public UIElement? OverlayDim { get; private set; }
    public FrameworkElement? PanelMotionHost { get; private set; }
    public Image? BackdropSnapshot { get; private set; }
    public FrameworkElement? ScrimHost { get; private set; }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        OverlayRoot = GetTemplateChild("OverlayRoot") as FrameworkElement;
        OverlayDim = GetTemplateChild("OverlayDim") as UIElement;
        PanelMotionHost = GetTemplateChild("PanelMotionHost") as FrameworkElement;
        BackdropSnapshot = GetTemplateChild("BackdropSnapshot") as Image;
        ScrimHost = GetTemplateChild("ScrimHost") as FrameworkElement;
    }

    public OverlayDialogAnimator BindAnimator(Window window)
    {
        ApplyTemplate();
        if (OverlayRoot is null || OverlayDim is null || PanelMotionHost is null ||
            BackdropSnapshot is null || ScrimHost is null)
        {
            throw new InvalidOperationException("OverlayDialogShell template parts are missing.");
        }

        return OverlayDialogAnimator.Bind(window, OverlayRoot, OverlayDim, PanelMotionHost, BackdropSnapshot, ScrimHost);
    }
}
