using System.Text.Json.Serialization;

namespace Scriblucent.Models;

public sealed class MindMapNode
{
    // Stable identity a link (LinkedNoteId, CrossLinkTargetIds) can point at. Cloning must decide
    // per call site whether to preserve this (restoring the same logical node - undo/redo) or
    // regenerate it (a genuinely new node - cut/copy/paste, import, duplicate); see the CloneNode
    // helpers, which is why this isn't just `= Guid.NewGuid()` treated as "set once and forget."
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text { get; set; } = string.Empty;
    public List<MindMapNode> Children { get; set; } = new();
    public bool IsExpanded { get; set; } = true;

    // Styling properties
    public string? BackgroundColor { get; set; } // Hex color code
    public string? BorderColor { get; set; } // Hex color code
    public double BorderThickness { get; set; } = 1.0;
    public string? NodeShape { get; set; } = "Rectangle"; // Rectangle, Rounded, Circle, Ellipse

    // Emoji/icon identifiers - a node can carry more than one (matching FreeMind, which allows
    // stacking as many icons on a node as you like), rendered together in one badge.
    public List<string> Icons { get; set; } = new();

    // Legacy single-icon field from before multi-icon support. Only participates in
    // deserialization now - MindMapFileService folds any value it carries into Icons right after
    // load (see MigrateLegacyIcon) and nothing in the app sets it going forward, so it always
    // round-trips as null once a file has been through that migration. Kept only so opening an
    // old .scribmap file doesn't silently drop whatever icon it already had.
    public string? Icon { get; set; }

    public string? IconBadgeColor { get; set; } = "#F59E0B"; // Default amber
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public string? TextColor { get; set; } // Hex color code
    public string? FontFamily { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FontSize { get; set; }

    // Free-text note attached to the node - distinct from Icon (a small emoji badge). Editable via
    // StyleNodeDialog; shown on canvas as a small indicator badge with the text as its tooltip.
    // Also the correct home for a FreeMind/Freeplane <richcontent TYPE="NOTE"> on import - Icon
    // used to be misused for this before this field existed.
    public string? Note { get; set; }

    // Per-node hyperlink to a note elsewhere in the app - reuses the same "given a note Guid,
    // open it" plumbing the document-level linked-note feature already has (MindMapPreviewWindow.
    // _openNoteAction), just addressed per-node instead of per-document.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? LinkedNoteId { get; set; }

    // Graphical cross-links: non-hierarchical connections to other nodes anywhere in the tree
    // (by Id, not by object reference, so they survive clone/serialize round-trips), drawn as a
    // second, visually distinct connector pass alongside the normal parent-child tree lines.
    // Stored one-directional (only on the source node) - drawing looks both ways isn't needed
    // since every node gets rendered and each renders its own outgoing links.
    public List<Guid> CrossLinkTargetIds { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ManualX { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ManualY { get; set; }

    // Set on FreeMind import from a node's HGAP/VSHIFT attributes - a delta relative to wherever
    // Freeplane's own auto-layout would have placed the node, not an absolute position (FreeMind
    // has no such thing). We don't know Freeplane's layout, but we do know our own: resolved into
    // real ManualX/ManualY (as an offset from THIS app's auto-layout for the same node) the first
    // time the map is opened in the editor - see MindMapPreviewWindow.ApplyPendingFreeMindOffsets.
    // Persisted (not JsonIgnore outright) so it survives a save/reload between import and that
    // first open; cleared once resolved.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PendingFreeMindOffsetX { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PendingFreeMindOffsetY { get; set; }

    public bool HasChildren => Children.Count > 0;
}
