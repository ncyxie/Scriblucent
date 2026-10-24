using System.Windows;

namespace Scriblucent.Controls;

/// <summary>Mode context for the card templates shared by the editor and study views.</summary>
public static class FlashcardFaceContext
{
    public static readonly DependencyProperty IsStudyProperty = DependencyProperty.RegisterAttached(
        "IsStudy", typeof(bool), typeof(FlashcardFaceContext),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsStudy(DependencyObject element) => (bool)element.GetValue(IsStudyProperty);

    public static void SetIsStudy(DependencyObject element, bool value) => element.SetValue(IsStudyProperty, value);

    public static readonly DependencyProperty IsAnswerProperty = DependencyProperty.RegisterAttached(
        "IsAnswer", typeof(bool), typeof(FlashcardFaceContext),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsAnswer(DependencyObject element) => (bool)element.GetValue(IsAnswerProperty);

    public static void SetIsAnswer(DependencyObject element, bool value) => element.SetValue(IsAnswerProperty, value);
}
