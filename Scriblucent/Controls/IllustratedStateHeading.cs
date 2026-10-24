using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Data;
using System.Windows.Media;

namespace Scriblucent.Controls;

/// <summary>Balances short illustrated-state headlines without imposing language-specific line breaks.</summary>
public sealed class IllustratedStateHeading : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(IllustratedStateHeading), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(nameof(LineHeight), typeof(double), typeof(IllustratedStateHeading), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double LineHeight { get => (double)GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, LineStackingStrategy = LineStackingStrategy.MaxHeight };

    public IllustratedStateHeading()
    {
        Focusable = false;
        AddVisualChild(_text); AddLogicalChild(_text);
        foreach (var (property, name) in new[]
        {
            (TextBlock.TextProperty, nameof(Text)), (TextBlock.LineHeightProperty, nameof(LineHeight)),
            (TextBlock.FontFamilyProperty, nameof(FontFamily)), (TextBlock.FontSizeProperty, nameof(FontSize)),
            (TextBlock.FontWeightProperty, nameof(FontWeight)), (TextBlock.FontStyleProperty, nameof(FontStyle)),
            (TextBlock.FontStretchProperty, nameof(FontStretch)), (TextBlock.ForegroundProperty, nameof(Foreground))
        }) _text.SetBinding(property, new Binding(name) { Source = this });
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _text : throw new ArgumentOutOfRangeException(nameof(index));
    protected override System.Collections.IEnumerator LogicalChildren => new[] { _text }.GetEnumerator();
    protected override AutomationPeer OnCreateAutomationPeer() => new HeadingPeer(this);

    private sealed class HeadingPeer(IllustratedStateHeading owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.Text;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // Measure full text even in a short viewport; a clipped height must not look
        // like permission to squeeze additional lines into a narrower column.
        Size Measure(Size size) { _text.Measure(new Size(size.Width, double.PositiveInfinity)); return _text.DesiredSize; }
        var original = Measure(constraint);
        if (!double.IsFinite(constraint.Width)
            || constraint.Width <= 1 || string.IsNullOrEmpty(Text) || Text.Contains('\n'))
            return original;

        var singleLine = Measure(new Size(double.PositiveInfinity, constraint.Height));
        if (original.Height <= singleLine.Height + .5)
            return Measure(constraint);

        // Find the narrowest measure retaining the original line count. This brings the
        // lines closer in length while preserving all content and the available width.
        double low = 1, high = constraint.Width;
        while (high - low > 1)
        {
            var width = (low + high) / 2;
            var candidate = Measure(new Size(width, constraint.Height));
            if (candidate.Height <= original.Height + .5) high = width;
            else low = width;
        }
        var balancedWidth = Math.Min(constraint.Width, high + 1);
        var balanced = Measure(new Size(balancedWidth, constraint.Height));
        return new Size(balancedWidth, balanced.Height);
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        _text.Arrange(new Rect(arrangeBounds));
        return arrangeBounds;
    }
}
