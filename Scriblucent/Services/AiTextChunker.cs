using System.Text;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal enum AiChunkingPurpose
{
    Flashcards,
    MindMap,
    Quiz
}

internal enum AiChunkingModelProfile
{
    Small,
    Medium,
    Large
}

internal static class AiTextChunker
{
    public static IReadOnlyList<string> Split(
        string text,
        AiChunkingPurpose purpose,
        AiChunkingModelProfile modelProfile = AiChunkingModelProfile.Large)
    {
        var normalized = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        var targetCharacters = CalculateTargetCharacters(normalized, purpose, modelProfile);
        var softLimit = (int)Math.Round(targetCharacters * 1.18);
        var minimumUsefulChunk = CalculateMinimumUsefulChunk(targetCharacters, purpose, modelProfile);

        var segments = SplitIntoNaturalSegments(normalized, targetCharacters);
        if (segments.Count == 0)
            return [normalized.Trim()];

        var chunks = PackSegments(segments, targetCharacters, softLimit, minimumUsefulChunk);
        MergeTinyTail(chunks, minimumUsefulChunk, softLimit);

        return chunks
            .Select(chunk => chunk.Trim())
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk))
            .ToList();
    }

    private static int CalculateTargetCharacters(
        string text,
        AiChunkingPurpose purpose,
        AiChunkingModelProfile modelProfile)
    {
        var textLength = text.Length;
        var paragraphCount = CountParagraphs(text);
        var averageParagraphLength = paragraphCount == 0 ? textLength : textLength / Math.Max(1, paragraphCount);
        var modelScale = GetModelChunkScale(modelProfile);

        var minimum = purpose switch
        {
            AiChunkingPurpose.Flashcards => 900,
            AiChunkingPurpose.Quiz => 900,
            _ => 1400
        };
        minimum = (int)Math.Round(minimum * modelScale);

        var maximum = purpose switch
        {
            AiChunkingPurpose.Flashcards => 2800,
            AiChunkingPurpose.Quiz => 2800,
            _ => 3600
        };
        maximum = (int)Math.Round(maximum * modelScale);

        var singlePassLimit = purpose switch
        {
            AiChunkingPurpose.Flashcards => 1300,
            AiChunkingPurpose.Quiz => 1300,
            _ => 1900
        };
        singlePassLimit = (int)Math.Round(singlePassLimit * modelScale);

        if (textLength <= singlePassLimit)
            return Math.Max(textLength, minimum);

        var scale = purpose switch
        {
            AiChunkingPurpose.Flashcards => 17.5,
            AiChunkingPurpose.Quiz => 17.5,
            _ => 20.0
        } * modelScale;
        var target = minimum + (int)Math.Round(Math.Sqrt(textLength) * scale);

        if (averageParagraphLength < 180 && paragraphCount >= 4)
            target = (int)Math.Round(target * 0.9);
        else if (averageParagraphLength > 900)
            target = (int)Math.Round(target * 1.1);

        return Math.Clamp(target, minimum, maximum);
    }

    private static int CalculateMinimumUsefulChunk(
        int targetCharacters,
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
        return Math.Max(floor, (int)Math.Round(targetCharacters * share));
    }

    private static double GetModelChunkScale(AiChunkingModelProfile modelProfile)
    {
        return modelProfile switch
        {
            AiChunkingModelProfile.Small => 0.60,
            AiChunkingModelProfile.Medium => 0.80,
            _ => 1.0
        };
    }

    private static List<string> SplitIntoNaturalSegments(string text, int targetCharacters)
    {
        var segments = new List<string>();
        var paragraphs = text.Split('\n', StringSplitOptions.None);
        var paragraphBuilder = new StringBuilder();

        foreach (var rawParagraph in paragraphs)
        {
            var paragraph = rawParagraph.Trim();
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                FlushParagraph(paragraphBuilder, segments, targetCharacters);
                continue;
            }

            if (paragraphBuilder.Length > 0)
                paragraphBuilder.AppendLine();
            paragraphBuilder.Append(paragraph);

            if (paragraphBuilder.Length >= targetCharacters)
                FlushParagraph(paragraphBuilder, segments, targetCharacters);
        }

        FlushParagraph(paragraphBuilder, segments, targetCharacters);
        return segments;
    }

    private static void FlushParagraph(StringBuilder builder, ICollection<string> segments, int targetCharacters)
    {
        if (builder.Length == 0)
            return;

        var paragraph = builder.ToString().Trim();
        builder.Clear();

        if (paragraph.Length <= targetCharacters * 1.35)
        {
            segments.Add(paragraph);
            return;
        }

        foreach (var sentenceGroup in SplitLongParagraph(paragraph, targetCharacters))
            segments.Add(sentenceGroup);
    }

    private static IEnumerable<string> SplitLongParagraph(string paragraph, int targetCharacters)
    {
        var sentences = Regex
            .Split(paragraph, @"(?<=[.!?])\s+")
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.Length > 0)
            .ToList();

        if (sentences.Count <= 1)
            return SplitByWords(paragraph, targetCharacters);

        var chunks = new List<string>();
        var builder = new StringBuilder();
        var softLimit = (int)Math.Round(targetCharacters * 1.12);

        foreach (var sentence in sentences)
        {
            if (builder.Length > 0 && builder.Length + sentence.Length + 1 > softLimit)
            {
                chunks.Add(builder.ToString().Trim());
                builder.Clear();
            }

            if (sentence.Length > softLimit)
            {
                if (builder.Length > 0)
                {
                    chunks.Add(builder.ToString().Trim());
                    builder.Clear();
                }

                chunks.AddRange(SplitByWords(sentence, targetCharacters));
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

    private static IEnumerable<string> SplitByWords(string text, int targetCharacters)
    {
        var chunks = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var builder = new StringBuilder();

        foreach (var word in words)
        {
            if (builder.Length > 0 && builder.Length + word.Length + 1 > targetCharacters)
            {
                chunks.Add(builder.ToString().Trim());
                builder.Clear();
            }

            if (word.Length > targetCharacters)
            {
                if (builder.Length > 0)
                {
                    chunks.Add(builder.ToString().Trim());
                    builder.Clear();
                }

                for (var i = 0; i < word.Length; i += targetCharacters)
                    chunks.Add(word.Substring(i, Math.Min(targetCharacters, word.Length - i)));
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
        int targetCharacters,
        int softLimit,
        int minimumUsefulChunk)
    {
        var chunks = new List<string>();
        var builder = new StringBuilder();

        foreach (var segment in segments.Where(segment => !string.IsNullOrWhiteSpace(segment)))
        {
            var separatorLength = builder.Length == 0 ? 0 : 2;
            var combinedLength = builder.Length + separatorLength + segment.Length;

            if (builder.Length > 0
                && combinedLength > targetCharacters
                && (builder.Length >= minimumUsefulChunk || combinedLength > softLimit))
            {
                chunks.Add(builder.ToString().Trim());
                builder.Clear();
            }

            if (builder.Length > 0)
                builder.AppendLine().AppendLine();
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

    private static int CountParagraphs(string text)
    {
        return text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => line.Length > 0);
    }

    private static string NormalizeText(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Trim();
    }
}
