using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Scriblucent.Services;

internal static class EditorToolbarContext
{
    private static readonly DependencyProperty[] TextProperties =
    [
        TextElement.FontFamilyProperty, TextElement.FontSizeProperty,
        TextElement.FontWeightProperty, TextElement.FontStyleProperty,
        TextElement.FontStretchProperty, TextElement.ForegroundProperty,
        TextElement.BackgroundProperty, Inline.TextDecorationsProperty
    ];

    internal static bool HasFormattingToClear(RichTextBox editor)
    {
        foreach (var property in TextProperties)
        {
            object value = editor.Selection.GetPropertyValue(property);
            if (ReferenceEquals(value, DependencyProperty.UnsetValue)) return true;
            object baseline = editor.Document.GetValue(property);
            if (property == Inline.TextDecorationsProperty)
            {
                if (value is TextDecorationCollection { Count: > 0 }) return true;
                continue;
            }
            if (value is SolidColorBrush color && baseline is SolidColorBrush defaultColor)
            {
                if (color.Color != defaultColor.Color) return true;
            }
            else if (!Equals(value, baseline))
            {
                // An explicit transparent highlight is equivalent to no highlight.
                if (property == TextElement.BackgroundProperty && (value is null || value is SolidColorBrush { Color.A: 0 })
                    && (baseline is null || baseline is SolidColorBrush { Color.A: 0 })) continue;
                return true;
            }
        }
        return false;
    }
}
