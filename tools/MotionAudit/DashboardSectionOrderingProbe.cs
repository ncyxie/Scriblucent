using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Scriblucent.Converters;

internal static partial class Program
{
    private static void ProbeDashboardSectionOrdering(FrameworkElement page, string dashboard, Action<string> mark)
    {
        var buttons = ControlTree(page).OfType<Scriblucent.Controls.PositionButtons>().ToArray();
        Require(buttons.Length == 4, $"{dashboard}: expected direct positioning controls on each of four sections.");
        foreach (var button in buttons)
        {
            var binding = BindingOperations.GetMultiBinding(button, Scriblucent.Controls.PositionButtons.CanMoveUpProperty)!;
            Require(binding?.Converter is DashboardSectionMoveConverter, "Section arrows lost their actual boundary binding.");
            var own = (FrameworkElement)page.FindName(((Binding)binding!.Bindings[0]).ElementName);
            var peer = (FrameworkElement)page.FindName(((Binding)binding.Bindings[1]).ElementName);
            using var ownRow = new SavedSectionProperty(own, Grid.RowProperty);
            using var peerRow = new SavedSectionProperty(peer, Grid.RowProperty);
            using var visibility = new SavedSectionProperty(peer, UIElement.VisibilityProperty);
            foreach (var first in new[] { true, false })
            foreach (var visible in new[] { Visibility.Visible, Visibility.Collapsed, Visibility.Hidden })
            {
                own.SetCurrentValue(Grid.RowProperty, first ? 0 : 1);
                peer.SetCurrentValue(Grid.RowProperty, first ? 1 : 0);
                peer.SetCurrentValue(UIElement.VisibilityProperty, visible); Pump(25);
                bool up = visible == Visibility.Visible && !first, down = visible == Visibility.Visible && first;
                Require(button.CanMoveUp == up && button.CanMoveDown == down, $"{dashboard}/{own.Name}: wrong menu boundary state.");
                Require(button.UpButton.IsEnabled == up && button.DownButton.IsEnabled == down,
                    "Direct arrows disagreed with the section boundary.");
                Require(button.UpButton.Width == 28 && button.DownButton.Width == 28, "Section arrows lost their stable dimensions.");
            }
            own.SetCurrentValue(Grid.RowProperty, 0); peer.SetCurrentValue(Grid.RowProperty, 0);
            peer.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible); Pump(25);
            Require(!button.CanMoveUp && !button.CanMoveDown, "Equal/unsettled rows offered a reorder direction.");
        }
        Pump(30);
        foreach (var button in buttons.Where(button => button.IsVisible && (button.CanMoveUp || button.CanMoveDown)))
        {
            var binding = BindingOperations.GetMultiBinding(button, Scriblucent.Controls.PositionButtons.CanMoveUpProperty)!;
            var own = (FrameworkElement)page.FindName(((Binding)binding.Bindings[0]).ElementName);
            int row = Grid.GetRow(own);
            bool down = !button.CanMoveUp;
            button.BringIntoView(); Pump(100);
            InvokeOrderingButton(down ? button.DownButton : button.UpButton); Pump(550);
            Require(Grid.GetRow(own) != row, $"{dashboard}: one-click arrow did not move its real section.");
            Require((down ? button.UpButton : button.DownButton).IsEnabled, "Moved section did not offer the reverse direction.");
            InvokeOrderingButton(down ? button.UpButton : button.DownButton); Pump(550);
            Require(Grid.GetRow(own) == row, "Section could not restore its original order through the direct arrows.");
            Border? tray = null;
            for (var ancestor = VisualTreeHelper.GetParent(button); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is Border frame) { tray = frame; break; }
            Require(tray is not null && ControlTree(tray).OfType<Button>().Count() == 3,
                "Section movement and disclosure do not share one action strip.");
            if (tray!.Parent is FrameworkElement header)
                SaveLocalControlCapture(header, $"section-strip-{dashboard}-{own.Name}");
        }
        mark($"PASS {dashboard}: direct section arrows follow both orders, hidden/empty peers and boundaries; actual one-click moves round-trip");
    }
    private sealed class SavedSectionProperty(DependencyObject target, DependencyProperty property) : IDisposable
    {
        private readonly BindingBase? _binding = BindingOperations.GetBindingBase(target, property);
        private readonly object _local = target.ReadLocalValue(property);
        public void Dispose()
        {
            if (_binding is not null) BindingOperations.SetBinding(target, property, _binding);
            else if (_local == DependencyProperty.UnsetValue) target.ClearValue(property);
            else target.SetValue(property, _local);
        }
    }
}
