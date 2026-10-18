using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Localization;

namespace Scriblucent.Controls;

internal enum EditorSaveFeedback { None, Saved, Failed }

/// <summary>The glyph and label share one rendered surface and one presence clock.</summary>
public sealed class EditorTitleStatus : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(EditorTitleStatus), new PropertyMetadata(string.Empty));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private static readonly Geometry Check = FrozenGeometry("M 1,7 L 5,11 L 13,3");
    private static readonly Geometry Cross = FrozenGeometry("M 3,3 L 11,11 M 11,3 L 3,11");
    private readonly Grid _glyphSlot = new() { Width = 20, MinHeight = 26, Margin = new Thickness(0, 0, 6, 0), Visibility = Visibility.Collapsed };
    internal readonly System.Windows.Shapes.Path Glyph = new()
    {
        Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, StrokeThickness = 1.5,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
    };
    private readonly System.Windows.Shapes.Ellipse _wash = new()
    {
        Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    internal readonly TextBlock Label = new() { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
    internal EditorSaveFeedback Feedback { get; private set; }

    public EditorTitleStatus()
    {
        IsHitTestVisible = false;
        Focusable = false;
        _wash.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SavedFeedbackWash");
        Label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Text)) { Source = this });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _glyphSlot.Children.Add(_wash);
        _glyphSlot.Children.Add(Glyph);
        row.Children.Add(_glyphSlot);
        row.Children.Add(Label);
        Content = row;
    }

    internal void SetEdited(bool dirty)
    {
        // A failed write stays visible until a successful retry; typing is not a failure.
        if (Feedback != EditorSaveFeedback.None) return;
        Text = LocalizationService.GetString("EditedIndicator");
        UiMotion.SetInlineStatusVisible(this, dirty);
    }

    internal void ShowSaved(Action refreshEdited, int holdMs = 1150)
    {
        UiMotion.CancelInlineStatusFlash(this);
        SetFeedback(EditorSaveFeedback.None);
        // Retained inactive tabs save normally, without replaying an old success on return.
        if (!IsLoaded) { SetEdited(false); refreshEdited(); return; }
        SetFeedback(EditorSaveFeedback.Saved);
        UiMotion.FlashInlineStatus(this, LocalizationService.GetString("Saved"), () =>
        {
            SetFeedback(EditorSaveFeedback.None);
            refreshEdited();
        }, holdMs);
    }

    internal void ShowFailure()
    {
        UiMotion.CancelInlineStatusFlash(this);
        SetFeedback(EditorSaveFeedback.Failed);
        Text = LocalizationService.GetString("NotSaved");
        UiMotion.SetInlineStatusVisible(this, true);
    }

    private void SetFeedback(EditorSaveFeedback value)
    {
        Feedback = value;
        _glyphSlot.Visibility = value == EditorSaveFeedback.None ? Visibility.Collapsed : Visibility.Visible;
        _wash.Visibility = value == EditorSaveFeedback.Saved && MotionPolicy.Spatial ? Visibility.Visible : Visibility.Hidden;
        Glyph.Data = value == EditorSaveFeedback.Saved ? Check : Cross;
        Glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, value == EditorSaveFeedback.Failed ? "AccentDanger" : "AccentSuccess");
    }

    private static Geometry FrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
