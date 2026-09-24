using System.Text.RegularExpressions;

namespace Scriblucent.Services.AI;

internal static class StudyAutoCoverage
{
    private const int MinimumRichTokenCount = 12;
    private const int MinimumSplitSideTokens = 4;
    private const int MaximumExtraAttempts = 12;

    private static readonly Regex LexicalTokenRegex = new(
        @"[\p{L}\p{Mn}\p{N}]+(?:['’\-][\p{L}\p{Mn}\p{N}]+)*",
        RegexOptions.Compiled);
    private static readonly Regex LeadingListRegex = new(
        @"^\s*(?:[-•*]|\d+\.)\s+",
        RegexOptions.Compiled);

    public static IReadOnlyDictionary<string, int> ResolveAttempts(
        IReadOnlyList<StudySourceUnitSnapshot> units,
        StudyCoverageMode coverageMode,
        string modelKey)
    {
        coverageMode = StudyPlanCoordinator.NormalizeCoverage(coverageMode);
        var clozeOnly = AiModelCapabilities.Resolve(modelKey).NaturalQuestionTier
            == NaturalQuestionPolicyTier.ClozeOnly;
        var caps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (units.Count == 0)
            return caps;

        foreach (var unit in units)
        {
            if (!GroundedClozeBuilder.IsViableSourceUnit(unit.Text))
            {
                caps[unit.Id] = 0;
                continue;
            }

            caps[unit.Id] = 1;
        }

        if (clozeOnly
            || coverageMode is not (StudyCoverageMode.Auto or StudyCoverageMode.Thorough))
        {
            return caps;
        }

        var viable = units.Where(unit => caps.GetValueOrDefault(unit.Id) > 0).ToList();
        var ranked = viable
            .Where(unit => IsRich(unit.Text))
            .OrderBy(RankExtra)
            .ThenByDescending(unit => unit.Text.Length)
            .ThenBy(unit => unit.Order)
            .ToList();
        var extras = coverageMode == StudyCoverageMode.Thorough
            ? ranked.Count
            : ExtraAttemptBudget(viable.Count, ranked.Count);
        foreach (var unit in ranked.Take(extras))
            caps[unit.Id] = 2;

        return caps;
    }

    public static int TotalAttempts(
        IReadOnlyList<StudySourceUnitSnapshot> units,
        StudyCoverageMode coverageMode,
        string modelKey)
        => ResolveAttempts(units, coverageMode, modelKey).Values.Sum();

    public static int ExtraAttemptBudget(int viableCount, int richCount)
    {
        if (viableCount <= 0 || richCount <= 0)
            return 0;
        return Math.Min(richCount, Math.Min(Math.Max(3, viableCount / 3), MaximumExtraAttempts));
    }

    public static bool IsRich(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var tokens = LexicalTokenRegex.Matches(text).Cast<Match>().ToList();
        var atomCount = CountAtoms(tokens);
        if (atomCount < MinimumRichTokenCount)
            return false;

        var hooks = 0;
        if (tokens.Any(token => token.Value.Any(char.IsDigit)))
            hooks++;
        if (HasInternalCapital(tokens))
            hooks++;
        if (IsList(text))
            hooks++;
        if (HasSplit(text, tokens))
            hooks++;
        return hooks >= 2 || atomCount >= 20;
    }

    public static IReadOnlyList<string> ExtraAttemptUnitIds(IReadOnlyDictionary<string, int> attemptCaps)
        => attemptCaps
            .Where(pair => pair.Value >= 2)
            .Select(pair => pair.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int RankExtra(StudySourceUnitSnapshot unit)
    {
        if (IsList(unit.Text))
            return 0;
        var tokens = LexicalTokenRegex.Matches(unit.Text).Cast<Match>().ToList();
        var hasNumber = tokens.Any(token => token.Value.Any(char.IsDigit));
        var hasName = HasInternalCapital(tokens);
        return hasNumber && hasName ? 1 : 2;
    }

    private static bool HasInternalCapital(IReadOnlyList<Match> tokens)
        => tokens.Skip(1).Any(token => token.Value.Length > 0 && char.IsUpper(token.Value[0]));

    private static bool IsList(string text)
        => LeadingListRegex.IsMatch(text) || StudyAnswerList.IsExplicitList(text);

    private static bool HasSplit(string text, IReadOnlyList<Match> tokens)
    {
        var semicolon = text.IndexOf(';');
        if (semicolon >= 0 && BothSidesHaveTokens(tokens, semicolon))
            return true;

        var colon = text.IndexOf(':');
        if (colon >= 0
            && colon + 1 < text.Length
            && char.IsWhiteSpace(text[colon + 1])
            && BothSidesHaveTokens(tokens, colon))
        {
            return true;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is ',' or '，' or '、' or '—' or '–' or '：' or '؛'
                && BothSidesHaveTokens(tokens, index))
            {
                return true;
            }
        }

        return false;
    }

    private static bool BothSidesHaveTokens(IReadOnlyList<Match> tokens, int splitIndex)
    {
        var left = tokens.Count(token => token.Index + token.Length <= splitIndex);
        var right = tokens.Count(token => token.Index >= splitIndex);
        return left >= MinimumSplitSideTokens && right >= MinimumSplitSideTokens;
    }

    private static int CountAtoms(IEnumerable<Match> tokens)
        => tokens.Sum(token => token.Length <= 24
            ? 1
            : Math.Max(1, (System.Globalization.StringInfo.ParseCombiningCharacters(token.Value).Length + 3) / 4));
}
