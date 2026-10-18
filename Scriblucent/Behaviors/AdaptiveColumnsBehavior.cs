using System.Windows;
using System.Windows.Controls;

namespace Scriblucent.Behaviors;

/// <summary>Two-column settings rows become a stable vertical flow on narrow owners.</summary>
public static class AdaptiveColumnsBehavior
{
    public static readonly DependencyProperty BreakpointProperty = DependencyProperty.RegisterAttached(
        "Breakpoint", typeof(double), typeof(AdaptiveColumnsBehavior), new PropertyMetadata(520d, BreakpointChanged),
        value => value is double number && double.IsFinite(number) && number > 0);
    public static void SetBreakpoint(DependencyObject target, double value) => target.SetValue(BreakpointProperty, value);
    public static double GetBreakpoint(DependencyObject target) => (double)target.GetValue(BreakpointProperty);
    private static void BreakpointChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    { if (target.GetValue(StateProperty) is State state) state.Update(); }
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(AdaptiveColumnsBehavior), new PropertyMetadata(false, Changed));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(AdaptiveColumnsBehavior));
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Grid grid) return;
        (grid.GetValue(StateProperty) as State)?.Dispose(); grid.ClearValue(StateProperty);
        if (e.NewValue is true) grid.SetValue(StateProperty, new State(grid));
    }
    private sealed class State
    {
        private readonly Grid _grid;
        private ColumnDefinition[]? _columns;
        private RowDefinition[]? _rows;
        private (FrameworkElement Child, int Row, int Column, int Span, Thickness Margin, double Height)[] _children = [];
        private bool? _stacked;
        internal State(Grid grid) { _grid = grid; grid.Loaded += Loaded; grid.SizeChanged += Sized; }
        private void Loaded(object sender, RoutedEventArgs e)
        {
            if (_columns == null)
            {
                _columns = _grid.ColumnDefinitions.ToArray(); _rows = _grid.RowDefinitions.ToArray();
                _children = _grid.Children.OfType<FrameworkElement>().Select(c => (c, Grid.GetRow(c), Grid.GetColumn(c), Grid.GetColumnSpan(c), c.Margin, c.Height)).ToArray();
            }
            Update();
        }
        private void Sized(object sender, SizeChangedEventArgs e) { if (_columns != null) Update(); }
        internal void Update()
        {
            var stacked = _grid.ActualWidth <= GetBreakpoint(_grid);
            if (_stacked == stacked || _columns == null || _rows == null) return;
            _stacked = stacked;
            _grid.ColumnDefinitions.Clear(); _grid.RowDefinitions.Clear();
            if (!stacked)
            {
                foreach (var c in _columns) _grid.ColumnDefinitions.Add(c);
                foreach (var r in _rows) _grid.RowDefinitions.Add(r);
                foreach (var item in _children)
                { Grid.SetRow(item.Child, item.Row); Grid.SetColumn(item.Child, item.Column); Grid.SetColumnSpan(item.Child, item.Span); item.Child.Margin = item.Margin; item.Child.Height = item.Height; }
                return;
            }
            var rowCount = _children.Select(c => c.Row).DefaultIfEmpty(0).Max() + 1;
            var spacer = _columns.Length == 3 && _columns[1].Width.IsAbsolute && _columns[1].Width.Value == 22;
            var flowColumns = spacer ? 2 : Math.Max(1, _columns.Length);
            for (var i = 0; i < rowCount * flowColumns; i++) _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (var item in _children)
            {
                var flowColumn = item.Span > 1 ? 0 : spacer ? item.Column / 2 : item.Column;
                Grid.SetRow(item.Child, item.Row * flowColumns + flowColumn); Grid.SetColumn(item.Child, 0); Grid.SetColumnSpan(item.Child, 1);
                // The middle spacer in three-column groups has no visual purpose when stacked.
                item.Child.Margin = flowColumn > 0 ? new Thickness(0, spacer ? 16 : 8, 0, item.Margin.Bottom) : item.Margin;
                if (spacer && item.Column == 1) item.Child.Height = 0;
            }
        }
        internal void Dispose() { _grid.Loaded -= Loaded; _grid.SizeChanged -= Sized; }
    }
}
