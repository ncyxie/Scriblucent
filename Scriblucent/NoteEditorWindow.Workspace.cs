using System;
using System.Windows;
using Scriblucent.Services;
using Scriblucent.Models;

namespace Scriblucent;

public partial class NoteEditorWindow
{
    private Window? _workspaceOwner;
    private Func<bool>? _workspaceSave;
    private bool _workspaceActive;
    private NoteDocument? _workspaceInactiveSnapshot;
    private string? _workspaceCaptureFingerprint;
    public event Action? WorkspaceStateChanged;
    public string WorkspaceTitle => TitleTextBox.Text.Trim();
    public bool WorkspaceDirty => HasUnsavedChanges();
    public bool WorkspaceAutoSaveEligible => !_isLoadingDocument && _activeAiGenerationCount == 0 && !_floatingImages.HasActiveGestures && HasContentChanged();

    public FrameworkElement AttachToWorkspace(Window owner, Func<bool> save, Action close)
    {
        _workspaceOwner = owner;
        _workspaceSave = save;
        StopAutoSaveTimer();
        EnableTabMode();
        CloseRequested += _ => close();
        var root = (FrameworkElement)DetachEditorContentForHosting()!;
        root.DataContext = DataContext;
        root.Resources.MergedDictionaries.Add(Resources);
        if (NameScope.GetNameScope(this) is { } names) NameScope.SetNameScope(root, names);
        return root;
    }
    internal NoteDocument CaptureExportDocument(NoteDocument baseline)
    {
        CommitWorkspaceEdits();
        var snapshot = EditorHosting.Clone(baseline);
        SaveToDocument(snapshot);
        snapshot.Images = _floatingImages.BuildSelfContainedAttachments().ToList();
        snapshot.SearchablePreview = null;
        return snapshot;
    }
    public NoteDocument CaptureWorkspaceDocument()
    {
        var fingerprint = GetWorkspaceCaptureFingerprint();
        if (_workspaceInactiveSnapshot is not null && _workspaceCaptureFingerprint == fingerprint)
            return EditorHosting.Clone(_workspaceInactiveSnapshot);
        _allowCloseWithoutPrompt = false;
        SaveToDocument(_currentDocument!);
        _workspaceInactiveSnapshot = EditorHosting.Clone(_currentDocument!);
        _workspaceCaptureFingerprint = GetWorkspaceCaptureFingerprint();
        return EditorHosting.Clone(_workspaceInactiveSnapshot);
    }
    private string GetWorkspaceCaptureFingerprint() => GetEditorSnapshot() + "|" + _isTagsPanelOpen + "|" + _currentDocument?.AiReviewNoticeDismissed;
    public void MarkWorkspaceSaved()
    {
        MarkCurrentStateSaved();
        ShowAutoSaveIndicator();
    }
    public void CommitWorkspaceEdits() => EditorHosting.CommitBindings(RootGrid);
    public bool ConfirmWorkspaceClose() => ConfirmCloseIfNeeded();
    public void SetWorkspaceActive(bool active)
    {
        if (_workspaceActive == active) return;
        if (!active)
        {
            CommitWorkspaceEdits();
            // Capture while still mounted. Autosave can persist this retained snapshot without
            // asking an unloaded RichTextBox/wrap engine to measure its invisible document.
            _workspaceInactiveSnapshot = HasContentChanged() ? CaptureWorkspaceDocument() : EditorHosting.Clone(_currentDocument!);
        }
        _workspaceActive = active;
        if (active) UpdateEditedIndicator();
    }
    public void DisposeWorkspaceEditor()
    {
        DisposeHostedEditor();
        _workspaceSave = null;
        _workspaceOwner = null;
        _allowCloseWithoutPrompt = true;
        Scriblucent.Animations.WindowContentEnterHelper.AllowImmediateClose(this);
        Close();
    }
}
