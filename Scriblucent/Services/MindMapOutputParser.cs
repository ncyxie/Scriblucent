using Scriblucent.Models;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal static class MindMapOutputParser
{
    private const int MaxNodeTextLength = 110;

    internal static List<MindMapNode> ParseOutlineText(string text)
    {
        var topLevel = new List<MindMapNode>();
        if (string.IsNullOrWhiteSpace(text))
            return topLevel;
        var stack = new List<(int Level, MindMapNode Node)>();
        foreach (var line in text.Replace("\r", string.Empty, StringComparison.Ordinal)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd()))
        {
            if (!TryParseOutlineLine(line, out var level, out var rawText))
                continue;
            var nodeText = NormalizeNodeText(rawText);
            if (nodeText.Length == 0)
                continue;
            while (stack.Count > 0 && stack[^1].Level >= level)
                stack.RemoveAt(stack.Count - 1);
            var node = new MindMapNode { Text = nodeText };
            if (stack.Count == 0) topLevel.Add(node); else stack[^1].Node.Children.Add(node);
            stack.Add((level, node));
        }
        return topLevel;
    }

    internal static string NormalizeNodeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = Regex.Replace(value.Trim(), @"^\s*(?:[-*•]|\d+[\.)])\s*", string.Empty);
        normalized = Regex.Replace(normalized, @"^(?:ROOT|CENTRAL|CENTER|TITLE|MAIN TOPIC)\s*[:：-]\s*", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim(' ', '"', '\'', '`', ':', '-', '•');
        return normalized.Length > MaxNodeTextLength ? normalized[..MaxNodeTextLength].TrimEnd() + "..." : normalized;
    }

    private static bool TryParseOutlineLine(string line, out int level, out string text)
    {
        var heading = Regex.Match(line, @"^(?<marks>#{1,6})\s+(?<text>.+)$");
        if (heading.Success)
        {
            level = Math.Max(0, heading.Groups["marks"].Value.Length - 1);
            text = heading.Groups["text"].Value;
            return true;
        }
        var bullet = Regex.Match(line, @"^(?<indent>\s*)(?:[-*•]\s+|\d+[\.)]\s+)(?<text>.+)$");
        if (bullet.Success)
        {
            level = Math.Max(0, bullet.Groups["indent"].Value.Replace("\t", "    ", StringComparison.Ordinal).Length / 2);
            text = bullet.Groups["text"].Value;
            return true;
        }
        var trimmed = line.TrimStart(' ', '\t');
        level = Math.Max(0, line[..^trimmed.Length].Replace("\t", "    ", StringComparison.Ordinal).Length / 2);
        text = trimmed;
        return trimmed.Length > 0;
    }
}
