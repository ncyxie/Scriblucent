using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Controls;
using Scriblucent.Icons;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Views;

public partial class MindMapPreviewWindow
{
    private static List<MindMapNode> _clipboardNodes = [];
    private static Guid _clipboardMap;
    private NodeSelectionScope? _nodeSelectionScope;
    private bool _nodeSelectionMode;
    private readonly Dictionary<MindMapNode, Point> _groupDragStarts = [];
    private void InitializeNodeSelection()
    {
        _nodeSelectionScope = new(this);
        NodeSelectionDock.Initialize(_nodeSelectionScope,
            new("SelectionDuplicate", AppIconKind.Duplicate, DuplicateSelectedNodes, CanDuplicateNodeSelection),
            new("Delete", AppIconKind.Delete, () => DeleteSelectedNodes(), CanDeleteSelection),
            BuildNodeSelectionMenu, new("Cut", AppIconKind.Cut, () => CutSelectedNodes(), CanDeleteSelection));
        LocalizationService.CultureChanged += NodeSelectionCultureChanged;
        Closed += (_, _) => { LocalizationService.CultureChanged -= NodeSelectionCultureChanged; NodeSelectionDock.Dispose(); };
    }
    private bool CanDeleteSelection() => _selectedNodes.Count > 0 && !_selectedNodes.Contains(_root);
    private bool CanDuplicateNodeSelection() => _selectedNodes.Count > 0 && !_selectedNodes.Contains(_root);
    private void NodeSelectionCultureChanged(object? sender, EventArgs e) => _nodeSelectionScope?.Notify();
    private void SyncNodeSelection()
    {
        if (_selectedNodes.Count > 1) _nodeSelectionMode = true;
        if (AddNodeMenuButton is not null) AddNodeMenuButton.IsEnabled = _selectedNodes.Count == 1;
        if (DeleteSelectedNodeButton is not null) DeleteSelectedNodeButton.IsEnabled = CanDeleteSelection();
        _nodeSelectionScope?.Notify(); UpdateMapCanvasSize();
    }
    private void UseContextSelection(MindMapNode node)
    {
        if (!_selectedNodes.Contains(node)) SelectSingleNode(node);
        _selectedNode = node; ApplyNodeSelectionVisuals();
    }
    private ContextMenu BuildNodeSelectionMenu()
    {
        var menu = new ContextMenu();
        void Add(string key, Action action, bool enabled = true)
        { var item = new MenuItem { Header = LocalizationService.GetString(key), IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("RenameSelected", RenameSelectedNodes);
        Add("Copy", CopySelectedNodes, _selectedNodes.Count > 0);
        Add("FontLabel", () => SetFontPanelOpen(true));
        Add("MindMapExportSelectedBranch", () => ExportMindMap(true));
        Add("MindMapPasteStyle", () => { if (_selectedNode is { } node) PasteNodeStyle(node); }, _copiedStyle is not null);
        return menu;
    }
    private void BeginNodeSelection(MindMapNode node)
    {
        _nodeSelectionMode = true; EnsureNodeSelected(node); ApplyNodeSelectionVisuals(); NodeSelectionDock.FocusOnEntry();
    }
    private void RenameSelectedNodes()
    {
        var targets = MindMapSelectionOperations.Nodes(_root).Where(_selectedNodes.Contains).Select(node => new RenameTarget(node.Id, node.Text)).ToArray();
        if (targets.Length == 0) return;
        var dialog = new BulkRenameDialog(targets) { Owner = WorkspaceOwner };
        if (dialog.ShowDialog() == true) ApplyNodeRenames(dialog.Changes);
    }
    internal void ApplyNodeRenames(IReadOnlyList<RenameChange> changes)
    {
        var names = changes.ToDictionary(change => change.Id, change => NormalizeNodeText(change.After));
        var targets = MindMapSelectionOperations.Nodes(_root).Where(node => _selectedNodes.Contains(node) && names.TryGetValue(node.Id, out var name) && name.Length > 0 && name != node.Text).ToArray();
        if (targets.Length == 0) return;
        PushUndoSnapshot();
        foreach (var node in targets) node.Text = names[node.Id];
        RebuildMap();
    }
    private void CopySelectedNodes()
    {
        _clipboardNodes = MindMapSelectionOperations.CloneForest(MindMapSelectionOperations.Roots(_root, _selectedNodes));
        _clipboardMap = _root.Id;
    }
    internal void DuplicateSelectedNodes()
    {
        if (!CanDuplicateNodeSelection()) return;
        CommitWorkspaceEdits();
        var sources = MindMapSelectionOperations.Roots(_root, _selectedNodes);
        if (sources.Count == 0) return;
        PushUndoSnapshot();
        var copies = MindMapSelectionOperations.DuplicateBranches(_root, _selectedNodes);
        if (_isManualPositioningMode)
        {
            var visible = sources.SelectMany(node => MindMapSelectionOperations.Nodes(node))
                .Where(_layouts.ContainsKey).Select(node => _layouts[node]).ToArray();
            if (visible.Length > 0)
            {
                var bounds = new Rect(visible.Min(layout => layout.X), visible.Min(layout => layout.Y),
                    visible.Max(layout => layout.X + layout.Width) - visible.Min(layout => layout.X),
                    visible.Max(layout => layout.Y + layout.Height) - visible.Min(layout => layout.Y));
                var candidates = new[] { new Vector(36, 36), new Vector(bounds.Width + 32, 0), new Vector(0, bounds.Height + 32),
                    new Vector(Math.Max(0, CanvasPadding - bounds.Left),
                        Math.Max(CanvasPadding, _layouts.Values.Max(layout => layout.Y + layout.Height) + 32) - bounds.Top) };
                var offset = candidates.First(candidate =>
                {
                    var moved = bounds; moved.Offset(candidate);
                    if (moved.Left < CanvasPadding || moved.Top < CanvasPadding) return false;
                    moved.Inflate(8, 8);
                    return !_layouts.Values.Any(layout => moved.IntersectsWith(new Rect(layout.X, layout.Y, layout.Width, layout.Height)));
                });
                foreach (var branch in sources.Zip(copies))
                foreach (var pair in MindMapSelectionOperations.Nodes(branch.First).Zip(MindMapSelectionOperations.Nodes(branch.Second)))
                {
                    if (_layouts.TryGetValue(pair.First, out var layout)) SetManualPosition(pair.Second, new Point(layout.X + offset.X, layout.Y + offset.Y));
                    else if (pair.Second.ManualX is { } x && pair.Second.ManualY is { } y)
                        SetManualPosition(pair.Second, new Point(x + offset.X, y + offset.Y));
                }
            }
        }
        else foreach (var copy in copies) ClearManualPositions(copy);
        _selectedNodes.Clear(); _selectedNodes.UnionWith(copies); _selectedNode = copies.Last();
        RebuildMap(); SyncNodeSelection();
        if (_layouts.TryGetValue(_selectedNode, out var selectedLayout)) CenterViewOnLayout(selectedLayout);
    }
    private void CutSelectedNodes()
    {
        if (!CanDeleteSelection()) return;
        bool selecting = _nodeSelectionMode || _selectedNodes.Count > 1;
        CopySelectedNodes(); PushUndoSnapshot();
        MindMapSelectionOperations.CutBranches(_root, _selectedNodes);
        ClearSelectedNodes(); if (selecting) _nodeSelectionMode = true; else SelectSingleNode(_root);
        RebuildMap(); SyncNodeSelection();
    }
    private void DeleteSelectedNodes()
    {
        if (!CanDeleteSelection()) return;
        var text = _selectedNodes.Count == 1
            ? string.Format(LocalizationService.GetString("MindMapDeleteNodeConfirmationFormat"), _selectedNodes.First().Text)
            : string.Format(LocalizationService.GetString("SelectionDeleteNodesConfirmation"), _selectedNodes.Count);
        if (!ModernDialog.ConfirmDanger(WorkspaceOwner, LocalizationService.GetString("MindMapDeleteNodeTitle"), text, LocalizationService.GetString("Delete"))) return;
        bool selecting = _nodeSelectionMode || _selectedNodes.Count > 1;
        PushUndoSnapshot(); MindMapSelectionOperations.DeleteNodes(_root, _selectedNodes);
        ClearSelectedNodes(); if (selecting) _nodeSelectionMode = true; else SelectSingleNode(_root);
        RebuildMap(); SyncNodeSelection();
    }
    private void PasteSelectedBranches(MindMapNode target)
    {
        if (_clipboardNodes.Count == 0 || _selectedNodes.Count > 1) return;
        var pasted = MindMapSelectionOperations.CloneForest(_clipboardNodes, keepExternalLinks: _clipboardMap == _root.Id);
        PushUndoSnapshot(); target.Children.AddRange(pasted); target.IsExpanded = true;
        MindMapSelectionOperations.RemoveDanglingLinks(_root);
        _selectedNodes.Clear(); _selectedNodes.UnionWith(pasted); _selectedNode = pasted.Last();
        RebuildMap(); SyncNodeSelection();
    }
    private bool HandleNodeSelectionKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (_nodeSelectionMode || _selectedNodes.Count > 0))
        { ClearSelectedNodes(); ApplyNodeSelectionVisuals(); e.Handled = true; return true; }
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _nodeSelectionMode = true;
            _selectedNodes.Clear(); _selectedNodes.UnionWith(MindMapSelectionOperations.Nodes(_root, expandedOnly: true).Where(node => node != _root));
            _selectedNode = _selectedNodes.LastOrDefault(); ApplyNodeSelectionVisuals(); e.Handled = true; return true;
        }
        return false;
    }
    private void PrepareGroupDrag(MindMapNode node)
    {
        _groupDragStarts.Clear();
        if (!_isManualPositioningMode || _isLinkDragGesture || !_selectedNodes.Contains(node) || _selectedNodes.Count < 2) return;
        foreach (var selected in _selectedNodes)
            if (_layouts.TryGetValue(selected, out var layout)) _groupDragStarts[selected] = new Point(layout.X, layout.Y);
    }
    private bool MoveGroupDrag(Point position)
    {
        if (_groupDragStarts.Count < 2) return false;
        double dx = Math.Max(position.X - _dragStartMousePos.X, CanvasPadding - _groupDragStarts.Values.Min(point => point.X));
        double dy = Math.Max(position.Y - _dragStartMousePos.Y, CanvasPadding - _groupDragStarts.Values.Min(point => point.Y));
        foreach (var (node, start) in _groupDragStarts)
        {
            var next = new Point(start.X + dx, start.Y + dy); SetManualPosition(node, next);
            _layouts[node] = _layouts[node] with { X = next.X, Y = next.Y };
        }
        foreach (var border in MapCanvas.Children.OfType<Border>())
            if (border.Tag is MindMapNode node && _groupDragStarts.ContainsKey(node))
            { Canvas.SetLeft(border, _layouts[node].X); Canvas.SetTop(border, _layouts[node].Y); }
        QueueConnectionRedraw(); return true;
    }
    private sealed class NodeSelectionScope(MindMapPreviewWindow owner) : ICollectionSelectionScope
    {
        public bool IsActive => owner._nodeSelectionMode || owner._selectedNodes.Count > 1;
        public bool CanSelect => true;
        public int Count => owner._selectedNodes.Count;
        public int TotalCount => MindMapSelectionOperations.Nodes(owner._root, true).Count(node => node != owner._root);
        public int MatchingSelectedCount => MindMapSelectionOperations.Nodes(owner._root, true).Count(node => node != owner._root && owner._selectedNodes.Contains(node));
        public string StatusText => owner._selectedNodes.Contains(owner._root) && IsActive ? LocalizationService.GetString("SelectionRootProtected") : string.Empty;
        public event Action? Changed;
        internal void Notify() => Changed?.Invoke();
        public bool Contains(Guid id) => owner._selectedNodes.Any(node => node.Id == id);
        public void Begin(Guid? initial = null) { owner._nodeSelectionMode = true; if (initial is Guid id && MindMapSelectionOperations.Nodes(owner._root).FirstOrDefault(node => node.Id == id) is { } node) owner.SelectSingleNode(node); owner.ApplyNodeSelectionVisuals(); }
        public void Toggle(Guid id, bool range = false, bool additive = false) { if (MindMapSelectionOperations.Nodes(owner._root).FirstOrDefault(node => node.Id == id) is { } node) owner.ToggleNodeSelection(node); owner.ApplyNodeSelectionVisuals(); }
        public void SelectAll() { owner._nodeSelectionMode = true; owner._selectedNodes.Clear(); owner._selectedNodes.UnionWith(MindMapSelectionOperations.Nodes(owner._root, true).Where(node => node != owner._root)); owner._selectedNode = owner._selectedNodes.LastOrDefault(); owner.ApplyNodeSelectionVisuals(); }
        public void Clear() { owner.ClearSelectedNodes(); owner._nodeSelectionMode = true; owner.ApplyNodeSelectionVisuals(); }
        public void End() { owner.ClearSelectedNodes(); owner.ApplyNodeSelectionVisuals(); }
    }
}
