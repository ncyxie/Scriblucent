using System.Collections;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Scriblucent.Services;

/// <summary>Commits WPF's queued typing before a custom structural edit opens its undo unit.</summary>
internal static class WpfEditorInput
{
    private static readonly PropertyInfo EditorProperty = typeof(TextBoxBase)
        .GetProperty("TextEditor", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo ThreadStoreProperty = EditorProperty.PropertyType
        .GetProperty("_ThreadLocalStore", BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo PendingInputProperty = ThreadStoreProperty.PropertyType
        .GetProperty("PendingInputItems", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo FlushMethod = typeof(RichTextBox).Assembly
        .GetType("System.Windows.Documents.TextEditorTyping")!
        .GetMethod("_FlushPendingInputItems", BindingFlags.Static | BindingFlags.NonPublic)!;

    internal static void Flush(RichTextBox editor)
    {
        // The native flush also throttles background layout. Avoid that side effect
        // when there is no queued input, including repeated calls within one command.
        if (PendingInputProperty.GetValue(ThreadStoreProperty.GetValue(null)) is not ICollection { Count: > 0 }) return;
        // Native Delete/Enter do this before opening their change block. Doing it
        // inside our block lets pending typing close or replace that block's undo unit.
        FlushMethod.Invoke(null, [EditorProperty.GetValue(editor)]);
    }
}
