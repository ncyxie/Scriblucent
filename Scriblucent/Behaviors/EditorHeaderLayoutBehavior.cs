using System.Windows;
using System.Windows.Controls;
using System.Runtime.CompilerServices;
using Scriblucent.Controls;

namespace Scriblucent.Behaviors;

/// <summary>Keep a readable title; move dense command bands below it when they cannot fit.</summary>
public static class EditorHeaderLayoutBehavior
{
    private static readonly ConditionalWeakTable<Grid, State> States = new();
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(EditorHeaderLayoutBehavior), new PropertyMetadata(false, Changed));
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Grid grid || !(bool)e.NewValue) return;
        grid.Loaded += (_, _) => States.GetValue(grid, g => new State(g)).Update();
    }
    private sealed class State
    {
        private readonly Grid _grid;
        private readonly FrameworkElement _title, _tray;
        private readonly int _row;
        private readonly Thickness _margin;
        private bool _stacked;
        public State(Grid grid)
        {
            _grid = grid;
            _title = grid.Children.OfType<FrameworkElement>().Single(child => child.Name == "EditorTitleField");
            _tray = grid.Children.OfType<FrameworkElement>().Single(child => child.Name == "EditorCommandTray");
            _row = Grid.GetRow(_title);
            _margin = _tray.Margin;
            // Reserve the optional command row once; the normal one-line layout gives it zero height.
            grid.RowDefinitions.Insert(_row + 1, new RowDefinition { Height = GridLength.Auto });
            foreach (var child in grid.Children.OfType<UIElement>())
                if (Grid.GetRow(child) > _row) Grid.SetRow(child, Grid.GetRow(child) + 1);
            grid.SizeChanged += (_, _) => Update();
            if (FindCommands(_tray) is { } panel) panel.SizeChanged += (_, _) => Update();
        }
        public void Update()
        {
            if (_grid.ActualWidth <= 0) return;
            var panel = FindCommands(_tray);
            var width = panel?.Children.OfType<FrameworkElement>().Sum(child => child.DesiredSize.Width) ?? _tray.DesiredSize.Width;
            var stacked = width + 280 > _grid.ActualWidth;
            if (stacked == _stacked) return;
            _stacked = stacked;
            Grid.SetColumnSpan(_title, stacked ? 2 : 1);
            Grid.SetColumn(_tray, stacked ? 0 : 1);
            Grid.SetColumnSpan(_tray, stacked ? 2 : 1);
            Grid.SetRow(_tray, _row + (stacked ? 1 : 0));
            _tray.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            _tray.Margin = stacked ? new Thickness(0, 4, 0, 0) : _margin;
            if (panel is not null)
            {
                panel.CanWrap = stacked;
                panel.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            }
        }
        private static EditorCommandPanel? FindCommands(FrameworkElement element)
        {
            if (element is EditorCommandPanel wrap) return wrap;
            if (element is Border { Child: FrameworkElement child }) return FindCommands(child);
            if (element is Grid grid) return grid.Children.OfType<FrameworkElement>().Select(FindCommands).FirstOrDefault(p => p is not null);
            return null;
        }
    }
}
