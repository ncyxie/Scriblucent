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
        if (!IsSuitableStudyText(text))
            throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));
    }

    public static bool IsRefusalOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        return output.TrimStart().StartsWith(RefusalOutput, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSuitableStudyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var textWithoutPlaceholders = PlaceholderRegex.Replace(text, " ");
        var normalized = Regex.Replace(textWithoutPlaceholders, @"\s+", " ").Trim();
        if (normalized.Length < 30)
            return false;

        if (RepeatedCharacterRegex.IsMatch(normalized))
            return false;

        if (RepeatedChunkRegex.IsMatch(normalized))
            return false;

        var visibleChars = normalized.Count(c => !char.IsWhiteSpace(c));
        if (visibleChars == 0)
            return false;

        var letterCount = normalized.Count(char.IsLetter);
        if (letterCount < MinimumLetterCount)
            return false;

        var letterRatio = (double)letterCount / visibleChars;
        if (letterRatio < 0.35)
            return false;

        if (IsMostlyUnsegmentedScript(normalized, letterCount))
            return HasEnoughUnsegmentedText(text, normalized, letterCount);

        var words = WordRegex.Matches(normalized)
            .Select(match => match.Value.Trim().ToLowerInvariant())
            .Where(word => word.Length >= 2)
            .ToList();

        if (words.Count < MinimumMeaningfulWordCount)
            return false;

        if (normalized.Length < ShortTextCharacterThreshold
            && words.Count < ShortTextMinimumWordCount
            && !HasStudyStructure(text))
        {
            return false;
        }

        var uniqueWords = words
            .Where(word => word.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (uniqueWords.Count < MinimumUniqueWordCount)
            return false;

        if (HasRepeatedWordRun(words) || HasRepeatedWordNgrams(words))
            return false;

        var mostCommonWordShare = words
            .GroupBy(word => word, StringComparer.OrdinalIgnoreCase)
            .Max(group => (double)group.Count() / words.Count);

        if (mostCommonWordShare > 0.40)
            return false;

        var uniqueWordShare = (double)uniqueWords.Count / words.Count;
        if (words.Count < 80)
        {
            if (uniqueWordShare < 0.30)
                return false;
        }
        else if (uniqueWordShare < 0.22)
        {
            return false;
        }

        var averageWordLength = words.Average(word => word.Length);
        if (averageWordLength > 18)
            return false;

        if (letterCount >= 40 && IsMostlyLatinText(normalized, letterCount))
        {
            if (!HasStudyStructure(text) && words.Count < 18 && normalized.Length < 220)
                return false;

            var vowelCount = normalized.Count(IsLatinVowel);
            var vowelRatio = (double)vowelCount / letterCount;
            if (vowelRatio < 0.16)
                return false;

            if (LooksLikeLatinGibberish(words))
                return false;
        }

        return true;
    }

    private static bool HasStudyStructure(string text)
    {
        return SentenceEndingRegex.IsMatch(text) || BulletOrHeadingRegex.IsMatch(text);
    }

    private static bool HasEnoughUnsegmentedText(string originalText, string normalized, int letterCount)
    {
        if (letterCount < Math.Max(MinimumLetterCount, 60))
            return false;

        if (normalized.Length < ShortTextCharacterThreshold && !HasStudyStructure(originalText))
            return false;

        return !RepeatedChunkRegex.IsMatch(normalized);
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
                var key = string.Join('\u001F', words.Skip(i).Take(size));
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
        return value is >= '\u0041' and <= '\u007A'
            or >= '\u00C0' and <= '\u024F'
            or >= '\u1E00' and <= '\u1EFF';
    }

    private static bool IsCjkLetter(char value)
    {
        return value is >= '\u3040' and <= '\u30FF'
            or >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uAC00' and <= '\uD7AF';
    }

    private static bool IsLatinVowel(char value)
    {
        return "aeiouyąęėįųūAEIOUYĄĘĖĮŲŪáéíóúàèìòùäëïöüÁÉÍÓÚÀÈÌÒÙÄËÏÖÜ".Contains(value);
    }
}
