using System;
using System.Windows;
using Scriblucent.Services;
using Scriblucent.Animations;

namespace Scriblucent.Views;

public partial class QuizPreviewWindow
{
    private Window? _workspaceOwner;
    private Func<bool>? _workspaceSave;
    private Action? _workspaceClose;
    private bool _workspaceDisposed;
    private Window WorkspaceOwner => _workspaceOwner ?? this;
    public string WorkspaceTitle => EditorTitle;
    public bool WorkspaceDirty => HasUnsavedChanges();
    public bool WorkspaceAutoSaveEligible => !_isInitializing && HasPendingAutoSaveChanges();
    public event Action? WorkspaceStateChanged;

    public FrameworkElement AttachToWorkspace(Window owner, Func<bool> save, Action close)
    {
        _workspaceOwner = owner;
        _workspaceSave = save;
        _workspaceClose = close;
        var root = EditorHosting.Detach(this, owner);
        root.PreviewKeyDown += QuizPreviewWindow_PreviewKeyDown;

        return root;
    }
    public void CommitWorkspaceEdits() => EditorHosting.CommitBindings(ContentRoot);
    public bool ConfirmWorkspaceClose()
    {
        CommitWorkspaceEdits();
        return GetCloseDecision() switch
        {
            UnsavedCloseDecision.LeaveWithoutSaving => true,
            UnsavedCloseDecision.SaveAndClose => _workspaceSave?.Invoke() == true,
            _ => false
        };
    }
    public void SetWorkspaceActive(bool active)
    {
        if (!active && ShellWindowChrome.IsFullscreenSuspended(WorkspaceOwner))
            ShellWindowChrome.ResumeFromFullscreen(WorkspaceOwner);

    }
    public void DisposeWorkspaceEditor()
    {
        if (_workspaceDisposed) return;
        _workspaceDisposed = true;
        if (WorkspaceOwner is not EditorWorkspaceWindow { IsCloseApproved: true }
            && ShellWindowChrome.IsFullscreenSuspended(WorkspaceOwner))
            ShellWindowChrome.ResumeFromFullscreen(WorkspaceOwner);
        _workspaceSave = null;
        _workspaceClose = null;
        _allowCloseWithoutPrompt = true;
        WindowContentEnterHelper.AllowImmediateClose(this);
        foreach (var question in _questions) DetachQuestionChangeTracking(question);
        _questions.CollectionChanged -= Questions_CollectionChanged;

        Close(); // Unshown controller has no content/motion root; Closed releases its subscriptions.
        _workspaceOwner = null;
    }
}
