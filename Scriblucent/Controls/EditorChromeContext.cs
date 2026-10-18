using System.Windows;

namespace Scriblucent.Controls;

/// <summary>Lets hosted editors join the document-tab chrome without changing document ownership.</summary>
public static class EditorChromeContext
{
    public static readonly DependencyProperty IsTabDraggingProperty = DependencyProperty.RegisterAttached(
        "IsTabDragging", typeof(bool), typeof(EditorChromeContext),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static void SetIsTabDragging(DependencyObject element, bool value) => element.SetValue(IsTabDraggingProperty, value);
    public static bool GetIsTabDragging(DependencyObject element) => (bool)element.GetValue(IsTabDraggingProperty);
    public static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(object), typeof(EditorChromeContext),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
    public static void SetController(DependencyObject element, object value) => element.SetValue(ControllerProperty, value);
    public static object GetController(DependencyObject element) => element.GetValue(ControllerProperty);
    public static readonly DependencyProperty IsTabbedProperty = DependencyProperty.RegisterAttached(
        "IsTabbed", typeof(bool), typeof(EditorChromeContext),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static void SetIsTabbed(DependencyObject element, bool value) => element.SetValue(IsTabbedProperty, value);
    public static bool GetIsTabbed(DependencyObject element) => (bool)element.GetValue(IsTabbedProperty);
}
