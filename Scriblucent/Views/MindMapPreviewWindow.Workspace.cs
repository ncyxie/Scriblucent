using System;
using System.Windows;
using Scriblucent.Services;
using Scriblucent.Animations;

namespace Scriblucent.Views;

public partial class MindMapPreviewWindow
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
        ContentRoot.Loaded += (_, _) => InitializeWorkspaceMap();
        ContentRoot.SizeChanged += (_, _) => QueueMapViewportRefresh();
        return root;
    }
    internal Scriblucent.Models.MindMapDocument CaptureExportDocument(Scriblucent.Models.MindMapDocument baseline)
    {
        var snapshot = ToDocument(baseline, captureLayout: false);
        snapshot.Root = CloneNode(_root, preserveId: true);
        if (_isManualPositioningMode)
        {
            var layouts = _layouts.ToDictionary(pair => pair.Key.Id, pair => pair.Value);
            foreach (var node in MindMapSelectionOperations.Nodes(snapshot.Root))
                if (layouts.TryGetValue(node.Id, out var layout)) { node.ManualX = layout.X; node.ManualY = layout.Y; }
        }
        else ClearManualPositions(snapshot.Root);
        return snapshot;
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
        if (!active) { EndTileSession(); CancelManualDragPending(); }
        else QueueMapViewportRefresh();
    }
    public void DisposeWorkspaceEditor()
    {
        if (_workspaceDisposed) return;
        _workspaceDisposed = true;
        _mapClosed = true;
        _snapshotRequest++;
        _tileSettleTimer?.Stop();
        if (WorkspaceOwner is not EditorWorkspaceWindow { IsCloseApproved: true }
            && ShellWindowChrome.IsFullscreenSuspended(WorkspaceOwner))
            ShellWindowChrome.ResumeFromFullscreen(WorkspaceOwner);
        CancelManualDragPending();
        _workspaceSave = null;
        _workspaceClose = null;
        _allowCloseWithoutPrompt = true;
        WindowContentEnterHelper.AllowImmediateClose(this);
        _searchDebounceTimer?.Stop();
        Close(); // Unshown controller has no content/motion root; Closed releases its subscriptions.
        _workspaceOwner = null;
    }
}
