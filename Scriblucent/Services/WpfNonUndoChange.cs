using System.Reflection;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Scriblucent.Services;

/// <summary>
/// Restores editor-owned controls after WPF substitutes them during Undo. The public
/// IsUndoEnabled property clears history; disabling only the internal recorder preserves
/// both stacks. This narrow framework dependency is covered by table photo Undo/Redo tests.
/// </summary>
internal static class WpfNonUndoChange
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly PropertyInfo ContainerProperty = typeof(TextPointer).GetProperty("TextContainer", InstanceMembers)!;

    internal static void Run(RichTextBox editor, Action action)
    {
        var container = ContainerProperty.GetValue(editor.Document.ContentStart)!;
        var type = container.GetType();
        var manager = type.GetProperty("UndoManager", InstanceMembers)!.GetValue(container);
        var enabled = manager?.GetType().GetProperty("IsEnabled", InstanceMembers);
        var wasEnabled = enabled?.GetValue(manager);
        var begin = type.GetMethod("BeginChangeNoUndo", InstanceMembers, null, Type.EmptyTypes, null)!;
        var end = type.GetMethod("EndChange", InstanceMembers, null, Type.EmptyTypes, null)!;
        begin.Invoke(container, null);
        try
        {
            enabled?.SetValue(manager, false);
            action();
        }
        finally
        {
            enabled?.SetValue(manager, wasEnabled);
            end.Invoke(container, null);
        }
    }
}
