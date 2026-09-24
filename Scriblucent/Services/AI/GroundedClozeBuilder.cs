using System.Globalization;
using System.Text.RegularExpressions;

namespace Scriblucent.Services.AI;

internal sealed record GroundedQuestionSeed(
    StudyFact Fact,
    string Cloze,
    string Answer,
    StudyAnswerKind AnswerKind,
    StudyValueShape ValueShape)
{
    public string AnswerTypeLabel => AnswerKind.ToString().ToLowerInvariant();
}

internal static class GroundedClozeBuilder
{
    private static readonly Regex LexicalTokenRegex = new(
        @"[\p{L}\p{Mn}\p{N}]+(?:['’\-][\p{L}\p{Mn}\p{N}]+)*",
        RegexOptions.Compiled);

    public static GroundedQuestionSeed CreateSeed(StudyFact fact)
    {
        var (cloze, answer) = Create(fact);
        return new GroundedQuestionSeed(
            fact,
            cloze,
            answer,
            fact.AnswerKind,
            fact.ValueShape);
    }

    public static (string Question, string Answer) Create(StudyFact fact)
        => TryCreate(fact, out var question, out var answer)
            ? (question, answer)
            : (fact.Evidence.Trim(), fact.Answer);

    public static bool TryCreate(StudyFact fact, out string question, out string answer)
    {
        question = fact.Evidence.Trim();
        answer = fact.Answer;
        if (question.Length == 0 || fact.Answer.Length == 0)
            return false;

        var answerStart = fact.AnswerStart >= 0
                          && fact.AnswerLength == fact.Answer.Length
                          && fact.AnswerStart + fact.AnswerLength <= question.Length
                          && string.Equals(
                              question.Substring(fact.AnswerStart, fact.AnswerLength),
                              fact.Answer,
                              StringComparison.Ordinal)
            ? fact.AnswerStart
            : question.IndexOf(fact.Answer, StringComparison.Ordinal);
        if (answerStart < 0)
            return false;

        var quoted = question.Substring(answerStart, fact.Answer.Length);
        if (!TryPickBlank(fact, question, answerStart, quoted, out var start, out var blank))
            return false;

        (question, answer) = Render(question, start, blank);
        return true;
    }

    public static bool IsViableSourceUnit(string text)
    {
        var evidence = text.Trim();
        if (evidence.Length == 0)
            return false;

        var fact = new StudyFact(
            "F000",
            "S000",
            evidence,
            evidence,
            StudyFactImportance.Supporting,
            0,
            AnswerStart: 0,
            AnswerLength: evidence.Length);
        return TryPickBlank(fact, evidence, 0, evidence, out _, out _);
    }

    private static bool TryPickBlank(
        StudyFact fact,
        string evidence,
        int answerStart,
        string answer,
        out int start,
        out string text)
    {
        start = -1;
        text = string.Empty;
        var answerEnd = answerStart + answer.Length;
        if (TryPickNoSpaceScriptBlank(evidence, answerStart, answer, out start, out text))
            return true;
        var tokens = LexicalTokenRegex.Matches(evidence)
            .Cast<Match>()
            .Where(match => match.Index >= answerStart && match.Index + match.Length <= answerEnd)
            .ToList();
        if (tokens.Count == 0)
            return false;

        if (IsAtomicValue(fact, tokens.Count))
        {
            if (!HasLetterStem(evidence, answerStart, answer))
                return false;
            start = answerStart;
            text = answer;
            return true;
        }

        foreach (var candidate in TokenBlankCandidates(evidence, tokens))
        {
            if (!HasLetterStem(evidence, candidate.Start, candidate.Text))
                continue;
            start = candidate.Start;
            text = candidate.Text;
            return true;
        }

        return false;
    }

    private static bool TryPickNoSpaceScriptBlank(
        string evidence,
        int answerStart,
        string answer,
        out int start,
        out string text)
    {
        start = -1;
        text = string.Empty;
        if (!answer.Any(IsNoSpaceScriptCharacter))
            return false;

        var elements = StringInfo.ParseCombiningCharacters(answer);
        if (elements.Length < 2)
            return false;
        var blankElements = Math.Clamp(elements.Length / 3, 1, 4);
        var fromElement = elements.Length - blankElements;
        var relativeStart = elements[fromElement];
        var relativeEnd = fromElement + blankElements < elements.Length
            ? elements[fromElement + blankElements]
            : answer.Length;
        start = answerStart + relativeStart;
        text = answer[relativeStart..relativeEnd];
        return HasLetterStem(evidence, start, text);
    }

    private static bool IsNoSpaceScriptCharacter(char character)
        => character is >= '\u3400' and <= '\u9fff'
           or >= '\u3040' and <= '\u30ff'
           or >= '\uac00' and <= '\ud7af';

    private static IEnumerable<(int Start, string Text)> TokenBlankCandidates(
        string evidence,
        IReadOnlyList<Match> tokens)
    {
        if (tokens.Count == 1)
        {
            yield return TokenRange(evidence, tokens, 0, 1);
            yield break;
        }

        if (tokens.Count == 2 && ShouldKeepPair(tokens[0], tokens[1]))
            yield return TokenRange(evidence, tokens, 0, 2);

        if (tokens.Count > 2 && ShouldKeepPair(tokens[^2], tokens[^1]))
            yield return TokenRange(evidence, tokens, tokens.Count - 2, tokens.Count);

        yield return TokenRange(evidence, tokens, tokens.Count - 1, tokens.Count);
        foreach (var index in Enumerable.Range(0, tokens.Count - 1)
                     .OrderByDescending(index => BlankTokenScore(tokens[index], index)))
        {
            yield return TokenRange(evidence, tokens, index, index + 1);
        }
    }

    private static bool ShouldKeepPair(Match first, Match second)
        => first.Value.Length >= 4
           || first.Value.Any(char.IsDigit)
           || (char.IsUpper(first.Value[0]) && char.IsUpper(second.Value[0]));

    private static int BlankTokenScore(Match token, int index)
        => (token.Value.Any(char.IsDigit) ? 100 : 0)
           + (char.IsUpper(token.Value[0]) ? 40 : 0)
           + Math.Min(token.Value.Length, 24)
           + index;

    private static (int Start, string Text) TokenRange(
        string evidence,
        IReadOnlyList<Match> tokens,
        int from,
        int toExclusive)
    {
        from = Math.Clamp(from, 0, tokens.Count - 1);
        toExclusive = Math.Clamp(toExclusive, from + 1, tokens.Count);
        var start = tokens[from].Index;
        var end = tokens[toExclusive - 1].Index + tokens[toExclusive - 1].Length;
        return (start, evidence.Substring(start, end - start));
    }

    private static bool IsAtomicValue(StudyFact fact, int tokenCount)
        => fact.ValueShape is StudyValueShape.CalendarDate or StudyValueShape.Year or StudyValueShape.AtomicNumber
           || (fact.ValueShape == StudyValueShape.ScopedQuantity && tokenCount == 1);

    private static bool HasLetterStem(string evidence, int start, string answer)
    {
        var end = start + answer.Length;
        if (start < 0 || end > evidence.Length)
            return false;
        var stem = string.Concat(evidence.AsSpan(0, start), evidence.AsSpan(end));
        return stem.Any(char.IsLetter);
    }

    private static (string Question, string Answer) Render(string evidence, int start, string answer)
    {
        var left = evidence[..start].TrimEnd();
        var right = evidence[(start + answer.Length)..].TrimStart();
        var cloze = string.Join(" ", new[] { left, "_____", right }
            .Where(part => part.Length > 0));
        cloze = Regex.Replace(cloze, @"\s+([.,;:!?。！？])", "$1");
        return (cloze, answer);
    }
}
