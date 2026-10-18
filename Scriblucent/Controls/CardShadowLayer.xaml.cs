using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Scriblucent.Controls;

public partial class CardShadowLayer : UserControl
{
    private static readonly DependencyProperty FaceOpacityProperty = DependencyProperty.Register(
        "FaceOpacity", typeof(double), typeof(CardShadowLayer), new PropertyMetadata(1d, FaceOpacityChanged));

    private static void FaceOpacityChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var layer = (CardShadowLayer)target;
        // Fade each nonoverlapping slice; opacity on the entire layer would create a full-card surface.
        foreach (var tile in layer.ShadowRoot.Children.OfType<Image>())
            tile.Opacity = .58 * (double)args.NewValue;
    }

    internal bool FollowsFace { get; private set; }
    public CardShadowLayer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // Shadow tiles paint outside a sibling face; their fixed corners must not enlarge its layout.
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Content is UIElement child) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (Parent is Panel panel && panel.Children.OfType<Border>().FirstOrDefault() is { } face)
        {
            // The symmetric fringe keeps the center equal to the face's center, including entry scale.
            SetBinding(RenderTransformProperty, new Binding(nameof(RenderTransform)) { Source = face });
            SetBinding(RenderTransformOriginProperty, new Binding(nameof(RenderTransformOrigin)) { Source = face });
            SetBinding(FaceOpacityProperty, new Binding(nameof(Opacity)) { Source = face });
            FollowsFace = true;
        }
    }
}
