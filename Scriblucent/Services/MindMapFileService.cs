using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Scriblucent.Localization;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>
/// Mind map file import/export. Two directions are supported on purpose, not symmetrically:
/// - .scribmap (this app's own lossless JSON format) round-trips a MindMapDocument exactly.
/// - FreeMind/Freeplane .mm/.mmap XML is import-only - export was deliberately removed since the
///   format can't represent manual node positions, node shape, border thickness, or icon badges,
///   and a lossy export reads as this app's bug when viewed elsewhere. Import loss is more
///   forgiving (bringing outside data into a different editor is expected to translate somewhat),
///   so that direction stays and is hardened here instead.
/// </summary>
internal static class MindMapFileService
{
    private const int CurrentScribmapFormatVersion = 1;

    private static readonly JsonSerializerOptions ScribmapJsonOptions = new()
    {
        WriteIndented = true
    };

    // Verified against FreeMind's own freemind.properties "icons.list" - only unambiguous,
    // high-confidence entries. Read-only now that FreeMind export has been removed; a node's
    // Icon field only ever needs to flow FreeMind -> this app now, not the other way.
    private static readonly Dictionary<string, string> FreeMindBuiltinToEmoji = new()
    {
        ["idea"] = "\U0001F4A1", // 💡
        ["help"] = "❓",
        ["yes"] = "✅",
        ["button_cancel"] = "❌",
        ["messagebox_warning"] = "⚠️",
        ["stop-sign"] = "\U0001F6D1", // 🛑
        ["info"] = "ℹ️",
        ["flag"] = "\U0001F6A9", // 🚩
        ["attach"] = "\U0001F4CE", // 📎
        ["folder"] = "\U0001F4C1", // 📁
        ["calendar"] = "\U0001F4C5", // 📅
        ["clock"] = "⏰",
        ["hourglass"] = "⏳",
        ["pencil"] = "✏️",
        ["password"] = "\U0001F512", // 🔒
        ["bell"] = "\U0001F514", // 🔔
        ["bookmark"] = "\U0001F516", // 🔖
    };

    public static bool IsScribmapPath(string path)
        => string.Equals(Path.GetExtension(path), ".scribmap", StringComparison.OrdinalIgnoreCase);

    public static bool IsFreeMindPath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".mm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".mmap", StringComparison.OrdinalIgnoreCase);
    }

    public static void SaveScribmapFile(string path, MindMapDocument document)
    {
        var file = new ScribmapFile { FormatVersion = CurrentScribmapFormatVersion, Document = document };
        var json = JsonSerializer.Serialize(file, ScribmapJsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    public static MindMapDocument LoadScribmapDocument(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        var file = JsonSerializer.Deserialize<ScribmapFile>(json)
            ?? throw new InvalidOperationException(LocalizationService.GetString("MindMapImportFailed"));

        if (file.FormatVersion > CurrentScribmapFormatVersion)
            throw new InvalidOperationException(LocalizationService.GetString("MindMapImportUnsupportedVersion"));

        var document = file.Document
            ?? throw new InvalidOperationException(LocalizationService.GetString("MindMapImportFailed"));

        MigrateLegacyIcon(document.Root);
        return document;
    }

    // Folds a pre-multi-icon-support file's single Icon field into the new Icons list, so opening
    // an old .scribmap doesn't silently drop an icon a node already had. Every scribmap load path
    // goes through LoadScribmapDocument, so this one call site covers all of them.
    private static void MigrateLegacyIcon(MindMapNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Icon) && !node.Icons.Contains(node.Icon))
            node.Icons.Insert(0, node.Icon);
        node.Icon = null;

        foreach (var child in node.Children)
            MigrateLegacyIcon(child);
    }

    public static MindMapNode LoadScribmapNodeTree(string path)
        => LoadScribmapDocument(path).Root;

    /// <summary>
    /// Constructs a fresh, standalone MindMapDocument from a .scribmap or FreeMind file,
    /// normalized as a new import (fresh Id, no group/schedule/pin state carried over). Single
    /// entry point for dashboard-level import.
    /// </summary>
    public static MindMapDocument LoadAnyDocument(string path, string fallbackTitle)
    {
        if (IsScribmapPath(path))
        {
            var document = LoadScribmapDocument(path);
            NormalizeImportedDocument(document, fallbackTitle);
            return document;
        }

        return LoadFreeMindAsDocument(path, fallbackTitle);
    }

    public static MindMapDocument LoadFreeMindAsDocument(string path, string fallbackTitle)
    {
        var root = LoadFreeMindNodeTree(path)
            ?? throw new InvalidOperationException(LocalizationService.GetString("MindMapImportFailed"));

        var document = new MindMapDocument
        {
            Title = fallbackTitle,
            Root = root
        };

        NormalizeImportedDocument(document, fallbackTitle);
        return document;
    }

    public static MindMapNode? LoadFreeMindNodeTree(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(LocalizationService.GetString("MindMapImportFileNotFound"), path);

        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(LocalizationService.GetString("MindMapImportReadFailed"), ex);
        }

        var map = doc.Root;
        if (map == null || !string.Equals(map.Name.LocalName, "map", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(LocalizationService.GetString("MindMapImportInvalidRoot"));

        var firstNode = map.Elements("node").FirstOrDefault();
        return firstNode is null ? null : ParseFreeMindNode(firstNode);
    }

    private static MindMapNode ParseFreeMindNode(XElement element)
    {
        var node = new MindMapNode();

        var textAttr = element.Attribute("TEXT")?.Value;
        node.Text = string.IsNullOrWhiteSpace(textAttr) ? string.Empty : textAttr.Trim();

        var bg = element.Attribute("BACKGROUND_COLOR")?.Value;
        if (!string.IsNullOrWhiteSpace(bg))
            node.BackgroundColor = bg;

        // FreeMind's COLOR attribute is the node's TEXT color, not a border color - MindMapNode
        // didn't have a text-color field when this was first written, so it was left unmapped
        // rather than landing in the wrong field (BorderColor). It does now (see MindMapNode.
        // TextColor, added alongside per-node bold/italic). <edge COLOR> is the correct source
        // for BorderColor.
        var colorAttr = element.Attribute("COLOR")?.Value;
        if (!string.IsNullOrWhiteSpace(colorAttr))
            node.TextColor = colorAttr;

        var edgeColor = element.Element("edge")?.Attribute("COLOR")?.Value;
        if (!string.IsNullOrWhiteSpace(edgeColor))
            node.BorderColor = edgeColor;

        var font = element.Element("font");
        if (font is not null)
        {
            if (string.Equals(font.Attribute("BOLD")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                node.IsBold = true;
            if (string.Equals(font.Attribute("ITALIC")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                node.IsItalic = true;

            var fontName = font.Attribute("NAME")?.Value;
            if (!string.IsNullOrWhiteSpace(fontName))
                node.FontFamily = fontName;

            var fontSizeAttr = font.Attribute("SIZE")?.Value;
            if (double.TryParse(fontSizeAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize))
                node.FontSize = fontSize;
        }

        if (string.Equals(element.Attribute("FOLDED")?.Value, "true", StringComparison.OrdinalIgnoreCase))
            node.IsExpanded = false;

        if (string.Equals(element.Attribute("STYLE")?.Value, "bubble", StringComparison.OrdinalIgnoreCase))
            node.NodeShape = "Rounded";

        // Real Freeplane files commonly store multi-line/formatted node titles as
        // <richcontent TYPE="NODE"> HTML instead of the TEXT attribute - fall back to it so such
        // nodes don't silently import as blank.
        if (string.IsNullOrWhiteSpace(node.Text))
        {
            var titleBody = element.Elements("richcontent")
                .FirstOrDefault(rc => string.Equals(rc.Attribute("TYPE")?.Value, "NODE", StringComparison.OrdinalIgnoreCase))
                ?.Descendants("body").FirstOrDefault();
            if (titleBody != null)
            {
                var richText = titleBody.Value.Trim();
                if (!string.IsNullOrWhiteSpace(richText))
                    node.Text = richText;
            }
        }

        // FreeMind lets a node carry any number of icons, stacked in the order they're listed -
        // collect all of them (matching FreeMindBuiltinToEmoji), not just the first.
        node.Icons = element.Elements("icon")
            .Select(icon => icon.Attribute("BUILTIN")?.Value)
            .Where(name => name is not null && FreeMindBuiltinToEmoji.ContainsKey(name))
            .Select(name => FreeMindBuiltinToEmoji[name!])
            .ToList();

        // Independent of the icon above - MindMapNode.Note is a real field now, so a node's
        // FreeMind note no longer needs to be squeezed into the icon badge (nor does having one
        // exclude the other, the way the old Icon-as-fallback did).
        var noteBody = element.Elements("richcontent")
            .FirstOrDefault(rc => string.Equals(rc.Attribute("TYPE")?.Value, "NOTE", StringComparison.OrdinalIgnoreCase))
            ?.Descendants("body").FirstOrDefault();
        if (noteBody != null)
        {
            var content = noteBody.Value;
            if (!string.IsNullOrWhiteSpace(content))
                node.Note = content.Trim();
        }

        // HGAP/VSHIFT are FreeMind/Freeplane's own manual-position mechanism - a delta from
        // wherever that app's auto-layout would otherwise have placed the node, not an absolute
        // coordinate (FreeMind has no such thing). Stashed as a pending offset rather than
        // resolved here, since resolving it needs OUR OWN auto-layout for this node, which only
        // exists once this tree is loaded into the editor - see
        // MindMapPreviewWindow.ApplyPendingFreeMindOffsets.
        if (TryParseFreeMindLength(element.Attribute("HGAP")?.Value, element.Attribute("HGAP_QUANTITY")?.Value, out var hgap))
            node.PendingFreeMindOffsetX = hgap;
        if (TryParseFreeMindLength(element.Attribute("VSHIFT")?.Value, element.Attribute("VSHIFT_QUANTITY")?.Value, out var vshift))
            node.PendingFreeMindOffsetY = vshift;

        foreach (var childEl in element.Elements("node"))
            node.Children.Add(ParseFreeMindNode(childEl));

        return node;
    }

    /// <summary>
    /// FreeMind writes plain numeric HGAP/VSHIFT ("22"); Freeplane can write either that or the
    /// unit-suffixed HGAP_QUANTITY/VSHIFT_QUANTITY form ("22.0 pt", "-100 px") - try the plain
    /// attribute first, then strip a trailing unit off the quantity form.
    /// </summary>
    private static readonly string[] FreeMindLengthUnits = { "pt", "px" };

    private static bool TryParseFreeMindLength(string? plain, string? quantity, out double value)
    {
        if (!string.IsNullOrWhiteSpace(plain)
            && double.TryParse(plain, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;

        if (!string.IsNullOrWhiteSpace(quantity))
        {
            var numericPart = quantity.Trim();
            var spaceIndex = numericPart.IndexOf(' ');
            if (spaceIndex > 0)
            {
                numericPart = numericPart[..spaceIndex];
            }
            else
            {
                foreach (var unit in FreeMindLengthUnits)
                {
                    if (numericPart.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
                    {
                        numericPart = numericPart[..^unit.Length];
                        break;
                    }
                }
            }

            if (double.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Resets an imported document to a fresh, standalone entry - always regenerates Id (even for
    /// .scribmap, where the embedded Id might match an existing library entry) so a re-imported
    /// backup or a file shared between two people's libraries can never silently overwrite an
    /// unrelated existing map via AddOrUpdateMindMap's upsert-by-Id. A "restore/sync in place"
    /// capability, if wanted later, should be its own explicitly-labeled action, not a side effect
    /// of Import.
    /// </summary>
    public static void NormalizeImportedDocument(MindMapDocument document, string fallbackTitle)
    {
        document.Id = Guid.NewGuid();
        document.Title = string.IsNullOrWhiteSpace(document.Title)
            ? fallbackTitle
            : document.Title.Trim();
        document.Tags ??= new List<string>();
        document.Root ??= new MindMapNode();
        document.Root.Children ??= new List<MindMapNode>();
        document.CreatedAt = DateTime.UtcNow;
        document.LastModified = DateTime.Now;
        document.GroupId = null;
        document.Schedules = new List<NoteScheduleEntry>();
        document.IsPinned = false;

        // Same reasoning as document.Id above, one level down: a re-imported .scribmap carries
        // its nodes' original Ids in the JSON, which could collide with an existing library
        // entry's node Ids (e.g. re-importing your own backup, or a duplicate). Cross-link
        // targets are remapped alongside the regeneration so links between two nodes that are
        // BOTH still present after import keep working - only ones pointing outside the imported
        // tree end up dangling, which the badge/click handler already treats as an inert no-op.
        var idMap = new Dictionary<Guid, Guid>();
        RegenerateNodeIds(document.Root, idMap);
        RemapCrossLinkTargets(document.Root, idMap);
    }

    private static void RegenerateNodeIds(MindMapNode node, Dictionary<Guid, Guid> idMap)
    {
        var newId = Guid.NewGuid();
        idMap[node.Id] = newId;
        node.Id = newId;

        foreach (var child in node.Children)
            RegenerateNodeIds(child, idMap);
    }

    private static void RemapCrossLinkTargets(MindMapNode node, Dictionary<Guid, Guid> idMap)
    {
        if (node.CrossLinkTargetIds.Count > 0)
        {
            node.CrossLinkTargetIds = node.CrossLinkTargetIds
                .Select(oldId => idMap.TryGetValue(oldId, out var newId) ? newId : oldId)
                .ToList();
        }

        foreach (var child in node.Children)
            RemapCrossLinkTargets(child, idMap);
    }
}
