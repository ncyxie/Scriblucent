using Scriblucent.Models;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

internal static class QuizOutputParser
{
    private const int MaxQuizTitleLength = 90;

    internal sealed record ParsedQuiz(string Title, IReadOnlyList<QuizQuestion> Questions);

    internal static ParsedQuiz Parse(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return new ParsedQuiz(string.Empty, []);

        var cleaned = Clean(rawOutput);
        var simpleParsed = ParseSimpleQuiz(cleaned);
        if (simpleParsed.Questions.Count > 0)
            return simpleParsed;

        return new ParsedQuiz(string.Empty, []);
    }

    internal static void AddParsedQuestions(ICollection<QuizQuestion> target, IEnumerable<QuizQuestion> questions, int setIndex)
    {
        foreach (var question in questions)
        {
            target.Add(new QuizQuestion
            {
                Type = question.Type,
                Question = question.Question,
                Options = question.Options
                    .Select(option => new QuizOption
                    {
                        Text = option.Text,
                        IsCorrect = option.IsCorrect
                    })
                    .ToList(),
                Explanation = question.Explanation,
                SetIndex = Math.Max(1, setIndex)
            });
        }
    }

    internal static IReadOnlyList<QuizQuestion> DeduplicateQuestions(IEnumerable<QuizQuestion> questions)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<QuizQuestion>();

        foreach (var question in questions)
        {
            var questionKey = NormalizeKey(question.Question);
            if (string.IsNullOrWhiteSpace(questionKey))
                continue;

            // Two genuinely different questions can share a normalized stem (generic model
            // phrasing repeated across chunks/sections) - folding in the correct answer(s), not
            // just the question text, keeps that pair distinct instead of dropping one of them.
            var answerKey = string.Join("|", question.Options
                .Where(option => option.IsCorrect)
                .Select(option => NormalizeKey(option.Text))
                .OrderBy(key => key, StringComparer.Ordinal));

            var key = $"{questionKey}||{answerKey}";
            if (!seen.Add(key))
                continue;

            unique.Add(question);
        }

        return unique;
    }

    internal static List<QuizQuestion> PrepareDisplayQuestions(IReadOnlyList<QuizQuestion> questions)
    {
        foreach (var question in questions.Where(question => question.Type != QuizQuestionType.TrueFalse))
            ShuffleChoiceOptions(question);

        return questions
            .Where(IsDisplayReadyQuestion)
            .ToList();
    }

    internal static string NormalizeTitle(string value)
    {
        var normalized = NormalizeQuizText(value);
        normalized = Regex.Replace(normalized, @"^(?:TITLE|QUIZ TITLE|TEST TITLE|PAVADINIMAS)\s*[:：-]\s*", string.Empty, RegexOptions.IgnoreCase);
        normalized = normalized.Trim(' ', '"', '\'', '`', ':', '-', '•');

        if (normalized.Length > MaxQuizTitleLength)
            normalized = normalized[..MaxQuizTitleLength].TrimEnd() + "...";

        return normalized;
    }

    internal static string NormalizeKey(string value)
    {
        var normalized = NormalizeQuizText(value).ToLowerInvariant();
        normalized = normalized
            .Replace('ą', 'a')
            .Replace('č', 'c')
            .Replace('ę', 'e')
            .Replace('ė', 'e')
            .Replace('į', 'i')
            .Replace('š', 's')
            .Replace('ų', 'u')
            .Replace('ū', 'u')
            .Replace('ž', 'z');
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}\s]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    internal static string Clean(string text)
    {
        var withoutAnsi = Regex.Replace(text, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty);
        var withoutThink = Regex.Replace(withoutAnsi, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        withoutThink = withoutThink.Replace("<think>", string.Empty, StringComparison.OrdinalIgnoreCase);
        withoutThink = withoutThink.Replace("</think>", string.Empty, StringComparison.OrdinalIgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"\[Start thinking\].*$", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*>.*$", string.Empty, RegexOptions.Multiline);
        withoutThink = Regex.Replace(withoutThink, @"^\s*Thinking Process:.*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*\[?end of text\]?\s*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*<\|end\|>\s*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        withoutThink = Regex.Replace(withoutThink, @"^\s*EOF\s*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase);
        withoutThink = withoutThink.Replace("EOF by user", string.Empty, StringComparison.OrdinalIgnoreCase);
        withoutThink = withoutThink.Replace("Interrupted by user", string.Empty, StringComparison.OrdinalIgnoreCase);
        return RemoveTrailingArtifacts(withoutThink.Trim());
    }

    private static ParsedQuiz ParseSimpleQuiz(string cleanedOutput)
    {
        var lines = cleanedOutput
            .Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Where(line => !line.StartsWith("```", StringComparison.Ordinal))
            .ToList();

        var questions = new List<QuizQuestion>();
        SimpleQuizParseState? current = null;

        foreach (var line in lines)
        {
            if (TryParseTitleLine(line, out _))
                continue;

            if (TryStripSimplePrefix(line, ["q", "question", "klausimas"], out var questionText))
            {
                AddSimpleQuestion(questions, current);
                current = new SimpleQuizParseState { QuestionText = NormalizeQuizText(questionText) };
                continue;
            }

            if (current is null)
                continue;

            if (TryStripSimplePrefix(line, ["type", "tipas"], out var typeText))
            {
                current.Type = ParseQuestionType(typeText);
                current.HasExplicitType = true;
                continue;
            }

            if (TryStripSimplePrefix(line, ["correct", "right", "teisingas", "teisinga"], out var correctText))
            {
                current.Correct.Add(NormalizeOptionText(correctText));
                continue;
            }

            if (TryStripSimplePrefix(line, ["wrong", "incorrect", "neteisingas", "neteisinga", "klaidingas", "klaidinga"], out var wrongText))
            {
                current.Wrong.Add(NormalizeOptionText(wrongText));
                continue;
            }

            if (TryStripSimplePrefix(line, ["answer", "a", "atsakymas"], out var answerText))
            {
                current.Answer = NormalizeQuizText(answerText);
                continue;
            }

            if (TryStripSimplePrefix(line, ["explanation", "paaiškinimas", "paaiskinimas"], out var explanationText))
            {
                current.Explanation = NormalizeExplanationText(explanationText);
                continue;
            }
        }

        AddSimpleQuestion(questions, current);
        return new ParsedQuiz(string.Empty, questions);
    }

    private static void AddSimpleQuestion(ICollection<QuizQuestion> questions, SimpleQuizParseState? state)
    {
        var question = BuildSimpleQuestion(state);
        if (question is not null)
            questions.Add(question);
    }

    private static QuizQuestion? BuildSimpleQuestion(SimpleQuizParseState? state)
    {
        if (state is null)
            return null;

        var questionText = NormalizeQuestionText(state.QuestionText);
        var explanation = NormalizeExplanationText(state.Explanation);
        var type = ResolveSimpleQuestionType(state);
        var options = type == QuizQuestionType.TrueFalse
            ? BuildSimpleTrueFalseOptions(state.Answer, state.Correct.FirstOrDefault())
            : BuildSimpleChoiceOptions(state.Correct, state.Wrong);

        var question = new QuizQuestion
        {
            Question = questionText,
            Type = type,
            Options = options,
            Explanation = explanation
        };

        NormalizeQuestionExplanation(question);

        return IsValidQuizQuestion(question) ? question : null;
    }

    private static QuizQuestionType ResolveSimpleQuestionType(SimpleQuizParseState state)
    {
        if (state.Type == QuizQuestionType.TrueFalse)
            return QuizQuestionType.TrueFalse;

        if (state.HasExplicitType && state.Type == QuizQuestionType.MultipleChoice)
            return QuizQuestionType.MultipleChoice;

        if (state.Correct.Count > 1)
            return QuizQuestionType.MultipleChoice;

        return QuizQuestionType.SingleChoice;
    }

    private static List<QuizOption> BuildSimpleChoiceOptions(IEnumerable<string> correct, IEnumerable<string> wrong)
    {
        return correct
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => new QuizOption { Text = text, IsCorrect = true })
            .Concat(wrong
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => new QuizOption { Text = text, IsCorrect = false }))
            .GroupBy(option => NormalizeKey(option.Text), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static List<QuizOption> BuildSimpleTrueFalseOptions(string answer, string? correctText)
    {
        var answerKey = NormalizeKey(string.IsNullOrWhiteSpace(answer) ? correctText ?? string.Empty : answer);

        // An answer with no recognizable true/false marker (garbled model output, "n/a", etc.)
        // must not silently become "False is correct" - drop it instead; IsValidQuizQuestion
        // rejects questions with fewer than 2 options.
        if (!TryResolveTrueFalseAnswer(answerKey, out var isTrue))
            return [];

        var isLithuanian = IsLithuanianTrueFalseAnswer(answerKey);
        var trueText = isLithuanian ? "Teisinga" : "True";
        var falseText = isLithuanian ? "Klaidinga" : "False";

        return
        [
            new QuizOption { Text = trueText, IsCorrect = isTrue },
            new QuizOption { Text = falseText, IsCorrect = !isTrue }
        ];
    }

    private static readonly string[] TrueAnswerMarkers = ["true", "teisinga", "tiesa", "taip"];
    private static readonly string[] FalseAnswerMarkers = ["false", "klaidinga", "netiesa", "ne"];

    private static bool TryResolveTrueFalseAnswer(string answerKey, out bool isTrue)
    {
        var trueHit = TrueAnswerMarkers.Any(marker => ContainsWholeWordMarker(answerKey, marker));
        var falseHit = FalseAnswerMarkers.Any(marker => ContainsWholeWordMarker(answerKey, marker));

        isTrue = trueHit;
        return trueHit || falseHit;
    }

    private static bool ContainsWholeWordMarker(string text, string marker)
    {
        return Regex.IsMatch(
            text,
            $@"(?:^|\s){Regex.Escape(marker)}(?:\s|$)",
            RegexOptions.IgnoreCase);
    }

    private static bool TryStripSimplePrefix(string line, string[] keywords, out string content)
    {
        var trimmed = line.Trim();
        foreach (var keyword in keywords)
        {
            var match = Regex.Match(trimmed, $@"^(?:[-*]\s*)?(?:{Regex.Escape(keyword)})\s*[:：\-]\s*(.+)$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                content = match.Groups[1].Value.Trim();
                return true;
            }
        }

        content = string.Empty;
        return false;
    }

    private static void NormalizeQuestionExplanation(QuizQuestion question)
    {
        question.Question = NormalizeQuestionText(question.Question);
        question.Explanation = NormalizeExplanationText(question.Explanation);

        if (!QuestionRepeatsExplanation(question))
            return;

        question.Explanation = BuildShortAnswerExplanation(question);
    }

    private static string BuildShortAnswerExplanation(QuizQuestion question)
    {
        if (question.Type == QuizQuestionType.TrueFalse)
            return "Based on the provided fact.";

        var correctAnswers = question.Options
            .Where(option => option.IsCorrect)
            .Select(option => option.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (correctAnswers.Count == 0)
            return string.Empty;

        return $"Correct answer: {string.Join(", ", correctAnswers)}.";
    }

    private static bool IsDisplayReadyQuestion(QuizQuestion question)
    {
        var correctCount = question.Options.Count(option => option.IsCorrect);
        return question.Type switch
        {
            QuizQuestionType.TrueFalse => question.Options.Count == 2 && correctCount == 1,
            QuizQuestionType.SingleChoice => question.Options.Count is >= 3 and <= 6 && correctCount == 1,
            QuizQuestionType.MultipleChoice => question.Options.Count is >= 3 and <= 6
                && correctCount >= 2
                && correctCount < question.Options.Count,
            _ => false
        };
    }

    private static void ShuffleChoiceOptions(QuizQuestion question)
    {
        question.Options = question.Options
            .OrderBy(option => StableOptionOrderKey(question.Question, option.Text))
            .ThenBy(option => NormalizeKey(option.Text), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int StableOptionOrderKey(string question, string option)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in NormalizeKey(question + " " + option))
                hash = (hash * 31) + c;
            return hash;
        }
    }

    private static bool TryParseTitleLine(string line, out string title)
    {
        var match = Regex.Match(line, @"^(?:TITLE|QUIZ TITLE|TEST TITLE|PAVADINIMAS)\s*[:：-]\s*(.+)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            title = NormalizeTitle(match.Groups[1].Value);
            return !string.IsNullOrWhiteSpace(title);
        }

        title = string.Empty;
        return false;
    }

    private static QuizQuestionType ParseQuestionType(string value)
    {
        var normalized = NormalizeKey(value);
        if (normalized.Contains("true false", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("truefalse", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "tf", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("teisinga klaidinga", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("klaidinga", StringComparison.OrdinalIgnoreCase))
        {
            return QuizQuestionType.TrueFalse;
        }

        if (normalized.Contains("multiple", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("multi", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("keli", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("kelis", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("daug", StringComparison.OrdinalIgnoreCase))
        {
            return QuizQuestionType.MultipleChoice;
        }

        return QuizQuestionType.SingleChoice;
    }

    private static bool IsValidQuizQuestion(QuizQuestion question)
    {
        if (string.IsNullOrWhiteSpace(question.Question) || question.Question.Length < 8)
            return false;

        if (LooksLikePromptLeak(question.Question)
            || question.Options.Any(option => LooksLikePromptLeak(option.Text))
            || LooksLikePromptLeak(question.Explanation))
        {
            return false;
        }

        if (QuestionRepeatsExplanation(question))
            return false;

        if (question.Options.Count < 2 || question.Options.Count > 6)
            return false;

        var correctCount = question.Options.Count(option => option.IsCorrect);

        if (question.Type == QuizQuestionType.TrueFalse && (question.Options.Count != 2 || correctCount != 1))
            return false;

        // A truefalse item phrased as an open question (ends with "?" or opens with a WH-word)
        // can never be coherent - there's no sensible "true or false" for "What did she invent?".
        // Verified directly against the real bundled model that this mismatch happens even with
        // an explicit prompt instruction against it, so the safest outcome is to drop the item
        // rather than publish a broken question, same as the unrecognized-answer case below.
        if (question.Type == QuizQuestionType.TrueFalse && LooksLikeOpenQuestion(question.Question))
            return false;

        if (question.Type == QuizQuestionType.SingleChoice && (question.Options.Count < 2 || correctCount != 1))
            return false;

        if (question.Type == QuizQuestionType.MultipleChoice
            && (question.Options.Count < 3 || correctCount < 2 || correctCount >= question.Options.Count))
        {
            return false;
        }

        if (question.Options.Any(option => string.IsNullOrWhiteSpace(option.Text)))
            return false;

        var uniqueOptionCount = question.Options
            .Select(option => NormalizeKey(option.Text))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return uniqueOptionCount == question.Options.Count;
    }

    private static readonly string[] WhQuestionWords =
        ["what", "when", "who", "which", "how", "where", "why", "kas", "kada", "kur", "kodėl", "kodel", "koks", "kokia"];

    private static bool LooksLikeOpenQuestion(string questionText)
    {
        if (string.IsNullOrWhiteSpace(questionText))
            return false;

        if (questionText.TrimEnd().EndsWith('?'))
            return true;

        var firstWord = Regex.Match(questionText, @"^\W*(\w+)").Groups[1].Value.ToLowerInvariant();
        return WhQuestionWords.Contains(firstWord);
    }

    private static bool QuestionRepeatsExplanation(QuizQuestion question)
    {
        if (string.IsNullOrWhiteSpace(question.Explanation))
            return false;

        var questionKey = NormalizeQuestionForSimilarity(question.Question);
        var explanationKey = NormalizeQuestionForSimilarity(question.Explanation);
        if (questionKey.Length < 12 || explanationKey.Length < 12)
            return false;

        return string.Equals(questionKey, explanationKey, StringComparison.OrdinalIgnoreCase)
            || questionKey.Contains(explanationKey, StringComparison.OrdinalIgnoreCase)
            || explanationKey.Contains(questionKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeQuestionForSimilarity(string value)
    {
        var normalized = NormalizeKey(value);
        normalized = Regex.Replace(normalized, @"^(true or false|teisinga ar klaidinga)\s+", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"^(which option completes this note fact|kuris variantas uzpildo fakta)\s+", string.Empty, RegexOptions.IgnoreCase);
        normalized = normalized.Replace("____", string.Empty, StringComparison.Ordinal);
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static string NormalizeQuestionText(string value)
    {
        var normalized = NormalizeQuizText(value);
        normalized = Regex.Replace(
            normalized,
            @"\s+according to\s+(?:the\s+)?(?:source\s+note|source\s+text|source|text|note)\??$",
            normalized.EndsWith("?", StringComparison.Ordinal) ? "?" : string.Empty,
            RegexOptions.IgnoreCase);
        normalized = Regex.Replace(
            normalized,
            @"\s+pagal\s+(?:užrašą|uzrasa|tekstą|teksta)\??$",
            normalized.EndsWith("?", StringComparison.Ordinal) ? "?" : string.Empty,
            RegexOptions.IgnoreCase);
        return NormalizeQuizText(normalized);
    }

    private static string NormalizeOptionText(string value)
    {
        var normalized = NormalizeQuizText(value);
        normalized = Regex.Replace(normalized, @"^\s*(?:[-*•]|\[[xX1✓ ]?\]|[A-Ha-h][\.)])\s*", string.Empty);
        normalized = Regex.Replace(normalized, @"\s*\((?:correct|teisingas|teisinga)\)\s*$", string.Empty, RegexOptions.IgnoreCase);
        return normalized.Trim();
    }

    private static string NormalizeExplanationText(string value)
    {
        var normalized = NormalizeQuizText(value);
        normalized = Regex.Replace(
            normalized,
            @"^(?:according to\s+)?(?:the\s+)?(?:source\s+note|source\s+text|source|text|note)\s+(?:explicitly\s+)?(?:states|says|notes|mentions|provides|identifies|explains)\s+(?:that\s+)?",
            string.Empty,
            RegexOptions.IgnoreCase);
        normalized = Regex.Replace(
            normalized,
            @"^(?:according to\s+)?(?:the\s+)?(?:source\s+note|source\s+text|source|text|note)\s*[:,]\s*",
            string.Empty,
            RegexOptions.IgnoreCase);
        normalized = Regex.Replace(
            normalized,
            @"^(?:pagal\s+)?(?:užrašą|uzrasa|tekstą|teksta)\s+(?:aiškiai\s+|aiskiai\s+)?(?:teigia|nurodo|mini|paaiškina|paaiskina)\s*,?\s*(?:kad\s+)?",
            string.Empty,
            RegexOptions.IgnoreCase);
        normalized = Regex.Replace(
            normalized,
            @"^(?:pagal\s+)?(?:užrašą|uzrasa|tekstą|teksta)\s*[:,]\s*",
            string.Empty,
            RegexOptions.IgnoreCase);

        return NormalizeQuizText(normalized);
    }

    private static string NormalizeQuizText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Replace("\r", "", StringComparison.Ordinal).Trim();
        normalized = Regex.Replace(normalized, @"^\s*(?:[-*•]|\d+[\.)])\s*", string.Empty);
        normalized = Regex.Replace(normalized, @"\s*\[?end of text\]?\s*$", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s*<\|end\|>\s*$", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s*EOF\s*$", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return RemoveTrailingArtifacts(normalized.Trim(' ', '"', '\'', '`'));
    }

    private static readonly string[] LithuanianTrueFalseMarkers = ["teisinga", "klaidinga", "tiesa", "netiesa", "taip", "ne"];

    private static bool IsLithuanianTrueFalseAnswer(string answerKey)
    {
        return LithuanianTrueFalseMarkers.Any(marker => ContainsWholeWordMarker(answerKey, marker));
    }

    private static bool LooksLikePromptLeak(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.ToLowerInvariant();
        string[] leakMarkers =
        [
            "source note",
            "note title",
            "output only",
            "output format",
            "use only facts",
            "never think out loud",
            "no markdown",
            "format rules",
            "as per instructions",
            "per instructions",
            "follows the rules"
        ];

        return text.Contains("<think>", StringComparison.OrdinalIgnoreCase)
            || leakMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string RemoveTrailingArtifacts(string text)
    {
        var result = text.Trim();

        string[] trailingArtifacts =
        [
            "[end of text]",
            "end of text",
            "<|end|>",
            "EOF",
            "EOF by user",
            "Interrupted by user"
        ];

        foreach (var artifact in trailingArtifacts)
        {
            while (result.EndsWith(artifact, StringComparison.OrdinalIgnoreCase))
                result = result[..^artifact.Length].TrimEnd();
        }

        return result;
    }

    private sealed class SimpleQuizParseState
    {
        public string QuestionText { get; set; } = string.Empty;
        public QuizQuestionType Type { get; set; } = QuizQuestionType.SingleChoice;
        public bool HasExplicitType { get; set; }
        public List<string> Correct { get; } = new();
        public List<string> Wrong { get; } = new();
        public string Answer { get; set; } = string.Empty;
        public string Explanation { get; set; } = string.Empty;
    }
}
