using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Scriblucent.Behaviors;

/// <summary>
/// A scrollbar thumb captures the mouse. Combo boxes and StaysOpen=false popups treat that
/// capture change as a click outside and close at the start of the drag. Hold them open
/// until the thumb is released.
/// </summary>
internal static class PopupScrollDragBehavior
{
    private sealed class Hold
    {
        public Popup? Popup;
        public bool PopupStaysOpen;
        public ContextMenu? Menu;
        public bool MenuStaysOpen;
        public ComboBox? Combo;
    }

    private static readonly List<Hold> Held = [];
    private static bool _restoringCombo;

    public static void Register()
    {
        EventManager.RegisterClassHandler(
            typeof(ScrollBar),
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnScrollBarMouseDown));
        EventManager.RegisterClassHandler(
            typeof(UIElement),
            UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnMouseUp),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(Thumb),
            UIElement.LostMouseCaptureEvent,
            new MouseEventHandler(OnThumbLostCapture),
            handledEventsToo: true);
        EventManager.RegisterClassHandler(
            typeof(ComboBox),
            Mouse.LostMouseCaptureEvent,
            new MouseEventHandler(OnComboLostCapture),
            handledEventsToo: true);
    }

    private static void OnScrollBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ScrollBar bar)
            return;

        var popup = FindPopup(bar);
        var menu = FindAncestor<ContextMenu>(bar);
        var combo = popup?.TemplatedParent as ComboBox
            ?? popup?.PlacementTarget as ComboBox
            ?? FindAncestor<ComboBox>(bar);
        if (popup is null && menu is null && combo is null)
            return;

        if (Held.Exists(hold => ReferenceEquals(hold.Popup, popup) && ReferenceEquals(hold.Menu, menu)))
            return;

        var hold = new Hold { Popup = popup, Menu = menu, Combo = combo };
        if (popup is not null)
        {
            hold.PopupStaysOpen = popup.StaysOpen;
            popup.StaysOpen = true;
        }

        if (menu is not null)
        {
            hold.MenuStaysOpen = menu.StaysOpen;
            menu.StaysOpen = true;
        }

        Held.Add(hold);
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Held.Count == 0)
            return;

        Dispatcher.CurrentDispatcher.BeginInvoke(ReleaseAll, DispatcherPriority.Input);
    }

    private static void OnThumbLostCapture(object sender, MouseEventArgs e)
    {
        if (Held.Count == 0 || Mouse.Captured is not null)
            return;

        Dispatcher.CurrentDispatcher.BeginInvoke(ReleaseAll, DispatcherPriority.Background);
    }

    private static void OnComboLostCapture(object sender, MouseEventArgs e)
    {
        if (_restoringCombo || sender is not ComboBox combo || combo.IsDropDownOpen)
            return;
        if (Mouse.Captured is not IInputElement captured || captured is not DependencyObject capturedObject)
            return;
        if (FindAncestor<ScrollBar>(capturedObject) is null)
            return;
        if (!Held.Exists(hold => ReferenceEquals(hold.Combo, combo)))
            return;

        _restoringCombo = true;
        try
        {
            combo.IsDropDownOpen = true;
            Mouse.Capture(captured);
        }
        finally
        {
            _restoringCombo = false;
        }
    }

    private static void ReleaseAll()
    {
        if (Held.Count == 0)
            return;

        var holds = Held.ToArray();
        Held.Clear();
        foreach (var hold in holds)
        {
            if (hold.Popup is not null)
                hold.Popup.StaysOpen = hold.PopupStaysOpen;
            if (hold.Menu is not null)
                hold.Menu.StaysOpen = hold.MenuStaysOpen;
        }
    }

    private static Popup? FindPopup(DependencyObject source)
    {
        for (var current = source; current is not null;)
        {
            if (current is Popup popup)
                return popup;

            var parent = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            if (parent is null && current.GetType().Name == "PopupRoot")
            {
                var property = current.GetType().GetProperty(
                    "Parent",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
                parent = property?.GetValue(current) as DependencyObject;
            }

            current = parent;
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }

        return null;
    }
}
