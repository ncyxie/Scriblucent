using Scriblucent.Localization;
using Scriblucent.Models;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

public sealed class MindMapConversionService
{
    private const int MaxNodeTextLength = 110;

    public async Task<MindMapNode?> ConvertToMindMapAsync(
        string noteTitle,
        string noteText,
        IProgress<BundledModelHostService.FlashcardProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(noteText))
            return null;

        AiInputGuard.EnsureSuitableStudyText(noteText);

        var normalizedTitle = NormalizeNodeText(noteTitle);
        var chunkingProfile = BundledModelHostService.Instance.GetSelectedChunkingProfile();
        var chunks = AiTextChunker.Split(noteText, AiChunkingPurpose.MindMap, chunkingProfile)
            .Where(AiInputGuard.IsSuitableStudyText)
            .ToList();

        if (chunks.Count == 0)
            throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));

        var combinedRoot = new MindMapNode
        {
            Text = normalizedTitle
        };

        var outputs = new List<string>();
        var repairedOutputs = new List<string>();
        var sawRefusal = false;

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkProgress = CreateChunkProgress(progress, i + 1, chunks.Count);
            var prompt = BuildPrompt(normalizedTitle, chunks[i], i + 1, chunks.Count);
            var output = await BundledModelHostService.Instance.CompleteAsync(
                prompt, nPredict: 1800, temperature: 0.15, progress: chunkProgress, cancellationToken);
            outputs.Add(output);

            if (AiInputGuard.IsRefusalOutput(output))
            {
                sawRefusal = true;
                continue;
            }

            var aiMap = ParseMindMap(output, normalizedTitle);
            if (aiMap is not null && IsUsefulMindMap(aiMap))
            {
                MergeMindMap(combinedRoot, aiMap);
                continue;
            }

            var repairPrompt = BuildRepairPrompt(normalizedTitle, chunks[i], output, i + 1, chunks.Count);
            var repairProgress = CreateRepairProgress(progress, "ConvertToMindMapStatusRepairing", i + 1, chunks.Count);
            var repaired = await BundledModelHostService.Instance.CompleteAsync(
                repairPrompt, nPredict: 1800, temperature: 0.1, progress: repairProgress, cancellationToken);
            repairedOutputs.Add(repaired);

            if (AiInputGuard.IsRefusalOutput(repaired))
            {
                sawRefusal = true;
                continue;
            }

            var repairedMap = ParseMindMap(repaired, normalizedTitle);
            if (repairedMap is not null && IsUsefulMindMap(repairedMap))
                MergeMindMap(combinedRoot, repairedMap);
        }

        if (IsUsefulMindMap(combinedRoot))
            return combinedRoot;

        if (sawRefusal && outputs.Count > 0 && outputs.All(AiInputGuard.IsRefusalOutput))
            throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));

        var fallback = BuildFallbackMindMap(normalizedTitle, noteText);
        if (IsUsefulMindMap(fallback))
            return fallback;

        var diagnosticPath = WriteParseDiagnostic(noteText, string.Join("\n\n--- CHUNK OUTPUT ---\n\n", outputs), string.Join("\n\n--- REPAIR OUTPUT ---\n\n", repairedOutputs));
        throw new InvalidOperationException($"Unable to parse AI output into a valid mind map. Diagnostic log: {diagnosticPath}");
    }

    private static IProgress<BundledModelHostService.FlashcardProgress>? CreateChunkProgress(
        IProgress<BundledModelHostService.FlashcardProgress>? progress,
        int chunkIndex,
        int chunkCount)
    {
        if (progress is null)
            return progress;

        return new Progress<BundledModelHostService.FlashcardProgress>(status =>
            progress.Report(status with
            {
                ChunkIndex = Math.Max(1, chunkIndex),
                ChunkCount = Math.Max(1, chunkCount)
            }));
    }

    private static IProgress<BundledModelHostService.FlashcardProgress>? CreateRepairProgress(
        IProgress<BundledModelHostService.FlashcardProgress>? progress,
        string repairStatusKey,
        int chunkIndex,
        int chunkCount)
    {
        if (progress is null)
            return progress;

        var safeChunkIndex = Math.Max(1, chunkIndex);
        var safeChunkCount = Math.Max(1, chunkCount);
        progress.Report(new BundledModelHostService.FlashcardProgress(
            repairStatusKey,
            ChunkIndex: safeChunkIndex,
            ChunkCount: safeChunkCount));

        return new Progress<BundledModelHostService.FlashcardProgress>(status =>
            progress.Report(status with
            {
                StatusKey = repairStatusKey,
                ChunkIndex = safeChunkIndex,
                ChunkCount = safeChunkCount
            }));
    }

    private static string BuildPrompt(string noteTitle, string sourceNote, int chunkIndex = 1, int chunkCount = 1)
    {
        var title = string.IsNullOrWhiteSpace(noteTitle) ? "Main topic" : noteTitle;
        var sectionInstruction = chunkCount > 1
            ? $"You are processing section {chunkIndex} of {chunkCount} from a longer note. Build a focused hierarchy for this section and avoid repeating generic branches."
            : "Build a focused hierarchy for the whole note.";

        return $"""
You are a strict mind map generator.
Never think out loud. Never explain. Never output reasoning, analysis, <think> tags, markdown fences, or extra text.

Convert SOURCE NOTE into a hierarchical mind map.
{sectionInstruction}
The main topic or note title must be the central idea.
Subsections and key points must become child branches and sub-branches.
Use the provided root title only for the root title.
Use ONLY SOURCE NOTE facts for branches and child nodes.
Use SOURCE NOTE's language and script for every generated label; use English only for structural markers.
Keep the structural marker "ROOT:" exactly as shown.
If SOURCE NOTE is random, incoherent, mostly symbols, image placeholders, only a few words, only one thin sentence, or does not contain enough meaningful study content, output exactly:
{AiInputGuard.RefusalOutput}
Do not invent context to make unsuitable text look useful.
If the note is not suitable for a hierarchy, still create the best concise hierarchy from the visible concepts.

Output ONLY in this exact indentation format:

ROOT: {title}
- Main branch
  - Child point
    - Sub point
- Another main branch

Rules:
- Use 3 to 8 main branches when possible.
- Keep node text concise, under 12 words.
- Do not number branches.
- Do not add facts not present in the note.

SOURCE NOTE:
{sourceNote}
""";
    }

    private static string BuildRepairPrompt(string noteTitle, string sourceNote, string previousOutput, int chunkIndex = 1, int chunkCount = 1)
    {
        var title = string.IsNullOrWhiteSpace(noteTitle) ? "Main topic" : noteTitle;
        var sectionInstruction = chunkCount > 1
            ? $"This is section {chunkIndex} of {chunkCount}; repair only the hierarchy for this section."
            : "Repair the hierarchy for the whole note.";

        return $"""
Repair the mind map output.
Ignore any noisy text, reasoning, comments, or markdown fences.
Use the provided root title only for the root title.
Use ONLY SOURCE NOTE facts for branches and child nodes.
Output ONLY this format:
{sectionInstruction}
Use SOURCE NOTE's language and script for every generated label; use English only for structural markers.
Keep the structural marker "ROOT:" exactly as shown.
If SOURCE NOTE is random, incoherent, mostly symbols, image placeholders, only a few words, only one thin sentence, or does not contain enough meaningful study content, output exactly:
{AiInputGuard.RefusalOutput}
Do not invent context to make unsuitable text look useful.

ROOT: {title}
- Main branch
  - Child point
    - Sub point

SOURCE NOTE:
{sourceNote}

PREVIOUS OUTPUT:
{previousOutput}
""";
    }

    private static MindMapNode? ParseMindMap(string rawOutput, string fallbackTitle)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return null;

        var cleaned = RemoveThinkAndArtifacts(rawOutput);
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

    private static bool TryParseTreeLine(string line, out int level, out string text)
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

    /// <summary>
    /// Parses freeform pasted/typed text into an outline tree for the mind-map editor's smart
    /// paste. Reuses TryParseTreeLine (same bullet/markdown-heading line parser AI mind-map
    /// generation already relies on), plus a fallback for lines with no bullet marker at all -
    /// pure indentation, which the AI-output parser never has to handle since the AI is always
    /// prompted to produce bullets, but is common in text copied from elsewhere (a plain
    /// tab-indented outline, another outliner's export, etc). Returns the parsed top-level nodes;
    /// each carries its own children built from deeper indentation.
    /// </summary>
    public static List<MindMapNode> ParseOutlineText(string text)
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

    private static MindMapNode? BuildFallbackMindMap(string noteTitle, string noteText)
    {
        var lines = noteText
            .Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count == 0)
            return null;

        var root = new MindMapNode
        {
            Text = string.IsNullOrWhiteSpace(noteTitle)
                ? NormalizeNodeText(lines[0])
                : NormalizeNodeText(noteTitle)
        };

        var stack = new List<(int Level, MindMapNode Node)> { (-1, root) };
        var parsed = 0;

        foreach (var line in lines)
        {
            if (!TryParseTreeLine(line, out var level, out var text))
                continue;

            text = NormalizeNodeText(text);
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, root.Text, StringComparison.OrdinalIgnoreCase))
                continue;

            while (stack.Count > 0 && stack[^1].Level >= level)
                stack.RemoveAt(stack.Count - 1);

            var parent = stack.Count == 0 ? root : stack[^1].Node;
            var node = new MindMapNode { Text = text };
            parent.Children.Add(node);
            stack.Add((level, node));
            parsed++;
        }

        if (parsed > 0)
            return root;

        var sentenceCandidates = Regex.Split(noteText, @"(?<=[.!?])\s+")
            .Select(NormalizeNodeText)
            .Where(text => text.Length >= 12)
            .Take(6)
            .ToList();

        foreach (var sentence in sentenceCandidates)
            root.Children.Add(new MindMapNode { Text = sentence });

        return root;
    }

    private static void MergeMindMap(MindMapNode targetRoot, MindMapNode sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(targetRoot.Text))
            targetRoot.Text = NormalizeNodeText(sourceRoot.Text);

        foreach (var child in sourceRoot.Children)
            MergeNode(targetRoot.Children, child);
    }

    private static void MergeNode(IList<MindMapNode> siblings, MindMapNode source)
    {
        var key = NormalizeMergeKey(source.Text);
        if (string.IsNullOrWhiteSpace(key))
            return;

        var existing = siblings.FirstOrDefault(node => string.Equals(NormalizeMergeKey(node.Text), key, StringComparison.Ordinal));
        if (existing is null)
        {
            siblings.Add(CloneMindMapNode(source));
            return;
        }

        foreach (var child in source.Children)
            MergeNode(existing.Children, child);
    }

    private static MindMapNode CloneMindMapNode(MindMapNode source)
    {
        var clone = new MindMapNode
        {
            Text = source.Text,
            IsExpanded = source.IsExpanded,
            BackgroundColor = source.BackgroundColor,
            BorderColor = source.BorderColor,
            BorderThickness = source.BorderThickness,
            NodeShape = source.NodeShape,
            Icons = new List<string>(source.Icons),
            IconBadgeColor = source.IconBadgeColor,
            IsBold = source.IsBold,
            IsItalic = source.IsItalic,
            TextColor = source.TextColor,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            Note = source.Note,
            LinkedNoteId = source.LinkedNoteId
            // CrossLinkTargetIds deliberately not copied here - this clone exists for AI-output
            // merge/dedup, where source is always freshly-parsed AI text with no cross-links yet,
            // and copying Ids verbatim without the remap MainViewModel.CloneMindMapNode does
            // would risk stale references if that ever changed.
        };

        foreach (var child in source.Children)
            clone.Children.Add(CloneMindMapNode(child));

        return clone;
    }

    private static string NormalizeMergeKey(string value)
    {
        var normalized = NormalizeNodeText(value).ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}\s]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static bool IsUsefulMindMap(MindMapNode? root)
    {
        return root is not null
            && !string.IsNullOrWhiteSpace(root.Text)
            && root.Children.Count > 0
            && CountNodes(root) >= 3;
    }

    private static int CountNodes(MindMapNode node)
    {
        return 1 + node.Children.Sum(CountNodes);
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

    private static string NormalizeNodeText(string value)
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

    private static string RemoveThinkAndArtifacts(string text)
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

    private static string WriteParseDiagnostic(string sourceNote, string firstOutput, string repairedOutput)
    {
        try
        {
            var diagnosticsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Scriblucent",
                "AiCache",
                "diagnostics");

            Directory.CreateDirectory(diagnosticsDir);
            var path = Path.Combine(diagnosticsDir, $"mindmap-parse-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"TimestampUtc: {DateTime.UtcNow:O}");
            sb.AppendLine("--- SOURCE NOTE ---");
            sb.AppendLine(sourceNote);
            sb.AppendLine();
            sb.AppendLine("--- FIRST OUTPUT ---");
            sb.AppendLine(firstOutput);
            sb.AppendLine();
            sb.AppendLine("--- REPAIR OUTPUT ---");
            sb.AppendLine(repairedOutput);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
        catch
        {
            return "(failed to write parse diagnostic log)";
        }
    }
}
