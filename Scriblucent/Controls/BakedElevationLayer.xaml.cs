using System.Windows;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

public partial class BakedElevationLayer
{
    public enum ElevationKind
    {
        Field,
        Button,
        ButtonCard,
        Dialog
    }

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(ElevationKind),
        typeof(BakedElevationLayer),
        new PropertyMetadata(ElevationKind.Field, (d, _) => ((BakedElevationLayer)d).Apply()));

    public ElevationKind Kind
    {
        get => (ElevationKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public BakedElevationLayer()
    {
        InitializeComponent();
        Apply();
    }

    private void Apply()
    {
        var profile = Kind switch
        {
            ElevationKind.Button => BakedElevationAsset.Button,
            ElevationKind.ButtonCard => BakedElevationAsset.ButtonCard,
            ElevationKind.Dialog => BakedElevationAsset.Dialog,
            _ => BakedElevationAsset.Field
        };

        var tile = profile.CornerTileSize;
        LeftColumn.Width = new GridLength(tile);
        RightColumn.Width = new GridLength(tile);
        TopRow.Height = new GridLength(tile);
        BottomRow.Height = new GridLength(tile);
        Margin = new Thickness(
            -profile.BleedTop,
            -profile.BleedTop,
            -profile.BleedTop,
            -profile.BleedBottom);

        if (ReadLocalValue(OpacityProperty) == DependencyProperty.UnsetValue)
            Opacity = profile.Opacity;

        TopLeft.Source = BakedElevationAsset.Slice(profile, BakedElevationAsset.SlicePart.TopLeft);
        TopEdge.Source = BakedElevationAsset.Slice(profile, BakedElevationAsset.SlicePart.TopEdge);
        TopRight.Source = BakedElevationAsset.Slice(profile, BakedElevationAsset.SlicePart.TopRight);
        LeftEdge.Source = BakedElevationAsset.Slice(profile, BakedElevationAsset.SlicePart.LeftEdge);
        RightEdge.Source = BakedElevationAsset.Slice(profile, BakedElevationAsset.SlicePart.RightEdge);
        BottomLeft.Source = BakedElevationAsset.Slice(profile, BakedElevationAsset.SlicePart.BottomLeft);
        BottomEdge.Source = BakedElevationAsset.Slice(profile, BakedElevationAsset.SlicePart.BottomEdge);
        BottomRight.Source = BakedElevationAsset.Slice(profile, BakedElevationAsset.SlicePart.BottomRight);
    }
}
