using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>The animated edge owns clipping; a narrower Grid slot must not hide the closing glide.</summary>
public sealed class SidebarChrome : Border
{
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) =>
        SidebarResizeMotion.GetIsEnabled(this) ? null : base.GetLayoutClip(layoutSlotSize);
}
