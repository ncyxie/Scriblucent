using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace Scriblucent.Services;

/// <summary>
/// The actual spell-checking logic, kept separate from SpellCheckDictionaryService's file/download
/// management. Matches Word/LibreOffice's single-active-language model - one dictionary is checked
/// against at a time, not several at once.
/// </summary>
public static partial class SpellCheckService
{
    public readonly record struct WordSpan(int Start, int Length);

    // URLs/emails are excluded before tokenization - otherwise fragments like "https"/"com" from
    // inside a URL would get flagged individually, which real editors avoid too.
    [GeneratedRegex(@"\b(?:https?://\S+|www\.\S+|[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})\b")]
    private static partial Regex UrlOrEmailPattern();

    // A "word" is a run of Unicode letters that may contain internal apostrophes/hyphens (don't,
    // well-known) but must start and end on a letter - covers every language in the curated
    // dictionary catalog (all Latin/Cyrillic alphabets).
    [GeneratedRegex(@"[\p{L}](?:[\p{L}'’-]*[\p{L}])?")]
    private static partial Regex WordPattern();

    internal static bool IsDictionaryWord(string word)
    {
        var match = WordPattern().Match(word);
        return match.Success && match.Index == 0 && match.Length == word.Length;
    }

    internal static WordSpan? WordAtOffset(string text, int offset)
    {
        if (offset < 0 || offset > text.Length) return null;
        foreach (Match match in WordPattern().Matches(text))
        {
            if (match.Index > offset) break;
            if (offset <= match.Index + match.Length) return new WordSpan(match.Index, match.Length);
        }
        return null;
    }

    public static IReadOnlyList<WordSpan> FindMisspelledWordSpans(
        string text,
        WordList? activeDictionary,
        IReadOnlySet<string>? customWords = null)
    {
        if (string.IsNullOrEmpty(text) || activeDictionary is null)
            return [];

        var excludedRanges = UrlOrEmailPattern()
            .Matches(text)
            .Select(match => (match.Index, End: match.Index + match.Length))
            .ToList();

        var misspelled = new List<WordSpan>();

        foreach (Match match in WordPattern().Matches(text))
        {
            if (excludedRanges.Any(range => match.Index >= range.Index && match.Index < range.End))
                continue;

            var word = match.Value;
            if (customWords is not null && customWords.Contains(word))
                continue;

            if (!activeDictionary.Check(word))
                misspelled.Add(new WordSpan(match.Index, match.Length));
        }

        return misspelled;
    }

    /// <summary>Suggestions are noticeably slower than Check (Hunspell's own benchmarks put it at
    /// roughly 1000x per-word) - only ever called on-demand for the single word under a right-
    /// click, never during the bulk document scan.</summary>
    public static IReadOnlyList<string> GetSuggestions(string word, WordList? activeDictionary, int maxSuggestions = 8)
    {
        if (activeDictionary is null)
            return [];

        return activeDictionary.Suggest(word)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxSuggestions)
            .ToList();
    }
}
