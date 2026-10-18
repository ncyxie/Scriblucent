using System.Windows;
using System.Windows.Controls;
using Scriblucent.Behaviors;

namespace Scriblucent.Controls;

/// <summary>Requests logical selection; native toggling never competes with the bound state.</summary>
public sealed class CollectionSelectionToggle : CheckBox
{
    static CollectionSelectionToggle()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(CollectionSelectionToggle), new FrameworkPropertyMetadata(typeof(CollectionSelectionToggle)));
    }
    internal static bool IsSelectionToggle(DependencyObject? source)
    {
        for (var p = source; p is not null; p = EditorSelectionInput.Parent(p))
            if (p is CollectionSelectionToggle) return true;
        return false;
    }
    internal Action? ToggleRequested;
    protected override void OnToggle() => ToggleRequested?.Invoke();
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        CheckBoxCheckMotionBehavior.SyncCheckBoxGlyph(this);
    }
}
