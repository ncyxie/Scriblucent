using Scriblucent.Localization;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

public sealed class AiInputRejectedException : InvalidOperationException
{
    public AiInputRejectedException(string message)
        : base(message)
    {
    }
}

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
    private static readonly Regex RepeatedCharacterRegex = new(
        @"(\S)\1{7,}",
        RegexOptions.Compiled);
    private static readonly Regex RepeatedChunkRegex = new(
        @"(\S.{1,24}?\S)\s*(?:\1\s*){3,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex LexicalTokenRegex = new(
        @"[\p{L}\p{Mn}\p{N}]+(?:['’\-][\p{L}\p{Mn}\p{N}]+)*",
        RegexOptions.Compiled);

    public static void EnsureSuitableStudyText(string text)
    {
        var reason = IsSuitableStudyText(text);
        if (reason != AiInputRejectionReason.None)
            throw new AiInputRejectedException(GetRejectionMessage(reason));
    }

    public static AiInputRejectionReason IsSuitableStudyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return AiInputRejectionReason.TooShort;

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length < 30
            || RepeatedCharacterRegex.IsMatch(normalized))
        {
            return normalized.Length < 30
                ? AiInputRejectionReason.TooShort
                : AiInputRejectionReason.RepeatedOrLowVariety;
        }

        if (RepeatedChunkRegex.IsMatch(normalized))
            return AiInputRejectionReason.RepeatedOrLowVariety;

        var visible = normalized.EnumerateRunes()
            .Where(rune => !Rune.IsWhiteSpace(rune))
            .ToList();
        var letters = visible.Where(Rune.IsLetter).ToList();
        if (letters.Count < MinimumLetterCount)
            return AiInputRejectionReason.TooShort;

        if (letters.Count / (double)visible.Count < 0.35)
            return AiInputRejectionReason.LowTextRatio;

        var tokens = LexicalTokenRegex.Matches(normalized)
            .Cast<Match>()
            .Select(match => match.Value.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            .ToList();
        if ((tokens.Count >= 20 && tokens.Distinct(StringComparer.Ordinal).Count() / (double)tokens.Count < 0.15)
            || HasRepeatedTokenNgrams(tokens))
            return AiInputRejectionReason.RepeatedOrLowVariety;

        var normalizedLetters = letters
            .Select(rune => Rune.ToLowerInvariant(rune).Value)
            .ToList();
        if (normalizedLetters.Distinct().Count() < 6)
            return AiInputRejectionReason.RepeatedOrLowVariety;

        var mostCommonShare = normalizedLetters
            .GroupBy(value => value)
            .Max(group => group.Count()) / (double)normalizedLetters.Count;
        if (mostCommonShare > 0.45)
            return AiInputRejectionReason.RepeatedOrLowVariety;

        return AiInputRejectionReason.None;
    }

    private static bool HasRepeatedTokenNgrams(IReadOnlyList<string> tokens)
    {
        for (var size = 2; size <= 6; size++)
        {
            if (tokens.Count < size * 4)
                continue;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index <= tokens.Count - size; index++)
            {
                var key = string.Join('\u001f', tokens.Skip(index).Take(size));
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            var total = tokens.Count - size + 1;
            if (counts.Values.Any(count => count >= 4 && count / (double)total > 0.25))
                return true;
        }
        return false;
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
}
