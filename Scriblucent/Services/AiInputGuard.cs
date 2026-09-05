using Scriblucent.Localization;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

public sealed class AiInputRejectedException : InvalidOperationException
{
    public AiInputRejectedException(string message)
        : base(message)
    {
    }
}

/// <summary>Why IsSuitableStudyText rejected a note - lets EnsureSuitableStudyText give the user
/// a specific reason instead of one generic message regardless of which heuristic fired.</summary>
public enum AiInputRejectionReason
{
    None,
    TooShort,
    RepeatedOrLowVariety,
    LowTextRatio,
    LooksLikeGibberish
}

public static class AiInputGuard
{
    private const int MinimumLetterCount = 45;
    private const int MinimumMeaningfulWordCount = 8;
    private const int MinimumUniqueWordCount = 6;
    private const int ShortTextCharacterThreshold = 140;
    private const int ShortTextMinimumWordCount = 12;

    public const string RefusalOutput = "REFUSE: insufficient meaningful note content";

    private static readonly Regex WordRegex = new(@"[\p{L}][\p{L}\p{Mn}'’-]{1,}", RegexOptions.Compiled);
    private static readonly Regex RepeatedCharacterRegex = new(@"(\S)\1{7,}", RegexOptions.Compiled);
    private static readonly Regex RepeatedChunkRegex = new(@"(\S.{1,24}?\S)\s*(?:\1\s*){3,}", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex PlaceholderRegex = new(@"\[(?:picture|photo|image|nuotrauka|paveiksl(?:e|ė)lis)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SentenceEndingRegex = new(@"[.!?。！？]", RegexOptions.Compiled);
    private static readonly Regex BulletOrHeadingRegex = new(@"(?m)^\s*(?:[-*•]|\d+[\.)])\s+\S|:\s*\p{L}", RegexOptions.Compiled);

    private static readonly HashSet<string> LatinNoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "asdf",
        "asdfg",
        "qwer",
        "qwerty",
        "zxcv",
        "zxcvb",
        "hjkl",
        "lkjh",
        "qaz",
        "wsx",
        "qazwsx",
        "lorem",
        "ipsum"
    };

    public static void EnsureSuitableStudyText(string text)
    {
        var reason = IsSuitableStudyText(text);
        if (reason != AiInputRejectionReason.None)
            throw new AiInputRejectedException(GetRejectionMessage(reason));
    }

    private static string GetRejectionMessage(AiInputRejectionReason reason)
    {
        var key = reason switch
        {
            AiInputRejectionReason.TooShort => "AiInputRejectedTooShort",
            AiInputRejectionReason.RepeatedOrLowVariety => "AiInputRejectedRepeatedOrLowVariety",
            AiInputRejectionReason.LowTextRatio => "AiInputRejectedLowTextRatio",
            AiInputRejectionReason.LooksLikeGibberish => "AiInputRejectedLooksLikeGibberish",
            _ => "AiInputRejectedInsufficientContent"
        };

        var localized = LocalizationService.GetString(key);
        return string.IsNullOrWhiteSpace(localized) || string.Equals(localized, key, StringComparison.Ordinal)
            ? LocalizationService.GetString("AiInputRejectedInsufficientContent")
            : localized;
    }

    public static bool IsRefusalOutput(string? output)
    {
        return ExplainRefusalOutput(output) != RefusalReasonNone;
    }

    // TEMPORARY trace support: same classification rules as IsRefusalOutput, but reports WHICH
    // rule matched so generation logs can show why an output was treated as a refusal. Remove
    // alongside AiGenerationTracer once the refusal investigation is done.
    internal const string RefusalReasonNone = "none";

    internal static string ExplainRefusalOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return RefusalReasonNone;

        var trimmed = output.TrimStart();

        // Primary: exact prefix match (grammar-constrained output)
        if (trimmed.StartsWith(RefusalOutput, StringComparison.OrdinalIgnoreCase))
            return "exact-prefix";

        // Very short outputs are likely refusals or garbage
        if (trimmed.Length < 20)
            return "under-20-chars";

        // Semantic: common natural-language refusal patterns
        var lower = trimmed.ToLowerInvariant();
        if (lower.StartsWith("i cannot ") || lower.StartsWith("i can't "))
            return "i-cannot-prefix";
        if (lower.Contains("insufficient") && lower.Contains("content"))
            return "insufficient-content";
        if (lower.Contains("not enough") && (lower.Contains("information") || lower.Contains("content") || lower.Contains("meaningful")))
            return "not-enough-info";
        if (lower.StartsWith("refuse") || lower.StartsWith("refusal"))
            return "refuse-prefix";

        return RefusalReasonNone;
    }

    public static AiInputRejectionReason IsSuitableStudyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return AiInputRejectionReason.TooShort;

        var textWithoutPlaceholders = PlaceholderRegex.Replace(text, " ");
        var normalized = Regex.Replace(textWithoutPlaceholders, @"\s+", " ").Trim();
        if (normalized.Length < 30)
            return AiInputRejectionReason.TooShort;

        if (RepeatedCharacterRegex.IsMatch(normalized))
            return AiInputRejectionReason.RepeatedOrLowVariety;

        if (RepeatedChunkRegex.IsMatch(normalized))
            return AiInputRejectionReason.RepeatedOrLowVariety;

        var visibleChars = normalized.Count(c => !char.IsWhiteSpace(c));
        if (visibleChars == 0)
            return AiInputRejectionReason.TooShort;

        var letterCount = normalized.Count(char.IsLetter);
        if (letterCount < MinimumLetterCount)
            return AiInputRejectionReason.TooShort;

        var letterRatio = (double)letterCount / visibleChars;
        if (letterRatio < 0.35)
            return AiInputRejectionReason.LowTextRatio;

        if (IsMostlyUnsegmentedScript(normalized, letterCount))
            return CheckUnsegmentedText(text, normalized, letterCount);

        var words = WordRegex.Matches(normalized)
            .Select(match => match.Value.Trim().ToLowerInvariant())
            .Where(word => word.Length >= 2)
            .ToList();

        if (words.Count < MinimumMeaningfulWordCount)
            return AiInputRejectionReason.TooShort;

        if (normalized.Length < ShortTextCharacterThreshold
            && words.Count < ShortTextMinimumWordCount
            && !HasStudyStructure(text))
        {
            return AiInputRejectionReason.TooShort;
        }

        var uniqueWords = words
            .Where(word => word.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (uniqueWords.Count < MinimumUniqueWordCount)
            return AiInputRejectionReason.RepeatedOrLowVariety;

        if (HasRepeatedWordRun(words) || HasRepeatedWordNgrams(words))
            return AiInputRejectionReason.RepeatedOrLowVariety;

        var mostCommonWordShare = words
            .GroupBy(word => word, StringComparer.OrdinalIgnoreCase)
            .Max(group => (double)group.Count() / words.Count);

        if (mostCommonWordShare > 0.40)
            return AiInputRejectionReason.RepeatedOrLowVariety;

        var uniqueWordShare = (double)uniqueWords.Count / words.Count;
        if (words.Count < 80)
        {
            if (uniqueWordShare < 0.30)
                return AiInputRejectionReason.RepeatedOrLowVariety;
        }
        else if (uniqueWordShare < 0.22)
        {
            return AiInputRejectionReason.RepeatedOrLowVariety;
        }

        var averageWordLength = words.Average(word => word.Length);
        if (averageWordLength > 18)
            return AiInputRejectionReason.LooksLikeGibberish;

        if (letterCount >= 40 && IsMostlyLatinText(normalized, letterCount))
        {
            if (!HasStudyStructure(text) && words.Count < 18 && normalized.Length < 220)
                return AiInputRejectionReason.TooShort;

            var vowelCount = normalized.Count(IsLatinVowel);
            var vowelRatio = (double)vowelCount / letterCount;
            if (vowelRatio < 0.16)
                return AiInputRejectionReason.LooksLikeGibberish;

            if (LooksLikeLatinGibberish(words))
                return AiInputRejectionReason.LooksLikeGibberish;
        }

        return AiInputRejectionReason.None;
    }

    private static bool HasStudyStructure(string text)
    {
        return SentenceEndingRegex.IsMatch(text) || BulletOrHeadingRegex.IsMatch(text);
    }

    private static AiInputRejectionReason CheckUnsegmentedText(string originalText, string normalized, int letterCount)
    {
        if (letterCount < Math.Max(MinimumLetterCount, 60))
            return AiInputRejectionReason.TooShort;

        if (normalized.Length < ShortTextCharacterThreshold && !HasStudyStructure(originalText))
            return AiInputRejectionReason.TooShort;

        return RepeatedChunkRegex.IsMatch(normalized)
            ? AiInputRejectionReason.RepeatedOrLowVariety
            : AiInputRejectionReason.None;
    }

    private static bool HasRepeatedWordRun(IReadOnlyList<string> words)
    {
        var runLength = 1;

        for (var i = 1; i < words.Count; i++)
        {
            if (string.Equals(words[i], words[i - 1], StringComparison.OrdinalIgnoreCase))
            {
                runLength++;
                if (runLength >= 4)
                    return true;

                continue;
            }

            runLength = 1;
        }

        return false;
    }

    private static bool HasRepeatedWordNgrams(IReadOnlyList<string> words)
    {
        for (var size = 2; size <= 4; size++)
        {
            if (words.Count < size * 4)
                continue;

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var total = words.Count - size + 1;

            for (var i = 0; i <= words.Count - size; i++)
            {
                var key = string.Join('', words.Skip(i).Take(size));
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            var maxShare = counts.Values.Max() / (double)total;
            if (counts.Values.Max() >= 4 && maxShare > 0.25)
                return true;
        }

        return false;
    }

    private static bool LooksLikeLatinGibberish(IReadOnlyList<string> words)
    {
        var latinWords = words
            .Where(word => word.Any(char.IsLetter) && word.All(character => !char.IsLetter(character) || IsLatinLetter(character)))
            .ToList();

        if (latinWords.Count < Math.Min(5, words.Count))
            return false;

        var knownNoiseShare = latinWords.Count(word => LatinNoiseWords.Contains(StripNonLetters(word))) / (double)latinWords.Count;
        if (knownNoiseShare >= 0.25)
            return true;

        var analyzable = latinWords
            .Select(StripNonLetters)
            .Where(word => word.Length >= 4)
            .ToList();

        if (analyzable.Count >= 5)
        {
            var noVowelShare = analyzable.Count(word => !word.Any(IsLatinVowel)) / (double)analyzable.Count;
            if (noVowelShare >= 0.35)
                return true;

            var lowVowelShare = analyzable.Count(word => word.Count(IsLatinVowel) / (double)word.Length <= 0.25) / (double)analyzable.Count;
            if (lowVowelShare >= 0.50)
                return true;

            var suspiciousShare = analyzable.Count(IsSuspiciousLatinWord) / (double)analyzable.Count;
            if (suspiciousShare >= 0.55)
                return true;
        }

        var shortTokenShare = words.Count(word => word.Length <= 2) / (double)words.Count;
        return words.Count >= 10 && shortTokenShare > 0.55;
    }

    private static bool IsSuspiciousLatinWord(string word)
    {
        if (word.Length < 4)
            return false;

        if (LatinNoiseWords.Contains(word))
            return true;

        var vowelCount = word.Count(IsLatinVowel);
        var vowelRatio = (double)vowelCount / word.Length;

        if (IsSingleKeyboardRowWord(word) && vowelRatio < 0.30)
            return true;

        if (word.Length >= 6 && vowelRatio < 0.20)
            return true;

        return HasLongLatinConsonantRun(word);
    }

    private static string StripNonLetters(string word)
    {
        return new string(word.Where(IsLatinLetter).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool IsSingleKeyboardRowWord(string word)
    {
        if (word.Length < 4)
            return false;

        string[] rows = ["qwertyuiop", "asdfghjkl", "zxcvbnm"];
        return rows.Any(row => word.All(row.Contains));
    }

    private static bool HasLongLatinConsonantRun(string word)
    {
        var runLength = 0;

        foreach (var character in word)
        {
            if (IsLatinLetter(character) && !IsLatinVowel(character))
            {
                runLength++;
                if (runLength >= 5)
                    return true;

                continue;
            }

            runLength = 0;
        }

        return false;
    }

    private static bool IsMostlyUnsegmentedScript(string text, int letterCount)
    {
        var cjkLetters = text.Count(character => char.IsLetter(character) && IsCjkLetter(character));
        return letterCount > 0 && (double)cjkLetters / letterCount >= 0.6;
    }

    private static bool IsMostlyLatinText(string text, int letterCount)
    {
        var latinLetters = text.Count(character => char.IsLetter(character) && IsLatinLetter(character));
        return (double)latinLetters / letterCount >= 0.8;
    }

    private static bool IsLatinLetter(char value)
    {
        return value is >= 'A' and <= 'z'
            or >= 'À' and <= 'ɏ'
            or >= 'Ḁ' and <= 'ỿ';
    }

    private static bool IsCjkLetter(char value)
    {
        return value is >= '぀' and <= 'ヿ'
            or >= '㐀' and <= '䶿'
            or >= '一' and <= '鿿'
            or >= '가' and <= '힯';
    }

    private static bool IsLatinVowel(char value)
    {
        return "aeiouyąęėįųūAEIOUYĄĘĖĮŲŪáéíóúàèìòùäëïöüÁÉÍÓÚÀÈÌÒÙÄËÏÖÜ".Contains(value);
    }
}
