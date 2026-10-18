using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Scriblucent.Controls;

/// <summary>Centers the visible numerals, rather than the font's asymmetric line box.</summary>
public sealed class CalendarDayLabel : Control
{
    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(
        nameof(Content), typeof(object), typeof(CalendarDayLabel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }

    private FormattedText? _text;
    private Rect _ink;
    private TextKey? _key;
    private readonly record struct TextKey(string Value, string Family, FontStyle Style,
        FontWeight Weight, FontStretch Stretch, double Size, double Dpi, string Culture, FlowDirection Flow);

    static CalendarDayLabel()
    {
        FocusableProperty.OverrideMetadata(typeof(CalendarDayLabel), new FrameworkPropertyMetadata(false));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(CalendarDayLabel), new FrameworkPropertyMetadata(false));
        // Control normally delegates these properties to its template. This label paints
        // directly, so inherited theme/font changes must invalidate its own drawing too.
        foreach (var property in new[] { ForegroundProperty, FontFamilyProperty, FontSizeProperty,
            FontStyleProperty, FontWeightProperty, FontStretchProperty, LanguageProperty })
            property.OverrideMetadata(typeof(CalendarDayLabel), new FrameworkPropertyMetadata(
                property.GetMetadata(typeof(Control)).DefaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _key = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var culture = Language.GetSpecificCulture();
        var value = Convert.ToString(Content, culture) ?? string.Empty;
        if (value.Length == 0 || Foreground is null) return;
        var key = new TextKey(value, FontFamily.Source, FontStyle, FontWeight, FontStretch,
            FontSize, VisualTreeHelper.GetDpi(this).PixelsPerDip, culture.Name, FlowDirection);
        if (_key != key)
        {
            _text = new FormattedText(value, culture, FlowDirection,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground, key.Dpi);
            _ink = _text.BuildGeometry(new Point()).Bounds;
            _key = key;
        }
        if (_text is null || _ink.IsEmpty) return;
        _text.SetForegroundBrush(Foreground);
        drawingContext.DrawText(_text, new Point(
            (ActualWidth - _ink.Width) / 2 - _ink.X,
            (ActualHeight - _ink.Height) / 2 - _ink.Y));
    }
}
