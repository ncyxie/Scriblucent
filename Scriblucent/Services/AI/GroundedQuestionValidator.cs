using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Scriblucent.Services.AI;

internal static class GroundedQuestionValidator
{
    private static readonly Regex NumberRegex = new(
        @"\p{N}+(?:[.,]\p{N}+)*",
        RegexOptions.Compiled);
    private static readonly Regex LexicalTokenRegex = new(
        @"[\p{L}\p{N}]+",
        RegexOptions.Compiled);
    private static readonly Regex SentencePreambleRegex = new(
        @"\.[ \t]+[\p{Lu}]",
        RegexOptions.Compiled);

    private static readonly string[] PromptControlFragments =
    [
        "assignments:",
        "locked answer:",
        "draft question:",
        "owned s",
        "reference s",
        "focus 1",
        "focus 2",
        "do not reverse",
        "missing piece",
        "grammatical recall",
        "in the unit's language",
        "stitch source",
        "without syntax",
        "these instructions",
        "do not quote these instructions",
        "shortest continuous text span"
    ];

    public static bool TryValidate(
        GroundedQuestionSeed seed,
        string question,
        out GroundedRejectionReason reason,
        NaturalQuestionPolicyTier policyTier = NaturalQuestionPolicyTier.Contextual,
        string? passage = null)
    {
        _ = policyTier;
        return TryValidateHard(seed.Fact, question, passage ?? seed.Fact.QuestionContext, out reason);
    }

    public static bool TryValidateHard(
        StudyFact fact,
        string question,
        string? packetContext,
        out GroundedRejectionReason reason)
    {
        question = question.Trim();
        if (!TryValidateQuestionShape(
                question,
                fact.Evidence,
                packetContext ?? fact.QuestionContext,
                out reason))
            return false;

        var normalizedAnswer = NormalizeForEcho(fact.Answer);
        if (normalizedAnswer.Length >= 2
            && ContainsWholePhrase(question, fact.Answer))
        {
            reason = GroundedRejectionReason.AnswerLeakage;
            return false;
        }

        var normalizedQuestionEcho = NormalizeForEcho(question);
        var normalizedSourceEcho = NormalizeForEcho(fact.Evidence);
        if (normalizedQuestionEcho.Length > 0
            && normalizedSourceEcho.Contains(normalizedQuestionEcho, StringComparison.Ordinal))
        {
            reason = GroundedRejectionReason.SourceEcho;
            return false;
        }

        if (!HasQuestionFramingNovelty(question, packetContext ?? fact.QuestionContext ?? fact.Evidence))
        {
            reason = GroundedRejectionReason.SourceEcho;
            return false;
        }

        reason = default;
        return true;
    }

    public static bool TryValidateQuestionShape(
        string question,
        string? unitText,
        out GroundedRejectionReason reason)
        => TryValidateQuestionShape(question, unitText, unitText, out reason);

    public static bool TryValidateQuestionShape(
        string question,
        string? unitText,
        string? packetContext,
        out GroundedRejectionReason reason)
    {
        question = question.Trim();
        if (question.Length is < 6 or > 180
            || question[^1] is not ('?' or '？' or '؟'))
        {
            reason = GroundedRejectionReason.TooShort;
            return false;
        }

        if (question.IndexOfAny(['_', '[', ']', '<', '>']) >= 0
            || question.Any(character => char.IsControl(character) && character is not '\t'))
        {
            reason = GroundedRejectionReason.Placeholder;
            return false;
        }

        var normalized = Normalize(question);
        if (PromptControlFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal)))
        {
            reason = GroundedRejectionReason.UngroundedQuestion;
            return false;
        }

        if (SentencePreambleRegex.IsMatch(question))
        {
            reason = GroundedRejectionReason.UngroundedQuestion;
            return false;
        }

        var sourceNumbers = NumberRegex.Matches(unitText ?? string.Empty)
            .Select(match => NormalizeDigits(match.Value))
            .ToHashSet(StringComparer.Ordinal);
        if (NumberRegex.Matches(question)
            .Select(match => NormalizeDigits(match.Value))
            .Any(number => !sourceNumbers.Contains(number)))
        {
            reason = GroundedRejectionReason.InvalidEvidence;
            return false;
        }

        if (!AreDistinctiveNamesGrounded(question, packetContext ?? unitText))
        {
            reason = GroundedRejectionReason.InvalidEvidence;
            return false;
        }

        reason = default;
        return true;
    }

    public static bool CanGenerateNaturalQuestion(
        StudyFact fact,
        string modelKey,
        NaturalQuestionPolicyTier? policyOverride = null)
    {
        var policy = policyOverride ?? AiModelCapabilities.Resolve(modelKey).NaturalQuestionTier;
        return policy != NaturalQuestionPolicyTier.ClozeOnly && !fact.IsCoverageFallback;
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static string NormalizeForEcho(string value)
    {
        var normalized = Normalize(value);
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}\s]", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    internal static bool ContainsWholePhrase(string text, string phrase)
    {
        var textKey = NormalizeForEcho(text);
        var phraseKey = NormalizeForEcho(phrase);
        if (phraseKey.Length == 0)
            return false;

        // Scripts without lexical spaces commonly form one regex token for a whole sentence.
        // Retain exact-substring leakage detection there, while requiring token boundaries for
        // spaced/alphabetic text so a short answer such as "he" does not match inside "the".
        if (phraseKey.Any(character => char.GetUnicodeCategory(character) == UnicodeCategory.OtherLetter))
            return textKey.Contains(phraseKey, StringComparison.Ordinal);

        return $" {textKey} ".Contains($" {phraseKey} ", StringComparison.Ordinal);
    }

    private static string NormalizeDigits(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.DecimalDigitNumber)
                builder.Append(char.GetNumericValue(character).ToString("0", CultureInfo.InvariantCulture));
            else
                builder.Append(character);
        }
        return builder.ToString();
    }

    private static bool AreDistinctiveNamesGrounded(string question, string? corpus)
    {
        var corpusTokens = ContentTokens(corpus ?? string.Empty, 2);
        if (corpusTokens.Count == 0)
            return true;

        return DistinctiveNameTokens(question).All(name =>
            corpusTokens.Any(token => IsGroundedName(name, token)));
    }

    private static IEnumerable<string> DistinctiveNameTokens(string question)
    {
        var tokens = LexicalTokenRegex.Matches(question);
        for (var index = 1; index < tokens.Count; index++)
        {
            var token = tokens[index].Value;
            if (token.Length >= 2 && token.Any(char.IsUpper))
                yield return Normalize(token);
        }
    }

    private static List<string> ContentTokens(string value, int minimumLength)
        => LexicalTokenRegex.Matches(NormalizeForEcho(value))
            .Select(match => match.Value)
            .Where(token => token.Length >= minimumLength)
            .ToList();

    private static bool HasQuestionFramingNovelty(string question, string? source)
    {
        var questionTokens = ContentTokens(question, 1);
        if (questionTokens.Count == 0)
            return false;

        var sourceTokens = ContentTokens(source ?? string.Empty, 1).ToHashSet(StringComparer.Ordinal);
        return sourceTokens.Count == 0 || questionTokens.Any(token => !sourceTokens.Contains(token));
    }

    private static bool IsGroundedName(string questionToken, string corpusToken)
    {
        if (questionToken.Contains(corpusToken, StringComparison.Ordinal)
            || corpusToken.Contains(questionToken, StringComparison.Ordinal))
        {
            return true;
        }

        var prefix = 0;
        var limit = Math.Min(questionToken.Length, corpusToken.Length);
        while (prefix < limit && questionToken[prefix] == corpusToken[prefix])
            prefix++;
        var longer = Math.Max(questionToken.Length, corpusToken.Length);
        return prefix >= 4
            && (prefix >= Math.Ceiling(longer * 0.7)
                || (questionToken.Length - prefix <= 2 && corpusToken.Length - prefix <= 2));
    }
}
