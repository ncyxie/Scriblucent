using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Text.Json;
using Scriblucent.Controls;
using Scriblucent.Icons;
using Scriblucent.Models;

namespace Scriblucent.Services;

public enum EditorDocumentKind { Note, Flashcards, Quiz, MindMap }
public readonly record struct EditorDocumentKey(EditorDocumentKind Kind, Guid Id);

/// <summary>The retained controller owns editing state; the workspace owns window lifetime.</summary>
public interface IHostedDocumentEditor
{
    string WorkspaceTitle { get; }
    bool WorkspaceDirty { get; }
    bool WorkspaceAutoSaveEligible { get; }
    event Action? WorkspaceStateChanged;
    FrameworkElement AttachToWorkspace(Window owner, Func<bool> save, Action close);
    void CommitWorkspaceEdits();
    void ReportWorkspaceSaveFailure();
    bool ConfirmWorkspaceClose();
    void SetWorkspaceActive(bool active);
    void DisposeWorkspaceEditor();
}

internal sealed class EditorSession : IDisposable
{
    public required EditorDocumentKey Key { get; init; }
    public required IHostedDocumentEditor Editor { get; init; }
    public required FrameworkElement Content { get; init; }
    public required Func<bool> Save { get; init; }
    public AppIconKind Icon => Key.Kind switch
    {
        EditorDocumentKind.Flashcards => AppIconKind.Flashcards,
        EditorDocumentKind.Quiz => AppIconKind.Quizzes,
        EditorDocumentKind.MindMap => AppIconKind.MindMaps,
        _ => AppIconKind.Notes
    };
    public bool IsDraft { get; set; }
    public bool HasEntered { get; set; }
    public IInputElement? FocusTarget { get; set; }
    public bool IsDisposed { get; private set; }
    public bool IsDirty => IsDraft || Editor.WorkspaceDirty;
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Scriblucent.Animations.HostedEditorMotion.Settle(Content);
        Scriblucent.Animations.UiMotion.Cleanup(Content);
        Editor.DisposeWorkspaceEditor();
    }
}

internal static class EditorHosting
{
    public static T Clone<T>(T document) => document is NoteDocument note
        ? (T)(object)CloneNote(note)
        : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(document))!;

    private static NoteDocument CloneNote(NoteDocument source)
    {
        // Serialized note/history content and legacy photo data are immutable strings. Keep those
        // references rather than round-tripping megabytes of text merely to isolate small lists.
        var copy = CopyScalarProperties(source);
        copy.Tags = source.Tags is null ? [] : [.. source.Tags];
        copy.Images = source.Images?.Select(CopyScalarProperties).ToList() ?? [];
        copy.Schedules = source.Schedules?.Select(CopyScalarProperties).ToList() ?? [];
        copy.EditHistory = source.EditHistory?.Select(CopyScalarProperties).ToList() ?? [];
        return copy;
    }
    private static T CopyScalarProperties<T>(T source) where T : new()
    {
        var copy = new T();
        foreach (var property in ScalarProperties<T>.Values) property.SetValue(copy, property.GetValue(source));
        return copy;
    }
    private static class ScalarProperties<T>
    {
        internal static readonly System.Reflection.PropertyInfo[] Values = typeof(T).GetProperties()
            .Where(property => property.CanRead && property.CanWrite).ToArray();
    }

    public static FrameworkElement Detach(Window controller, Window owner)
    {
        var content = ShellWindowChrome.DetachContent(controller);
        if (content is FeatureWindowShell shell)
        {
            content = shell.Body as UIElement;
            shell.Body = null!;
        }
        var root = content as FrameworkElement ?? throw new InvalidOperationException("Editor content is missing.");
        root.Resources.MergedDictionaries.Add(controller.Resources);
        if (NameScope.GetNameScope(controller) is { } names) NameScope.SetNameScope(root, names);
        root.DataContext = controller.DataContext;
        EditorChromeContext.SetController(root, controller);
        root.Opacity = 1;
        root.Visibility = Visibility.Visible;
        return root;
    }

    public static void CommitBindings(DependencyObject root)
    {
        if (root is TextBox text) text.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) CommitBindings(VisualTreeHelper.GetChild(root, i));
    }
}
