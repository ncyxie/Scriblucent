using System.Text;
using Scriblucent.Localization;
using Scriblucent.Services;

namespace Scriblucent.Services.AI;

internal static class AiChunkingPipeline
{
    private const int DefaultOverlapTokens = 75;

    public static async Task<IReadOnlyList<TextChunk>> PrepareChunksAsync(
        string noteText,
        AiChunkingPurpose purpose,
        ITokenCounter? tokenCounter,
        string? modelKey = null,
        int overlapTokens = DefaultOverlapTokens,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(noteText))
            return Array.Empty<TextChunk>();

        var normalized = NormalizeText(noteText);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<TextChunk>();

        // Tokenize full text once to get accurate total and ratio
        double charToTokenRatio;
        int totalTokens;
        if (tokenCounter is not null)
        {
            totalTokens = await tokenCounter.CountTokensAsync(normalized, ct);
            charToTokenRatio = totalTokens > 0 ? (double)normalized.Length / totalTokens : 3.25;
        }
        else
        {
            // Fallback: estimate token count from characters
            totalTokens = (int)Math.Ceiling(normalized.Length / 3.25);
            charToTokenRatio = 3.25;
        }

        var profile = BundledModelHostService.Instance.GetSelectedChunkingProfile(modelKey);
        var targetTokens = CalculateTargetTokens(totalTokens, purpose, profile);
        var targetChars = (int)Math.Round(targetTokens * charToTokenRatio);
        var softLimitChars = (int)Math.Round(targetChars * 1.18);
        var minimumUsefulChars = CalculateMinimumUsefulChars(targetChars, purpose, profile);

        var segments = SplitIntoNaturalSegments(normalized, targetChars);
        if (segments.Count == 0)
        {
            var tokenCount = tokenCounter is not null
                ? await tokenCounter.CountTokensAsync(normalized, ct)
                : Math.Max(1, (int)Math.Ceiling(normalized.Length / 3.25));
            return [new TextChunk(normalized.Trim(), 0, 1, 0, normalized.Length, tokenCount)];
        }

        var rawChunks = PackSegments(segments, targetChars, softLimitChars, minimumUsefulChars);
        MergeTinyTail(rawChunks, minimumUsefulChars, softLimitChars);
        MergeUndersizedChunks(rawChunks, minimumUsefulChars, softLimitChars, targetChars);

        // Filter empty and trim
        rawChunks = rawChunks
            .Select(c => c.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        if (rawChunks.Count == 0)
            return Array.Empty<TextChunk>();

        // Apply overlap and compute provenance
        var result = await BuildTextChunksAsync(normalized, rawChunks, overlapTokens, tokenCounter, ct);
        return result;
    }

    private static async Task<List<TextChunk>> BuildTextChunksAsync(
        string fullText,
        List<string> rawChunks,
        int overlapTokens,
        ITokenCounter? tokenCounter,
        CancellationToken ct)
    {
        var chunks = new List<TextChunk>();
        var searchStart = 0;

        for (var i = 0; i < rawChunks.Count; i++)
        {
            var chunkText = rawChunks[i];

            // Find character offset in original text
            var startOffset = FindCharOffset(fullText, chunkText, searchStart);
            var endOffset = startOffset + chunkText.Length;

            // Token count for this chunk
            var tokenCount = tokenCounter is not null
                ? await tokenCounter.CountTokensAsync(chunkText, ct)
                : Math.Max(1, chunkText.Length / 4);

            chunks.Add(new TextChunk(
                Text: chunkText,
                Index: i,
                TotalCount: rawChunks.Count,
                StartCharOffset: startOffset,
                EndCharOffset: endOffset,
                TokenCount: tokenCount));

            // Advance search start for next chunk (with overlap consideration)
            if (i < rawChunks.Count - 1)
                searchStart = Math.Max(searchStart + 1, startOffset + chunkText.Length / 2);
        }

        // Apply overlap: prepend tail of previous chunk to current chunk
        if (overlapTokens > 0 && chunks.Count > 1)
        {
            chunks = await ApplyOverlapAsync(chunks, fullText, overlapTokens, tokenCounter, ct);
        }

        return chunks;
    }

    private static async Task<List<TextChunk>> ApplyOverlapAsync(
        List<TextChunk> chunks,
        string fullText,
        int overlapTokens,
        ITokenCounter? tokenCounter,
        CancellationToken ct)
    {
        var result = new List<TextChunk> { chunks[0] };

        for (var i = 1; i < chunks.Count; i++)
        {
            var prevChunk = chunks[i - 1];
            var currentChunk = chunks[i];

            // Take the last ~overlapTokens worth of text from previous chunk
            var prevText = prevChunk.Text;
            var overlapText = TakeLastTokens(prevText, overlapTokens);

            if (!string.IsNullOrWhiteSpace(overlapText))
            {
                var overlappedText = overlapText + "\n\n" + currentChunk.Text;
                var tokenCount = tokenCounter is not null
                    ? await tokenCounter.CountTokensAsync(overlappedText, ct)
                    : Math.Max(1, overlappedText.Length / 4);

                result.Add(new TextChunk(
                    Text: overlappedText,
                    Index: currentChunk.Index,
                    TotalCount: currentChunk.TotalCount,
                    StartCharOffset: currentChunk.StartCharOffset - overlapText.Length,
                    EndCharOffset: currentChunk.EndCharOffset,
                    TokenCount: tokenCount));
            }
            else
            {
                result.Add(currentChunk);
            }
        }

        return result;
    }

    private static string TakeLastTokens(string text, int tokenCount)
    {
        // Approximate: split by any whitespace (\n, \t, multiple spaces, CJK without spaces
        // falls back to char-level below) and take last N words.
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // CJK text often has no spaces - fall back to character-based slice so overlap still
        // provides context instead of returning the whole chunk or nothing.
        if (words.Length <= 1 && text.Length > tokenCount * 2)
        {
            var approxChars = (int)Math.Ceiling(tokenCount * 3.25);
            if (approxChars < text.Length)
                return text[^approxChars..].TrimStart();
            return text;
        }

        var approxTokensPerWord = 1.3;
        var wordCount = Math.Min(words.Length, (int)Math.Ceiling(tokenCount / approxTokensPerWord));

        if (wordCount >= words.Length)
            return text;

        return string.Join(" ", words.Skip(words.Length - wordCount));
    }

    private static int CalculateTargetTokens(
        int totalTokens,
        AiChunkingPurpose purpose,
        AiChunkingModelProfile modelProfile)
    {
        var modelScale = modelProfile switch
        {
            AiChunkingModelProfile.Small => 0.60,
            AiChunkingModelProfile.Medium => 0.80,
            _ => 1.0
        };

        var minimum = purpose switch
        {
            AiChunkingPurpose.Flashcards => 300,
            AiChunkingPurpose.Quiz => 300,
            _ => 450
        };
        minimum = (int)Math.Round(minimum * modelScale);

        var maximum = purpose switch
        {
            AiChunkingPurpose.Flashcards => 900,
            AiChunkingPurpose.Quiz => 900,
            _ => 1200
        };
        maximum = (int)Math.Round(maximum * modelScale);

        var singlePassLimit = purpose switch
        {
            AiChunkingPurpose.Flashcards => 420,
            AiChunkingPurpose.Quiz => 420,
            _ => 600
        };
        singlePassLimit = (int)Math.Round(singlePassLimit * modelScale);

        if (totalTokens <= singlePassLimit)
            return Math.Max(totalTokens, minimum);

        var scale = purpose switch
        {
            AiChunkingPurpose.Flashcards => 5.5,
            AiChunkingPurpose.Quiz => 5.5,
            _ => 6.5
        } * modelScale;
        var target = minimum + (int)Math.Round(Math.Sqrt(totalTokens) * scale);

        return Math.Clamp(target, minimum, maximum);
    }

    private static int CalculateMinimumUsefulChars(
        int targetChars,
        AiChunkingPurpose purpose,
        AiChunkingModelProfile modelProfile)
    {
        var share = purpose switch
        {
            AiChunkingPurpose.Flashcards => 0.42,
            AiChunkingPurpose.Quiz => 0.42,
            _ => 0.48
        };
        var floor = modelProfile switch
        {
            AiChunkingModelProfile.Small => 280,
            AiChunkingModelProfile.Medium => 360,
            _ => 450
        };
        return Math.Max(floor, (int)Math.Round(targetChars * share));
    }

    private static List<string> SplitIntoNaturalSegments(string text, int targetChars)
    {
        var segments = new List<string>();
        var paragraphs = text.Split('\n', StringSplitOptions.None);
        var paragraphBuilder = new StringBuilder();

        foreach (var rawParagraph in paragraphs)
        {
            var paragraph = rawParagraph.Trim();
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                FlushParagraph(paragraphBuilder, segments, targetChars);
                continue;
            }

            if (paragraphBuilder.Length > 0)
                paragraphBuilder.Append('\n'); // NormalizeText already collapsed \r\n/\r to \n -
                                                // AppendLine() would insert Environment.NewLine
                                                // (\r\n on Windows), desyncing FindCharOffset's
                                                // ordinal search against the LF-only source text.
            paragraphBuilder.Append(paragraph);

            // Professional: keep heading with its body — never leave a heading alone in a segment
            // (would create a tiny 45-char chunk with no context). Defer flush if last added paragraph is a heading.
            if (IsHeadingParagraph(paragraph))
                continue;

            if (paragraphBuilder.Length >= targetChars)
                FlushParagraph(paragraphBuilder, segments, targetChars);
        }

        FlushParagraph(paragraphBuilder, segments, targetChars);
        return segments;
    }

    private static void FlushParagraph(StringBuilder builder, ICollection<string> segments, int targetChars)
    {
        if (builder.Length == 0)
            return;

        var paragraph = builder.ToString().Trim();
        builder.Clear();

        if (paragraph.Length <= targetChars * 1.35)
        {
            segments.Add(paragraph);
            return;
        }

        // List-aware split: keep bullet items atomic, split between items not inside an item
        if (IsListParagraph(paragraph))
        {
            foreach (var listChunk in SplitListParagraph(paragraph, targetChars))
                segments.Add(listChunk);
            return;
        }

        foreach (var sentenceGroup in SplitLongParagraph(paragraph, targetChars))
            segments.Add(sentenceGroup);
    }

    private static bool IsHeadingParagraph(string paragraph)
    {
        var trimmed = paragraph.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 80)
            return false;
        // Markdown heading or single line ending with colon, no sentence terminator
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\s*#{1,6}\s+.+"))
            return true;
        if (trimmed.EndsWith(":", StringComparison.Ordinal) && !trimmed.Contains('.', StringComparison.Ordinal) && !trimmed.Contains('!', StringComparison.Ordinal) && !trimmed.Contains('?', StringComparison.Ordinal))
            return true;
        return false;
    }

    private static bool IsListParagraph(string paragraph)
    {
        // Contains at least two bullet/numbered lines
        var lines = paragraph.Split('\n');
        var listLines = lines.Count(l => System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), @"^([-*•]|\d+[.)])\s+\S"));
        return listLines >= 2;
    }

    private static IEnumerable<string> SplitListParagraph(string paragraph, int targetChars)
    {
        var lines = paragraph.Split('\n').Select(l => l.TrimEnd()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var chunks = new List<string>();
        var builder = new StringBuilder();
        var softLimit = (int)Math.Round(targetChars * 1.12);

        foreach (var line in lines)
        {
            var isListItem = System.Text.RegularExpressions.Regex.IsMatch(line.Trim(), @"^([-*•]|\d+[.)])\s+");
            // Keep heading-like line that precedes list with its list
            if (builder.Length > 0 && builder.Length + line.Length + 1 > softLimit && builder.Length >= targetChars * 0.3)
            {
                chunks.Add(builder.ToString().Trim());
                builder.Clear();
            }

            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(line);

            // If current builder exceeds softLimit and next line is also list item, we can flush at item boundary
            // This ensures we split between items, not inside an item's text
        }

        if (builder.Length > 0)
        {
            // If builder still too long, fall back to sentence/word split but keep item boundaries preferred
            if (builder.Length <= targetChars * 1.35)
                chunks.Add(builder.ToString().Trim());
            else
            {
                // Split remaining builder by respecting item boundaries: try to split at item starts
                var text = builder.ToString();
                var itemStarts = System.Text.RegularExpressions.Regex.Matches(text, @"(?m)^\s*([-*•]|\d+[.)])\s+");
                if (itemStarts.Count > 1)
                {
                    // Split at item boundaries
                    var current = new StringBuilder();
                    foreach (var l in text.Split('\n'))
                    {
                        if (current.Length > 0 && current.Length + l.Length + 1 > targetChars && current.Length >= targetChars * 0.3)
                        {
                            chunks.Add(current.ToString().Trim());
                            current.Clear();
                        }
                        if (current.Length > 0) current.Append('\n');
                        current.Append(l);
                    }
                    if (current.Length > 0) chunks.Add(current.ToString().Trim());
                }
                else
                {
                    foreach (var part in SplitByWords(text, targetChars))
                        chunks.Add(part);
                }
            }
        }

        return chunks;
    }

    private static IEnumerable<string> SplitLongParagraph(string paragraph, int targetChars)
    {
        var sentences = System.Text.RegularExpressions.Regex
            .Split(paragraph, @"(?<=[.!?])\s+")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (sentences.Count <= 1)
            return SplitByWords(paragraph, targetChars);

        var chunks = new List<string>();
        var builder = new StringBuilder();
        var softLimit = (int)Math.Round(targetChars * 1.12);

        foreach (var sentence in sentences)
        {
            if (builder.Length > 0 && builder.Length + sentence.Length + 1 > softLimit)
            {
                // Don't emit tiny builder alone
                if (builder.Length >= targetChars * 0.3)
                {
                    chunks.Add(builder.ToString().Trim());
                    builder.Clear();
                }
            }

            if (sentence.Length > softLimit)
            {
                var effectiveSentence = sentence;
                if (builder.Length > 0 && builder.Length < targetChars * 0.3)
                {
                    // Tiny builder (e.g. 45-char heading) should not be emitted alone - merge it
                    // into the long sentence before splitting so first slice contains both
                    effectiveSentence = builder.ToString().Trim() + " " + sentence;
                    builder.Clear();
                }
                else if (builder.Length > 0)
                {
                    chunks.Add(builder.ToString().Trim());
                    builder.Clear();
                }
                chunks.AddRange(SplitByWords(effectiveSentence, targetChars));
                continue;
            }

            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(sentence);
        }

        if (builder.Length > 0)
            chunks.Add(builder.ToString().Trim());

        return chunks;
    }

    private static IEnumerable<string> SplitByWords(string text, int targetChars)
    {
        var chunks = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var builder = new StringBuilder();

        foreach (var word in words)
        {
            if (builder.Length > 0 && builder.Length + word.Length + 1 > targetChars)
            {
                chunks.Add(builder.ToString().Trim());
                builder.Clear();
            }

            if (word.Length > targetChars)
            {
                if (builder.Length > 0)
                {
                    chunks.Add(builder.ToString().Trim());
                    builder.Clear();
                }
                var startIdx = chunks.Count;
                for (var i = 0; i < word.Length; i += targetChars)
                    chunks.Add(word.Substring(i, Math.Min(targetChars, word.Length - i)));
                // Merge tiny tail slice (e.g. 45 chars after 975) into previous slice
                if (chunks.Count > startIdx + 1 && chunks[^1].Length < targetChars * 0.3)
                {
                    chunks[^2] = chunks[^2] + chunks[^1];
                    chunks.RemoveAt(chunks.Count - 1);
                }
                continue;
            }

            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(word);
        }

        if (builder.Length > 0)
            chunks.Add(builder.ToString().Trim());

        return chunks;
    }

    private static List<string> PackSegments(
        IEnumerable<string> segments,
        int targetChars,
        int softLimit,
        int minimumUsefulChunk)
    {
        var chunks = new List<string>();
        var builder = new StringBuilder();

        foreach (var segment in segments.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var separatorLength = builder.Length == 0 ? 0 : 2;
            var combinedLength = builder.Length + separatorLength + segment.Length;

            if (builder.Length > 0
                && combinedLength > targetChars
                && (builder.Length >= minimumUsefulChunk || combinedLength > softLimit))
            {
                // Never emit a tiny builder (<0.5*minimum) alone - it pays a full LLM call for ~10 tokens.
                // Keep it with the next segment even if combined exceeds softLimit; MergeUndersizedChunks
                // later allows up to target*1.35 for such merges.
                var tinyThreshold = (int)Math.Round(minimumUsefulChunk * 0.5);
                if (builder.Length >= tinyThreshold)
                {
                    chunks.Add(builder.ToString().Trim());
                    builder.Clear();
                }
            }

            if (builder.Length > 0)
                builder.Append('\n').Append('\n'); // see the identical note above - keep LF-only
            builder.Append(segment.Trim());
        }

        if (builder.Length > 0)
            chunks.Add(builder.ToString().Trim());

        return chunks;
    }

    private static void MergeTinyTail(IList<string> chunks, int minimumUsefulChunk, int softLimit)
    {
        if (chunks.Count < 2)
            return;

        var lastIndex = chunks.Count - 1;
        var last = chunks[lastIndex];
        if (last.Length >= minimumUsefulChunk)
            return;

        var previous = chunks[lastIndex - 1];
        if (previous.Length + last.Length + 2 > softLimit)
            return;

        chunks[lastIndex - 1] = $"{previous}\n\n{last}".Trim();
        chunks.RemoveAt(lastIndex);
    }

    // Fixes inefficiency where a 45-char heading before a huge paragraph creates a tiny chunk:
    // PackSegments can flush builder=45 even though 45 < 0.5*minimum, paying a full LLM call for ~11 tokens.
    // MergeTinyTail only fixes the LAST chunk, so middle tinies survive. This pass merges ANY chunk
    // < 0.5*minimum with a neighbor, allowing up to target*1.35 (not just softLimit) for the merged size
    // so a tiny tail like 17-char "Final conclusion." still merges even when prev is 1800.
    private static void MergeUndersizedChunks(IList<string> chunks, int minimumUsefulChunk, int softLimit, int targetChars)
    {
        if (chunks.Count < 2)
            return;

        var threshold = (int)Math.Round(minimumUsefulChunk * 0.5);
        // Allow tiny merges to grow up to 35% over target (same as SplitIntoNaturalSegments:292) even if > softLimit
        var maxMerged = Math.Max(softLimit, (int)Math.Round(targetChars * 1.35));
        var i = 0;
        while (i < chunks.Count)
        {
            if (chunks[i].Length >= threshold || chunks.Count == 1)
            {
                i++;
                continue;
            }

            // Tiny chunk - prefer merging forward (keeps heading with following content), else backward
            if (i == 0 && chunks.Count > 1)
            {
                if (chunks[i].Length + 2 + chunks[i + 1].Length <= maxMerged)
                {
                    chunks[i + 1] = $"{chunks[i]}\n\n{chunks[i + 1]}".Trim();
                    chunks.RemoveAt(i);
                    continue;
                }
            }
            else if (i == chunks.Count - 1)
            {
                var prev = chunks[i - 1];
                if (prev.Length + 2 + chunks[i].Length <= maxMerged)
                {
                    chunks[i - 1] = $"{prev}\n\n{chunks[i]}".Trim();
                    chunks.RemoveAt(i);
                    i = Math.Max(0, i - 1);
                    continue;
                }
            }
            else
            {
                var next = chunks[i + 1];
                if (chunks[i].Length + 2 + next.Length <= maxMerged)
                {
                    chunks[i + 1] = $"{chunks[i]}\n\n{next}".Trim();
                    chunks.RemoveAt(i);
                    continue;
                }

                var prev = chunks[i - 1];
                if (prev.Length + 2 + chunks[i].Length <= maxMerged)
                {
                    chunks[i - 1] = $"{prev}\n\n{chunks[i]}".Trim();
                    chunks.RemoveAt(i);
                    i = Math.Max(0, i - 1);
                    continue;
                }
            }

            i++;
        }
    }

    private static int FindCharOffset(string fullText, string chunkText, int searchStart)
    {
        var offset = fullText.IndexOf(chunkText, searchStart, StringComparison.Ordinal);
        return offset >= 0 ? offset : searchStart;
    }

    private static string NormalizeText(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
    }
}
