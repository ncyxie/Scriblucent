using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Scriblucent.Behaviors;
using Scriblucent.Icons;
using Scriblucent.Localization;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>A compact, keyboard-focusable drag affordance using the existing control material.</summary>
public sealed class ItemReorderGrip : Button
{
    public ItemReorderGrip()
    {
        SetResourceReference(StyleProperty, "ToolbarIconButtonStyle");
        Content = new AppIcon { Kind = AppIconKind.GripVertical, Width = 14, Height = 14 };
        Width = Height = DashboardChromeMetrics.Number(this, "Control.Size.Reorder", 24);
        MinWidth = 0; MinHeight = 0; Padding = new Thickness(0);
        VerticalAlignment = VerticalAlignment.Center; Cursor = Cursors.SizeAll; Focusable = true;
        ItemReorderBehavior.SetIsHandle(this, true);
        ContextMenu = new ContextMenu();
        ItemReorderBehavior.SetMenuActions(ContextMenu, true);
        SetBinding(ToolTipProperty, new Binding("[ReorderHandleHint]") { Source = LocalizationProvider.Instance });
        SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,
            new Binding("[ReorderOptions]") { Source = LocalizationProvider.Instance });
    }
    protected override void OnClick()
    {
        base.OnClick();
        if (ContextMenu is not { } menu || ItemReorderBehavior.IsDragging) return;
        ItemReorderBehavior.PrepareContextMenu(menu, this);
        if (menu.Items.Count > 0) UiMotion.OpenContextMenu(menu, this);
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Up or Key.Down)
        { ItemReorderBehavior.MoveFromHandle(this, key == Key.Down); e.Handled = true; }
    }
}
