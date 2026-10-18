using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Scriblucent.Behaviors;
using Scriblucent.Icons;
using Scriblucent.Localization;
using Scriblucent.Services;

namespace Scriblucent.Controls;

/// <summary>Drag for distance; direct one-click buttons for a precise step.</summary>
public sealed class ItemReorderControls : Border
{
    public ItemReorderGrip Grip { get; } = new();
    public Button EarlierButton { get; }
    public Button LaterButton { get; }
    private INotifyCollectionChanged? _items;
    private ICollectionSelectionScope? _selection;
    private bool _queued;

    public ItemReorderControls()
    {
        SetResourceReference(StyleProperty, "ReorderControlTrayStyle");
        VerticalAlignment = VerticalAlignment.Center;
        EarlierButton = CreateStep(false); LaterButton = CreateStep(true);
        Grip.SetResourceReference(StyleProperty, "ReorderTrayButtonStyle");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Grip); buttons.Children.Add(EarlierButton); buttons.Children.Add(LaterButton);
        Child = buttons;
        Loaded += (_, _) => Observe();
        Unloaded += (_, _) => Unobserve();
        DataContextChanged += (_, _) => { if (IsLoaded) Observe(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) QueueRefresh(); };
    }
    private Button CreateStep(bool after)
    {
        var size = DashboardChromeMetrics.Number(this, "Control.Size.Reorder", 24);
        var button = new Button { Width = size, Height = size, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(), Margin = new Thickness(2, 0, 0, 0),
            Content = new AppIcon { Kind = after ? AppIconKind.ArrowDown : AppIconKind.ArrowUp, Width = 14, Height = 14 } };
        button.SetResourceReference(StyleProperty, "ReorderTrayButtonStyle");
        var label = new Binding(after ? "[ReorderLater]" : "[ReorderEarlier]") { Source = LocalizationProvider.Instance };
        button.SetBinding(ToolTipProperty, label);
        button.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, label);
        button.Click += (_, e) => { ItemReorderBehavior.MoveFromHandle(button, after); QueueRefresh(); e.Handled = true; };
        return button;
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == CollectionSelectionFrame.SelectionProperty && IsLoaded) Observe();
    }
    private void Observe()
    {
        Unobserve();
        _items = ItemReorderBehavior.FindReorderList(this)?.Items;
        if (_items is not null) _items.CollectionChanged += ItemsChanged;
        _selection = CollectionSelectionFrame.GetSelection(this);
        if (_selection is not null) _selection.Changed += QueueRefresh;
        QueueRefresh();
    }
    private void Unobserve()
    {
        if (_items is not null) _items.CollectionChanged -= ItemsChanged;
        if (_selection is not null) _selection.Changed -= QueueRefresh;
        _items = null; _selection = null;
    }
    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRefresh();
    private void QueueRefresh()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            _queued = false;
            if (!IsLoaded) return;
            EarlierButton.IsEnabled = ItemReorderBehavior.CanMoveFromHandle(this, false);
            LaterButton.IsEnabled = ItemReorderBehavior.CanMoveFromHandle(this, true);
        });
    }
}
