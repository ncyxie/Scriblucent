using Scriblucent.Models;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal static class MindMapOutputParser
{
    private const int MaxNodeTextLength = 110;

    internal static MindMapNode? Parse(string rawOutput, string fallbackTitle)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return null;

        var cleaned = Clean(rawOutput);
        var lines = cleaned
            .Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            .ToList();

        if (lines.Count == 0)
            return null;

        var rootText = NormalizeNodeText(fallbackTitle);
        var root = new MindMapNode
        {
            Text = string.IsNullOrWhiteSpace(rootText)
                ? NormalizeNodeText(lines[0])
                : rootText
        };

        var stack = new List<(int Level, MindMapNode Node)> { (-1, root) };
        var parsedNodes = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (TryParseRootLine(line, out var parsedRoot))
            {
                root.Text = parsedRoot;
                continue;
            }

            if (!TryParseTreeLine(line, out var level, out var nodeText))
                continue;

            nodeText = NormalizeNodeText(nodeText);
            if (string.IsNullOrWhiteSpace(nodeText))
                continue;

            while (stack.Count > 0 && stack[^1].Level >= level)
                stack.RemoveAt(stack.Count - 1);

            var parent = stack.Count == 0 ? root : stack[^1].Node;
            var node = new MindMapNode { Text = nodeText };
            parent.Children.Add(node);
            stack.Add((level, node));
            parsedNodes++;
        }

        if (parsedNodes == 0)
            return null;

        PruneEmptyNodes(root);
        return root;
    }

    internal static List<MindMapNode> ParseOutlineText(string text)
    {
        var topLevel = new List<MindMapNode>();
        if (string.IsNullOrWhiteSpace(text))
            return topLevel;

        var lines = text
            .Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var stack = new List<(int Level, MindMapNode Node)>();

        foreach (var line in lines)
        {
            if (!TryParseOutlineLine(line, out var level, out var rawText))
                continue;

            var nodeText = NormalizeNodeText(rawText);
            if (string.IsNullOrWhiteSpace(nodeText))
                continue;

            while (stack.Count > 0 && stack[^1].Level >= level)
                stack.RemoveAt(stack.Count - 1);

            var node = new MindMapNode { Text = nodeText };
            if (stack.Count == 0)
                topLevel.Add(node);
            else
                stack[^1].Node.Children.Add(node);

            stack.Add((level, node));
        }

        return topLevel;
    }

    internal static bool TryParseTreeLine(string line, out int level, out string text)
    {
        var heading = Regex.Match(line, @"^(?<marks>#{1,6})\s+(?<text>.+)$");
        if (heading.Success)
        {
            level = Math.Max(0, heading.Groups["marks"].Value.Length - 1);
            text = heading.Groups["text"].Value;
            return true;
        }

        var bullet = Regex.Match(line, @"^(?<indent>\s*)(?:[-*•]\s+|\d+[\.)]\s+)(?<text>.+)$");
        if (!bullet.Success)
        {
            level = 0;
            text = string.Empty;
            return false;
        }

        var indent = bullet.Groups["indent"].Value.Replace("\t", "    ", StringComparison.Ordinal).Length;
        level = Math.Max(0, indent / 2);
        text = bullet.Groups["text"].Value;
        return true;
    }

    internal static string NormalizeNodeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim();
        normalized = Regex.Replace(normalized, @"^\s*(?:[-*•]|\d+[\.)])\s*", string.Empty);
        normalized = Regex.Replace(normalized, @"^(?:ROOT|CENTRAL|CENTER|TITLE|MAIN TOPIC)\s*[:：-]\s*", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+", " ");
        normalized = normalized.Trim(' ', '"', '\'', '`', ':', '-', '•');

        if (normalized.Length > MaxNodeTextLength)
            normalized = normalized[..MaxNodeTextLength].TrimEnd() + "...";

        return normalized;
    }

    internal static string Clean(string text)
    {
        var withoutAnsi = Regex.Replace(text, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty);
        var withoutThink = Regex.Replace(withoutAnsi, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        withoutThink = withoutThink.Replace("<think>", string.Empty, StringComparison.OrdinalIgnoreCase);
        withoutThink = withoutThink.Replace("</think>", string.Empty, StringComparison.OrdinalIgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"\[Start thinking\].*$", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*>.*$", string.Empty, RegexOptions.Multiline);
        withoutThink = Regex.Replace(withoutThink, @"^\s*Thinking Process:.*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*\[?end of text\]?\s*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*<\|end\|>\s*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*EOF\s*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        withoutThink = withoutThink.Replace("EOF by user", string.Empty, StringComparison.OrdinalIgnoreCase);
        withoutThink = withoutThink.Replace("Interrupted by user", string.Empty, StringComparison.OrdinalIgnoreCase);
        return withoutThink.Trim();
    }

    private static bool TryParseRootLine(string line, out string rootText)
    {
        var match = Regex.Match(line.Trim(), @"^(?:ROOT|CENTRAL|CENTER|TITLE|MAIN TOPIC)\s*[:：-]\s*(.+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            rootText = NormalizeNodeText(match.Groups[1].Value);
            return !string.IsNullOrWhiteSpace(rootText);
        }

        rootText = string.Empty;
        return false;
    }

    private static bool TryParseOutlineLine(string line, out int level, out string text)
    {
        if (TryParseTreeLine(line, out level, out text))
            return true;

        var trimmed = line.TrimStart(' ', '\t');
        if (trimmed.Length == 0)
        {
            level = 0;
            text = string.Empty;
            return false;
        }

        var indentText = line[..^trimmed.Length].Replace("\t", "    ", StringComparison.Ordinal);
        level = Math.Max(0, indentText.Length / 2);
        text = trimmed;
        return true;
    }

    private static void PruneEmptyNodes(MindMapNode node)
    {
        for (var i = node.Children.Count - 1; i >= 0; i--)
        {
            var child = node.Children[i];
            PruneEmptyNodes(child);
            if (string.IsNullOrWhiteSpace(child.Text))
                node.Children.RemoveAt(i);
        }
    }
}
