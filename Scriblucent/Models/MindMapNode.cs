using System.Text.Json.Serialization;

namespace Scriblucent.Models;

public sealed class MindMapNode
{
    public string Text { get; set; } = string.Empty;
    public List<MindMapNode> Children { get; set; } = new();
    public bool IsExpanded { get; set; } = true;

    // Styling properties
    public string? BackgroundColor { get; set; } // Hex color code
    public string? BorderColor { get; set; } // Hex color code
    public double BorderThickness { get; set; } = 1.0;
    public string? NodeShape { get; set; } = "Rectangle"; // Rectangle, Rounded, Circle, Ellipse
    public string? Icon { get; set; } // Emoji or icon identifier
    public string? IconBadgeColor { get; set; } = "#F59E0B"; // Default amber
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ManualX { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ManualY { get; set; }

    public bool HasChildren => Children.Count > 0;
}
