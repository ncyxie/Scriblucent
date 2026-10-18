using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Converters;
using Scriblucent.Localization;

namespace Scriblucent.Behaviors;

/// <summary>Fit a small choice set once per content/typography change, never per selection or frame.</summary>
public static class DropdownChoiceWidthBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DropdownChoiceWidthBehavior), new PropertyMetadata(false, Changed));
    public static void SetIsEnabled(DependencyObject target, bool value) => target.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject target) => (bool)target.GetValue(IsEnabledProperty);
    public static readonly DependencyProperty MaxFitWidthProperty = DependencyProperty.RegisterAttached(
        "MaxFitWidth", typeof(double), typeof(DropdownChoiceWidthBehavior), new PropertyMetadata(260d, LimitChanged),
        value => value is double width && double.IsFinite(width) && width > 0);
    public static void SetMaxFitWidth(DependencyObject target, double value) => target.SetValue(MaxFitWidthProperty, value);
    public static double GetMaxFitWidth(DependencyObject target) => (double)target.GetValue(MaxFitWidthProperty);
    private static readonly DependencyPropertyKey FittedWidthKey = DependencyProperty.RegisterAttachedReadOnly(
        "FittedWidth", typeof(double), typeof(DropdownChoiceWidthBehavior), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FittedWidthProperty = FittedWidthKey.DependencyProperty;
    public static double GetFittedWidth(DependencyObject target) => (double)target.GetValue(FittedWidthProperty);
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(WidthState), typeof(DropdownChoiceWidthBehavior));
    private static readonly DependencyPropertyDescriptor ContentDescriptor = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ComboBoxItem));
    private static readonly DependencyPropertyDescriptor[] Metrics = new[]
    {
        Control.FontFamilyProperty, Control.FontSizeProperty, Control.FontWeightProperty,
        Control.FontStyleProperty, Control.FontStretchProperty, Control.PaddingProperty,
        Control.BorderThicknessProperty, FrameworkElement.FlowDirectionProperty, ComboBox.IsEditableProperty
    }.Select(property => DependencyPropertyDescriptor.FromProperty(property, typeof(ComboBox))).ToArray();

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ComboBox combo) return;
        combo.Loaded -= Loaded; combo.Unloaded -= Unloaded; Detach(combo);
        if (args.NewValue is not true) { combo.SetValue(FittedWidthKey, double.NaN); return; }
        combo.Loaded += Loaded; combo.Unloaded += Unloaded;
        if (combo.IsLoaded) Attach(combo);
    }
    private static void LimitChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    { if (target.GetValue(StateProperty) is WidthState state) state.Queue(); }
    private static void Loaded(object sender, RoutedEventArgs args) => Attach((ComboBox)sender);
    private static void Unloaded(object sender, RoutedEventArgs args) => Detach((ComboBox)sender);
    private static void Attach(ComboBox combo)
    {
        if (combo.GetValue(StateProperty) is not null) return;
        var state = new WidthState(combo); combo.SetValue(StateProperty, state); state.Attach();
    }
    private static void Detach(ComboBox combo)
    {
        if (combo.GetValue(StateProperty) is not WidthState state) return;
        state.Detach(); combo.ClearValue(StateProperty);
    }

    internal static double MeasureWidth(IReadOnlyList<string> labels, ComboBox combo, double? pixelsPerDip = null)
    {
        var face = new Typeface(combo.FontFamily, combo.FontStyle, combo.FontWeight, combo.FontStretch);
        double widest = 0, dpi = pixelsPerDip ?? VisualTreeHelper.GetDpi(combo).PixelsPerDip;
        foreach (var text in labels)
            widest = Math.Max(widest, new FormattedText(text, CultureInfo.CurrentUICulture, combo.FlowDirection,
                face, combo.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace);
        var inset = (Thickness)ComboBoxContentInsetConverter.Instance.Convert(
            [combo.Padding, combo.BorderThickness, combo.IsEditable], typeof(Thickness), null!, CultureInfo.CurrentUICulture);
        return Math.Min(GetMaxFitWidth(combo), Math.Ceiling((widest + inset.Left + inset.Right + 2) / 4) * 4);
    }

    private sealed class WidthState(ComboBox combo)
    {
        private readonly List<ComboBoxItem> _observed = [];
        private DispatcherOperation? _pending;
        internal void Attach()
        {
            ((INotifyCollectionChanged)combo.Items).CollectionChanged += ItemsChanged;
            LocalizationService.CultureChanged += CultureChanged;
            foreach (var metric in Metrics) metric.AddValueChanged(combo, MetricsChanged);
            Measure();
        }
        internal void Detach()
        {
            _pending?.Abort();
            ((INotifyCollectionChanged)combo.Items).CollectionChanged -= ItemsChanged;
            LocalizationService.CultureChanged -= CultureChanged;
            foreach (var metric in Metrics) metric.RemoveValueChanged(combo, MetricsChanged);
            Unobserve();
        }
        private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) => Queue();
        private void CultureChanged(object? sender, EventArgs args) => Queue();
        private void MetricsChanged(object? sender, EventArgs args) => Queue();
        internal void Queue()
        {
            if (_pending is { Status: DispatcherOperationStatus.Pending }) return;
            _pending = combo.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _pending = null; if (combo.IsLoaded) Measure(); }));
        }
        private void Unobserve()
        {
            foreach (var item in _observed) ContentDescriptor.RemoveValueChanged(item, MetricsChanged);
            _observed.Clear();
        }
        private void Measure()
        {
            Unobserve();
            // Explicitly opt in only finite controls. A mistakenly enabled library picker must
            // never materialize or scan a whole document library to decide its chrome width.
            if (combo.Items.Count is 0 or > 128) { combo.SetValue(FittedWidthKey, double.NaN); return; }
            var labels = new List<string>(combo.Items.Count);
            foreach (var value in combo.Items)
            {
                var text = value as string;
                if (value is ComboBoxItem item)
                {
                    text = item.Content as string; _observed.Add(item); ContentDescriptor.AddValueChanged(item, MetricsChanged);
                }
                if (text is null) { combo.SetValue(FittedWidthKey, double.NaN); return; }
                labels.Add(text);
            }
            combo.SetValue(FittedWidthKey, MeasureWidth(labels, combo));
        }
    }
}
