using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services.AI;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal sealed class MindMapConversionService
{
    private readonly ITextGenerationBackend _backend;

    public MindMapConversionService(ITextGenerationBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public async Task<MindMapNode?> ConvertToMindMapAsync(
        string noteTitle,
        string noteText,
        IProgress<AiGenerationProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(noteText))
            return null;

        var normalizedTitle = MindMapOutputParser.NormalizeNodeText(noteTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            normalizedTitle = MindMapOutputParser.NormalizeNodeText(
                noteText.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Main topic");

        var combinedRoot = new MindMapNode { Text = normalizedTitle };
        var profile = SamplingProfiles.Default;
        var repairProfile = SamplingProfiles.Repair;

        // Use shared pipeline with per-chunk immediate repair (the MindMap's true per-chunk
        // recovery semantics) instead of the previous hand-rolled loop - keeps chunking,
        // model-key snapping, coverage reporting and diagnostics centralized.
        // Fallback heuristic is handled outside the pipeline: if pipeline throws parse-failed,
        // we still try the local fallback before surfacing the diagnostic.
        try
        {
            var result = await AiGenerationPipeline.RunAsync<MindMapNode>(
                noteText,
                AiChunkingPurpose.MindMap,
                _backend,
                new AiGenerationCallbacks
                {
                    PerChunkRepair = true,
                    BuildChunkPrompt = (chunk, chunks) =>
                    {
                        var branchTarget = CalculateTargetBranchCount(chunk.Text);
                        return BuildPrompt(normalizedTitle, chunk.Text, branchTarget, chunk.Index + 1, chunk.TotalCount);
                    },
                    BuildPerChunkRepairPrompt = (chunk, previousOutput, chunks) =>
                    {
                        var branchTarget = CalculateTargetBranchCount(chunk.Text);
                        return BuildRepairPrompt(normalizedTitle, chunk.Text, previousOutput, branchTarget, chunk.Index + 1, chunk.TotalCount);
                    },
                    BuildChunkGrammar = chunk =>
                    {
                        var (branchMin, branchMax) = CalculateTargetBranchCountRange(chunk.Text);
                        return OutputGrammars.MindMapGrammar(branchMin, branchMax);
                    },
                    BuildPerChunkRepairGrammar = chunk =>
                    {
                        var (branchMin, branchMax) = CalculateTargetBranchCountRange(chunk.Text);
                        return OutputGrammars.MindMapGrammar(branchMin, branchMax);
                    },
                    // Keep batched fields null when per-chunk is active; pipeline will not use them
                    MaxTokens = 900,
                    RepairMaxTokens = 900,
                    Temperature = profile.Temperature,
                    RepairTemperature = repairProfile.Temperature,
                    TopP = profile.TopP,
                    RepairTopP = repairProfile.TopP,
                    TopK = profile.TopK,
                    RepairTopK = repairProfile.TopK,
                    PresencePenalty = profile.PresencePenalty,
                    RepairPresencePenalty = repairProfile.PresencePenalty,
                    RepetitionPenalty = profile.RepetitionPenalty,
                    RepairRepetitionPenalty = repairProfile.RepetitionPenalty,
                    GrammarRoot = OutputGrammars.MindMapRoot,
                    OnChunkGenerated = (output, chunks, index) =>
                    {
                        var aiMap = MindMapOutputParser.Parse(output, normalizedTitle);
                        if (aiMap is not null && IsUsefulMindMap(aiMap))
                        {
                            MergeMindMap(combinedRoot, aiMap);
                            return Task.FromResult(1);
                        }
                        return Task.FromResult(0);
                    },
                    Finalize = () =>
                    {
                        // Only a useful merged tree counts as success - mirrors prior IsUsefulMindMap(combinedRoot) gate
                        if (IsUsefulMindMap(combinedRoot))
                            return Task.FromResult<object?>(combinedRoot);
                        return Task.FromResult<object?>(null);
                    },
                    DiagnosticTag = _ => "mindmap"
                },
                progress, ct);

            return result;
        }
        catch (InvalidOperationException ex) when (ex.Data.Contains("DiagnosticPath"))
        {
            // Pipeline parse failure - try local heuristic fallback before rethrowing with diagnostic
            var fallback = BuildFallbackMindMap(normalizedTitle, noteText);
            if (IsUsefulMindMap(fallback))
                return fallback;

            throw;
        }
        catch (AiInputRejectedException)
        {
            throw;
        }
    }

    /// <summary>
    /// Parses freeform pasted/typed text into an outline tree for the mind-map editor's smart
    /// paste. The parsing implementation is shared with the AI output parser.
    /// </summary>
    public static List<MindMapNode> ParseOutlineText(string text)
        => MindMapOutputParser.ParseOutlineText(text);

    private static string BuildPrompt(string noteTitle, string sourceNote, int branchTarget, int chunkIndex = 1, int chunkCount = 1)
    {
        var title = string.IsNullOrWhiteSpace(noteTitle) ? "Main topic" : noteTitle;
        var section = chunkCount > 1
            ? $"Section {chunkIndex} of {chunkCount}. "
            : "";

        // The example below is deliberately generic filler, never the real title/content - an
        // earlier version put the real title on the ROOT: line, and the model would just copy the
        // whole template verbatim as its "answer" instead of writing real content, since that's a
        // valid, low-effort completion under grammar-constrained decoding. Verified directly.
        //
        // The prompt names a CONCRETE branch count, not a range - traced real runs show a
        // grammar-constrained model consistently settles on the minimum of whatever range it is
        // offered (offered 3 to 7, it produced exactly 3). The grammar band around this target
        // (see CalculateTargetBranchCountRange) remains only as a mechanical backstop.
        return $"""
{section}Convert this note into a mind map about "{title}". Split it into exactly {branchTarget}
top-level branches (one per major topic/section), each with its own supporting
details. Use the exact format shown below - the example content is generic filler, replace it
with real points from the note; do not copy it.

Be concise: cover only the key points, not every sentence. Each point should be a short phrase,
not a full sentence. Do not restate the same point twice.

ROOT: Topic name
- First major topic
  - Supporting detail
    - Extra detail
  - Another supporting detail
- Second major topic
  - Supporting detail

Note:
{sourceNote}
""";
    }

    private static string BuildRepairPrompt(string noteTitle, string sourceNote, string previousOutput, int branchTarget, int chunkIndex = 1, int chunkCount = 1)
    {
        var title = string.IsNullOrWhiteSpace(noteTitle) ? "Main topic" : noteTitle;
        var section = chunkCount > 1
            ? $"Section {chunkIndex} of {chunkCount}. "
            : "";

        return $"""
{section}Repair this mind map output about "{title}". Split it into exactly {branchTarget}
top-level branches (one per major topic/section). Be concise - key points only, short phrases, no
repeats. Use the exact format shown - the example content is generic filler, replace it with real
points from the note; do not copy it.

ROOT: Topic name
- First major topic
  - Supporting detail

Note:
{sourceNote}

Previous output:
{previousOutput}
""";
    }

    // Turns note length into a concrete branch target for the prompt (see BuildPrompt's comment -
    // constrained models take range minimums, so the prompt asks for one number) and a tight band
    // the grammar enforces as a backstop. The divisor (45 words/branch) is calibrated against a
    // verified real-model result where a ~400-word note naturally settled on 9 branches and looked
    // right; the band floor sits at target-1 rather than target-2 for the same minimum-taking
    // reason - the floor is what the model actually produces when it under-delivers.
    internal static int CalculateTargetBranchCount(string sourceNote)
    {
        var wordCount = CountWords(sourceNote);
        return Math.Clamp((int)Math.Round(wordCount / 45.0), 3, 12);
    }

    private static (int Min, int Max) CalculateTargetBranchCountRange(string sourceNote)
    {
        var branchTarget = CalculateTargetBranchCount(sourceNote);
        return (Math.Max(2, branchTarget - 1), branchTarget + 2);
    }

    private static int CountWords(string text)
    {
        return Regex.Matches(text, @"[\p{L}][\p{L}\p{Mn}'’-]{1,}").Count;
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
                ? MindMapOutputParser.NormalizeNodeText(lines[0])
                : MindMapOutputParser.NormalizeNodeText(noteTitle)
        };

        var stack = new List<(int Level, MindMapNode Node)> { (-1, root) };
        var parsed = 0;

        foreach (var line in lines)
        {
            if (!MindMapOutputParser.TryParseTreeLine(line, out var level, out var text))
                continue;

            text = MindMapOutputParser.NormalizeNodeText(text);
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
            .Select(MindMapOutputParser.NormalizeNodeText)
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
            targetRoot.Text = MindMapOutputParser.NormalizeNodeText(sourceRoot.Text);

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
        };

        foreach (var child in source.Children)
            clone.Children.Add(CloneMindMapNode(child));

        return clone;
    }

    private static string NormalizeMergeKey(string value)
    {
        var normalized = MindMapOutputParser.NormalizeNodeText(value).ToLowerInvariant();
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

}
