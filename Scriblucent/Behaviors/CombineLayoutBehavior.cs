using System.Windows;
using System.Windows.Controls;

namespace Scriblucent.Behaviors;

/// <summary>Keeps the library viewport finite while moving its configuration above it on narrow windows.</summary>
public static class CombineLayoutBehavior
{
    private static readonly DependencyPropertyKey IsCompactPropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsCompact", typeof(bool), typeof(CombineLayoutBehavior), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCompactProperty = IsCompactPropertyKey.DependencyProperty;
    public static bool GetIsCompact(DependencyObject target) => (bool)target.GetValue(IsCompactProperty);
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(CombineLayoutBehavior), new PropertyMetadata(false, Changed));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(CombineLayoutBehavior));
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Grid grid) return;
        (grid.GetValue(StateProperty) as State)?.Dispose();
        grid.ClearValue(StateProperty);
        if (e.NewValue is true) grid.SetValue(StateProperty, new State(grid));
    }
    private sealed class State : IDisposable
    {
        private readonly Grid _grid;
        private bool? _compact;
        private bool _compactExpanded;
        private bool _wideExpanded = true;
        private Expander? _options;
        internal State(Grid grid) { _grid = grid; grid.Loaded += Loaded; grid.SizeChanged += Sized; }
        private void Loaded(object sender, RoutedEventArgs e) => Update();
        private void Sized(object sender, SizeChangedEventArgs e) => Update();
        private void Update()
        {
            if (_grid.Children.Count != 2 || _grid.ActualWidth <= 0) return;
            var compact = _grid.ActualWidth < 880;
            var optionsAvailable = _options is null;
            _options ??= LogicalTreeHelper.FindLogicalNode(_grid, "ConfigurationOptions") as Expander;
            optionsAvailable &= _options is not null;
            var modeChanged = _compact != compact;
            if (modeChanged)
            {
                if (_options is not null && !optionsAvailable && _compact is { } previous)
                {
                    if (previous) _compactExpanded = _options.IsExpanded;
                    else _wideExpanded = _options.IsExpanded;
                }
                _compact = compact;
                _grid.SetValue(IsCompactPropertyKey, compact);
                _grid.ColumnDefinitions.Clear(); _grid.RowDefinitions.Clear();
                _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
                if (!compact)
                {
                    _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                    _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                }
                _grid.RowDefinitions.Add(new RowDefinition { Height = compact ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
                if (compact) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var library = (FrameworkElement)_grid.Children[0];
                var configuration = (FrameworkElement)_grid.Children[1];
                Grid.SetColumn(library, 0); Grid.SetRow(library, compact ? 1 : 0);
                Grid.SetColumn(configuration, compact ? 0 : 2); Grid.SetRow(configuration, 0);
                System.Windows.Input.KeyboardNavigation.SetTabIndex(library, compact ? 1 : 0);
                System.Windows.Input.KeyboardNavigation.SetTabIndex(configuration, compact ? 0 : 1);
                configuration.Margin = compact ? new Thickness(0, 0, 0, 16) : new Thickness(0);
            }
            if (_options is not null && (modeChanged || optionsAvailable))
                _options.SetCurrentValue(Expander.IsExpandedProperty, compact ? _compactExpanded : _wideExpanded);
            // Fit the title's two-line localized label, disclosure, padding and lower gap.
            var maximum = compact ? Math.Max(112, _grid.ActualHeight * .4) : double.PositiveInfinity;
            _grid.RowDefinitions[0].MaxHeight = maximum;
            var configurationPanel = (FrameworkElement)_grid.Children[1];
            configurationPanel.MaxHeight = Math.Max(1, maximum - configurationPanel.Margin.Top - configurationPanel.Margin.Bottom);
        }
        public void Dispose() { _grid.Loaded -= Loaded; _grid.SizeChanged -= Sized; }
    }
}
