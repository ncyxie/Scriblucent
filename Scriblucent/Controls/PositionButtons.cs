using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Scriblucent.Icons;

namespace Scriblucent.Controls;

/// <summary>Direct section positioning with stable geometry at either boundary.</summary>
public sealed class PositionButtons : Border
{
    public static readonly DependencyProperty UpLabelProperty = DependencyProperty.Register(nameof(UpLabel), typeof(object), typeof(PositionButtons));
    public static readonly DependencyProperty DownLabelProperty = DependencyProperty.Register(nameof(DownLabel), typeof(object), typeof(PositionButtons));
    public static readonly DependencyProperty CanMoveUpProperty = DependencyProperty.Register(nameof(CanMoveUp), typeof(bool), typeof(PositionButtons), new PropertyMetadata(false));
    public static readonly DependencyProperty CanMoveDownProperty = DependencyProperty.Register(nameof(CanMoveDown), typeof(bool), typeof(PositionButtons), new PropertyMetadata(false));
    public object? UpLabel { get => GetValue(UpLabelProperty); set => SetValue(UpLabelProperty, value); }
    public object? DownLabel { get => GetValue(DownLabelProperty); set => SetValue(DownLabelProperty, value); }
    public bool CanMoveUp { get => (bool)GetValue(CanMoveUpProperty); set => SetValue(CanMoveUpProperty, value); }
    public bool CanMoveDown { get => (bool)GetValue(CanMoveDownProperty); set => SetValue(CanMoveDownProperty, value); }
    public static readonly RoutedEvent MoveUpEvent = EventManager.RegisterRoutedEvent(nameof(MoveUp), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PositionButtons));
    public static readonly RoutedEvent MoveDownEvent = EventManager.RegisterRoutedEvent(nameof(MoveDown), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PositionButtons));
    public event RoutedEventHandler MoveUp { add => AddHandler(MoveUpEvent, value); remove => RemoveHandler(MoveUpEvent, value); }
    public event RoutedEventHandler MoveDown { add => AddHandler(MoveDownEvent, value); remove => RemoveHandler(MoveDownEvent, value); }
    public Button UpButton { get; }
    public Button DownButton { get; }
    public PositionButtons()
    {
        SetResourceReference(StyleProperty, "ReorderControlTrayStyle");
        VerticalAlignment = VerticalAlignment.Center;
        UpButton = Create(false); DownButton = Create(true);
        DownButton.Margin = new Thickness(2, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(UpButton); buttons.Children.Add(DownButton);
        Child = buttons;
    }
    private Button Create(bool down)
    {
        var size = DashboardChromeMetrics.Number(this, "Control.Size.Section", 28);
        var button = new Button { Width = size, Height = size, MinWidth = 0, MinHeight = 0, Padding = new Thickness(), Focusable = true,
            Content = new AppIcon { Kind = down ? AppIconKind.ArrowDown : AppIconKind.ArrowUp, Width = 14, Height = 14 } };
        button.SetResourceReference(StyleProperty, "ReorderTrayButtonStyle");
        button.SetBinding(IsEnabledProperty, new Binding(down ? nameof(CanMoveDown) : nameof(CanMoveUp)) { Source = this });
        var label = new Binding(down ? nameof(DownLabel) : nameof(UpLabel)) { Source = this };
        button.SetBinding(ToolTipProperty, label);
        button.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, label);
        button.Click += (_, e) => { Move(down); e.Handled = true; };
        return button;
    }
    private void Move(bool down)
    {
        if (down ? CanMoveDown : CanMoveUp) RaiseEvent(new RoutedEventArgs(down ? MoveDownEvent : MoveUpEvent, this));
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && key is Key.Up or Key.Down)
        { Move(key == Key.Down); e.Handled = true; }
    }
}
