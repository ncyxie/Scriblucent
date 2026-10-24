using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Scriblucent.Icons;
using Scriblucent.Localization;
using Scriblucent.ViewModels;
using Scriblucent.Views;

namespace Scriblucent.Controls;

/// <summary>A content-sized summary for a large container or a selected block, baked once per drag.</summary>
internal static class ItemReorderPreview
{
    internal static FrameworkElement Create(FrameworkElement source, object item, int count)
    {
        var (text, kind) = item switch
        {
            NoteCardViewModel value => (value.Title, AppIconKind.Notes),
            FlashcardSetViewModel value => (value.Title, AppIconKind.Flashcards),
            MindMapViewModel value => (value.Title, AppIconKind.MindMaps),
            QuizViewModel value => (value.Title, AppIconKind.Quizzes),
            FlashcardsPreviewWindow.FlashcardPreviewItem value => (value.Question, AppIconKind.Flashcards),
            QuizPreviewWindow.QuizPreviewQuestion value => (string.IsNullOrWhiteSpace(value.Question) ? value.HeaderText : value.Question, AppIconKind.Quizzes),
            NoteGroupViewModel value => (value.Name, AppIconKind.Group),
            FlashcardSetGroupViewModel value => (value.Name, AppIconKind.Group),
            MindMapGroupViewModel value => (value.Name, AppIconKind.Group),
            QuizGroupViewModel value => (value.Name, AppIconKind.Group),
            _ => (LocalizationService.GetString("ReorderDrag"), AppIconKind.GripVertical)
        };
        var label = new StackPanel();
        label.Children.Add(new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold,
            MaxWidth = 260, MaxHeight = 40, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
        if (count > 1) label.Children.Add(new TextBlock { Text = string.Format(LocalizationService.GetString("PremiumSelectedCount"), count),
            FontSize = 13, Margin = new Thickness(0, 4, 0, 0), Foreground = (Brush)source.FindResource("TextColorSecondary") });
        var row = new DockPanel();
        var icon = new AppIcon { Kind = kind, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(icon, Dock.Left); row.Children.Add(icon); row.Children.Add(label);
        var face = new SurfaceMaterialBorder { MinWidth = 140, MaxWidth = 320, Padding = new Thickness(14, 12, 14, 12), Child = row,
            Background = (Brush)source.FindResource("CardBackground"), BorderBrush = (Brush)source.FindResource("AccentRimBrush"),
            BorderThickness = new Thickness(1), CornerRadius = (CornerRadius)source.FindResource("Radius.Card") };
        TextElement.SetForeground(face, (Brush)source.FindResource("TextColor"));
        TextElement.SetFontFamily(face, (FontFamily)source.FindResource("FontFamily.UI"));
        SurfaceMaterial.SetKind(face, SurfaceMaterialKind.Raised);
        face.Measure(new Size(320, 120)); face.Arrange(new Rect(face.DesiredSize)); face.UpdateLayout();
        return face;
    }
}
