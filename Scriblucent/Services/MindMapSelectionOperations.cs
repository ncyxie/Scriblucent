using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>Tree edits use logical nodes, never canvas controls. Every mutation is wrapped by one editor undo transaction.</summary>
internal static class MindMapSelectionOperations
{
    internal static IEnumerable<MindMapNode> Nodes(MindMapNode root, bool expandedOnly = false)
    {
        var pending = new Stack<MindMapNode>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            yield return node;
            if (!expandedOnly || node.IsExpanded)
                for (int i = node.Children.Count - 1; i >= 0; i--) pending.Push(node.Children[i]);
        }
    }
    internal static IReadOnlyList<MindMapNode> Roots(MindMapNode root, IReadOnlySet<MindMapNode> selected)
    {
        var result = new List<MindMapNode>(); var pending = new Stack<MindMapNode>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (selected.Contains(node)) { result.Add(node); continue; }
            for (int i = node.Children.Count - 1; i >= 0; i--) pending.Push(node.Children[i]);
        }
        return result;
    }
    internal static List<MindMapNode> CloneForest(IEnumerable<MindMapNode> roots, bool keepExternalLinks = true)
    {
        var sourceRoots = roots.ToArray();
        var all = sourceRoots.SelectMany(node => Nodes(node)).Distinct().ToArray();
        var copies = all.ToDictionary(node => node, node => new MindMapNode
        {
            Text = node.Text, IsExpanded = node.IsExpanded, BackgroundColor = node.BackgroundColor,
            BorderColor = node.BorderColor, BorderThickness = node.BorderThickness, NodeShape = node.NodeShape,
            Icons = [.. node.Icons], IconBadgeColor = node.IconBadgeColor, IsBold = node.IsBold,
            IsItalic = node.IsItalic, TextColor = node.TextColor, FontFamily = node.FontFamily, FontSize = node.FontSize,
            Note = node.Note, LinkedNoteId = node.LinkedNoteId, ManualX = node.ManualX, ManualY = node.ManualY,
            PendingFreeMindOffsetX = node.PendingFreeMindOffsetX, PendingFreeMindOffsetY = node.PendingFreeMindOffsetY
        });
        var ids = all.ToDictionary(node => node.Id, node => copies[node].Id);
        foreach (var node in all)
        {
            copies[node].Children = node.Children.Select(child => copies[child]).ToList();
            copies[node].CrossLinkTargetIds = node.CrossLinkTargetIds.Where(id => ids.ContainsKey(id) || keepExternalLinks)
                .Select(id => ids.GetValueOrDefault(id, id)).Distinct().ToList();
        }
        return sourceRoots.Select(node => copies[node]).ToList();
    }
    internal static void DeleteNodes(MindMapNode root, IReadOnlySet<MindMapNode> selected)
    {
        if (selected.Contains(root)) throw new InvalidOperationException("The root cannot be deleted.");
        var original = Nodes(root).ToArray();
        foreach (var parent in original.Reverse())
            parent.Children = parent.Children.SelectMany(child => selected.Contains(child) ? (IEnumerable<MindMapNode>)child.Children : new[] { child }).ToList();
        RemoveDanglingLinks(root);
    }
    internal static void CutBranches(MindMapNode root, IReadOnlySet<MindMapNode> selected)
    {
        if (selected.Contains(root)) throw new InvalidOperationException("The root cannot be cut.");
        var branches = Roots(root, selected).ToHashSet();
        foreach (var parent in Nodes(root).ToArray()) parent.Children.RemoveAll(branches.Contains);
        RemoveDanglingLinks(root);
    }
    internal static void RemoveDanglingLinks(MindMapNode root)
    {
        var nodes = Nodes(root).ToArray(); var ids = nodes.Select(node => node.Id).ToHashSet();
        foreach (var node in nodes) node.CrossLinkTargetIds.RemoveAll(id => !ids.Contains(id));
    }
}
