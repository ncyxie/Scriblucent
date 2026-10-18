using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>Standalone cards share one close shadow. The shadow stays outside clips and scroll bitmap replacements.</summary>
public sealed class ElevationHost : Grid
{
    private readonly CardShadowLayer _shadow = new();
    public ElevationHost()
    {
        IsHitTestVisible = true; ClipToBounds = false;
        Children.Add(_shadow);
        Loaded += (_, _) =>
        {
            foreach (UIElement child in Children)
            {
                if (ReferenceEquals(child, _shadow)) continue;
                var face = child as Border ?? SurfacePaint.FindBorder(child);
                if (face is null) continue;
                _shadow.SetBinding(RenderTransformProperty, new Binding(nameof(RenderTransform)) { Source = face });
                _shadow.SetBinding(RenderTransformOriginProperty, new Binding(nameof(RenderTransformOrigin)) { Source = face });
                break;
            }
        };
    }
}
