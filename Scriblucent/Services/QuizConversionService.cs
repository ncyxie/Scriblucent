using Scriblucent.Localization;
using Scriblucent.Models;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Scriblucent.Services;

public sealed class QuizConversionService
{
    private const int MaxQuizTitleLength = 90;
    private const int MaxQuestionCount = 60;

    public async Task<QuizDocument?> ConvertToQuizAsync(
        string noteTitle,
        string noteText,
        Guid? sourceNoteId = null,
        IProgress<BundledModelHostService.FlashcardProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(noteText))
            return null;

        AiInputGuard.EnsureSuitableStudyText(noteText);

        var normalizedNoteTitle = NormalizeTitle(noteTitle);
        var chunkingProfile = BundledModelHostService.Instance.GetSelectedChunkingProfile();
        var chunks = AiTextChunker.Split(noteText, AiChunkingPurpose.Quiz, chunkingProfile)
            .Where(AiInputGuard.IsSuitableStudyText)
            .ToList();

        if (chunks.Count == 0)
            throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));

        var questions = new List<QuizQuestion>();
        var outputs = new List<string>();
        var sawRefusal = false;

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkProgress = CreateChunkProgress(progress, i + 1, chunks.Count);
            var targetQuestionCount = CalculateTargetChunkQuestionCount(chunks[i], chunks.Count);
            var prompt = BuildPrompt(normalizedNoteTitle, chunks[i], i + 1, chunks.Count);
            var output = await BundledModelHostService.Instance.CompleteAsync(
                prompt,
                nPredict: CalculatePredictTokens(targetQuestionCount),
                temperature: 0.25,
                progress: chunkProgress,
                cancellationToken);
            outputs.Add(output);

            if (AiInputGuard.IsRefusalOutput(output))
            {
                sawRefusal = true;
                continue;
            }

            var parsed = ParseQuiz(output);
            AddParsedQuestions(questions, parsed.Questions, i + 1);
        }

        var parsedQuestions = PrepareDisplayQuestions(DeduplicateQuestions(questions).Take(MaxQuestionCount).ToList());
        if (parsedQuestions.Count > 0)
        {
            return new QuizDocument
            {
                Title = ResolveQuizTitle(normalizedNoteTitle, noteText),
                Questions = parsedQuestions,
                SourceNoteId = sourceNoteId
            };
        }

        if (sawRefusal && outputs.Count > 0 && outputs.All(AiInputGuard.IsRefusalOutput))
            throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));

        var repairPrompt = BuildRepairPrompt(
            normalizedNoteTitle,
            noteText);
        var repairTargetQuestionCount = CalculateTargetChunkQuestionCount(noteText, chunkCount: 1);
        var repairProgress = CreateRepairProgress(progress, "ConvertToTestStatusRepairing");
        var repaired = await BundledModelHostService.Instance.CompleteAsync(
            repairPrompt,
            nPredict: CalculatePredictTokens(repairTargetQuestionCount, isRepair: true),
            temperature: 0.1,
            progress: repairProgress,
            cancellationToken);

        if (AiInputGuard.IsRefusalOutput(repaired))
            throw new AiInputRejectedException(LocalizationService.GetString("AiInputRejectedInsufficientContent"));

        var repairedParsed = ParseQuiz(repaired);
        var repairedQuestions = new List<QuizQuestion>();
        AddParsedQuestions(repairedQuestions, repairedParsed.Questions, 1);
        var repairedOnlyQuestions = PrepareDisplayQuestions(DeduplicateQuestions(repairedQuestions).Take(MaxQuestionCount).ToList());
        if (repairedOnlyQuestions.Count > 0)
        {
            return new QuizDocument
            {
                Title = ResolveQuizTitle(normalizedNoteTitle, noteText),
                Questions = repairedOnlyQuestions,
                SourceNoteId = sourceNoteId
            };
        }

        var diagnosticPathChunks = WriteParseDiagnostic(
            noteText,
            string.Join("\n\n--- CHUNK OUTPUT ---\n\n", outputs),
            repaired);
        throw new InvalidOperationException($"Unable to parse AI output into a valid quiz. Diagnostic log: {diagnosticPathChunks}");
    }


    private static IProgress<BundledModelHostService.FlashcardProgress>? CreateChunkProgress(
        IProgress<BundledModelHostService.FlashcardProgress>? progress,
        int chunkIndex,
        int chunkCount)
    {
        if (progress is null)
            return progress;

        return new Progress<BundledModelHostService.FlashcardProgress>(status =>
            progress.Report(status with
            {
                ChunkIndex = Math.Max(1, chunkIndex),
                ChunkCount = Math.Max(1, chunkCount)
            }));
    }

    private static IProgress<BundledModelHostService.FlashcardProgress>? CreateRepairProgress(
        IProgress<BundledModelHostService.FlashcardProgress>? progress,
        string repairStatusKey)
    {
        if (progress is null)
            return progress;

        progress.Report(new BundledModelHostService.FlashcardProgress(repairStatusKey));

        return new Progress<BundledModelHostService.FlashcardProgress>(status =>
            progress.Report(status with
            {
                StatusKey = repairStatusKey
            }));
    }

    private static int CalculateTargetChunkQuestionCount(string sourceNote, int chunkCount)
    {
        var wordCount = CountWords(sourceNote);
        if (wordCount < 70)
            return 4;

        if (wordCount < 140)
            return 5;

        if (chunkCount <= 1)
            return wordCount < 260 ? 7 : 9;

        return wordCount < 180 ? 5 : 7;
    }

    private static int CalculatePredictTokens(int targetQuestionCount, bool isRepair = false)
    {
        var baseTokens = isRepair ? 900 : 700;
        var perQuestionTokens = isRepair ? 260 : 240;
        return Math.Clamp(baseTokens + (targetQuestionCount * perQuestionTokens), 1400, 3600);
    }

    private static int CountWords(string text)
    {
        return Regex.Matches(text, @"[\p{L}][\p{L}\p{Mn}'’-]{1,}").Count;
    }

    private static string BuildQuestionTypePlan(int targetQuestionCount)
    {
        var plan = new List<string>();
        for (var i = 1; i <= targetQuestionCount; i++)
        {
            var type = i switch
            {
                1 => "type: single, 4 options preferred",
                2 => "type: truefalse (prefer a false statement when plausible)",
                3 => "type: multi, 4 options preferred",
                4 => "type: single, 3 options ok",
                5 => "type: truefalse (prefer a true statement)",
                6 => "type: multi, 4 options preferred",
                7 => "type: single, 4 options preferred",
                8 => "type: truefalse (prefer a false statement when plausible)",
                _ => i % 3 == 0 ? "type: multi, 4 options preferred" : i % 2 == 0 ? "type: truefalse" : "type: single, 3 options ok"
            };

            plan.Add($"- Q{i}: {type}");
        }

        return string.Join("\n", plan);
    }

    private static string BuildPrompt(string noteTitle, string sourceNote, int chunkIndex = 1, int chunkCount = 1)
    {
        var displayTitle = string.IsNullOrWhiteSpace(noteTitle) ? "(blank)" : noteTitle;
        var targetQuestionCount = CalculateTargetChunkQuestionCount(sourceNote, chunkCount);
        var questionTypePlan = BuildQuestionTypePlan(targetQuestionCount);
        var sectionInstruction = chunkCount > 1
            ? $"You are processing section {chunkIndex} of {chunkCount} from a longer note."
            : "Build a focused quiz for the whole note.";

        return $"""
You are a strict quiz generator.
Never think out loud. Never explain. Never output reasoning, analysis, <think> tags or any extra text.

{sectionInstruction}
Cover the important facts in this section from beginning to end.
Create exactly {targetQuestionCount} high-quality quiz questions.
Use SOURCE NOTE as the authority for correct answers and explanations.
Questions, correct lines, true statements in truefalse questions, and explanations must use only SOURCE NOTE facts.
Only wrong lines and false true-false statements may invent plausible false distractors related to SOURCE NOTE.
Use SOURCE NOTE's language and script for all generated quiz content; use English only for the format keys.
Keep format keys exactly as shown: title, q, type, correct, wrong, answer, explanation.
Use NOTE TITLE as the quiz title. Do not create questions from NOTE TITLE alone.
If SOURCE NOTE is random, incoherent, mostly symbols, image placeholders, only a few words, only one thin sentence, or does not contain enough meaningful study content, output exactly:
{AiInputGuard.RefusalOutput}
Do not invent context to make unsuitable text look useful.

Output ONLY in this exact format, nothing else:

title: NOTE TITLE

q: question text
type: single
correct: correct answer
wrong: plausible wrong answer
wrong: plausible wrong answer
wrong: plausible wrong answer
explanation: short factual explanation

q: question text
type: multi
correct: correct answer
correct: another correct answer
wrong: plausible wrong answer
wrong: plausible wrong answer
explanation: short factual explanation

q: true-false statement
type: truefalse
answer: true/false
explanation: short factual explanation

Question type plan:
{questionTypePlan}

Rules:
- Repeat q/type/correct/wrong/explanation lines for each question.
- Do not use OPTIONS, [x], tables, markdown, numbering, or END markers.
- Keep question text concise and explanations short.
- Follow the question type plan exactly.
- For type: single, output exactly one correct line and two or three wrong lines. Prefer three wrong lines.
- For type: multi, output type: multi, then at least two correct lines and one or two wrong lines. Prefer four total options.
- Follow the option count hint in the question type plan when possible.
- Single and multi questions should have 3-4 total options; truefalse has exactly 2 options.
- Do not pad with an unrelated fourth option; use 3 total options if a plausible fourth option is not available.
- Wrong options must match the semantic category of the correct answer: dates with dates, places with places, people with people, concepts with concepts.
- truefalse: output answer: true/false or the translated equivalent instead of correct/wrong lines.
- Multi questions should ask which statements/items are correct, using SOURCE NOTE-supported facts and plausible distractors.
- True-false should include both true and false statements when plausible.
- Mark only SOURCE NOTE-supported facts as correct; wrong lines may be plausible distractors.
- Ask about SOURCE NOTE facts only.

NOTE TITLE:
{displayTitle}

SOURCE NOTE:
{sourceNote}
""";
    }

    private static string BuildRepairPrompt(string noteTitle, string sourceNote)
    {
        var displayTitle = string.IsNullOrWhiteSpace(noteTitle) ? "(blank)" : noteTitle;
        var targetQuestionCount = CalculateTargetChunkQuestionCount(sourceNote, chunkCount: 1);
        var questionTypePlan = BuildQuestionTypePlan(targetQuestionCount);

        return $"""
You repair quiz output.
Ignore any previous noisy text or thinking.
Never think out loud. Never explain. Never output reasoning, analysis, <think> tags or any extra text.

Create exactly {targetQuestionCount} valid quiz questions for the whole note.
Use SOURCE NOTE as the authority for correct answers and explanations.
Questions, correct lines, true statements in truefalse questions, and explanations must use only SOURCE NOTE facts.
Only wrong lines and false true-false statements may invent plausible false distractors related to SOURCE NOTE.
Use SOURCE NOTE's language and script for all generated quiz content; use English only for the format keys.
Keep format keys exactly as shown: title, q, type, correct, wrong, answer, explanation.
Use NOTE TITLE as the quiz title. Do not create questions from NOTE TITLE alone.
If SOURCE NOTE is random, incoherent, mostly symbols, image placeholders, only a few words, only one thin sentence, or does not contain enough meaningful study content, output exactly:
{AiInputGuard.RefusalOutput}
Do not invent context to make unsuitable text look useful.

Output ONLY in this exact format, nothing else:

title: NOTE TITLE

q: question text
type: single
correct: correct answer
wrong: plausible wrong answer
wrong: plausible wrong answer
wrong: plausible wrong answer
explanation: short factual explanation

q: question text
type: multi
correct: correct answer
correct: another correct answer
wrong: plausible wrong answer
wrong: plausible wrong answer
explanation: short factual explanation

q: true-false statement
type: truefalse
answer: true/false
explanation: short factual explanation

Question type plan:
{questionTypePlan}

Rules:
- Repeat q/type/correct/wrong/explanation lines for each question.
- Do not use OPTIONS, [x], tables, markdown, numbering, or END markers.
- Keep question text concise and explanations short.
- Follow the question type plan exactly.
- For type: single, output exactly one correct line and two or three wrong lines. Prefer three wrong lines.
- For type: multi, output type: multi, then at least two correct lines and one or two wrong lines. Prefer four total options.
- Follow the option count hint in the question type plan when possible.
- Single and multi questions should have 3-4 total options; truefalse has exactly 2 options.
- Do not pad with an unrelated fourth option; use 3 total options if a plausible fourth option is not available.
- Wrong options must match the semantic category of the correct answer: dates with dates, places with places, people with people, concepts with concepts.
- truefalse: output answer: true/false or the translated equivalent instead of correct/wrong lines.
- Multi questions should ask which statements/items are correct, using SOURCE NOTE-supported facts and plausible distractors.
- True-false should include both true and false statements when plausible.
- Mark only SOURCE NOTE-supported facts as correct; wrong lines may be plausible distractors.
- Ask about SOURCE NOTE facts only.

NOTE TITLE:
{displayTitle}

SOURCE NOTE:
{sourceNote}
""";
    }

    private static ParsedQuiz ParseQuiz(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return new ParsedQuiz(string.Empty, []);

        var cleaned = RemoveThinkAndArtifacts(rawOutput);
        var simpleParsed = ParseSimpleQuiz(cleaned);
        if (simpleParsed.Questions.Count > 0)
            return simpleParsed;

        return new ParsedQuiz(string.Empty, []);
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
        var isLithuanian = IsLithuanianTrueFalseAnswer(answerKey);
        var trueText = isLithuanian ? "Teisinga" : "True";
        var falseText = isLithuanian ? "Klaidinga" : "False";
        var isTrue = answerKey.Contains("true", StringComparison.OrdinalIgnoreCase)
            || answerKey.Contains("teisinga", StringComparison.OrdinalIgnoreCase)
            || answerKey.Contains("tiesa", StringComparison.OrdinalIgnoreCase)
            || answerKey.Contains("taip", StringComparison.OrdinalIgnoreCase);

        return
        [
            new QuizOption { Text = trueText, IsCorrect = isTrue },
            new QuizOption { Text = falseText, IsCorrect = !isTrue }
        ];
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

    private static void AddParsedQuestions(ICollection<QuizQuestion> target, IEnumerable<QuizQuestion> questions, int setIndex)
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

    private static IReadOnlyList<QuizQuestion> DeduplicateQuestions(IEnumerable<QuizQuestion> questions)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<QuizQuestion>();

        foreach (var question in questions)
        {
            var key = NormalizeKey(question.Question);
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
                continue;

            unique.Add(question);
        }

        return unique;
    }

    private static List<QuizQuestion> PrepareDisplayQuestions(IReadOnlyList<QuizQuestion> questions)
    {
        foreach (var question in questions.Where(question => question.Type != QuizQuestionType.TrueFalse))
            ShuffleChoiceOptions(question);

        return questions
            .Where(IsDisplayReadyQuestion)
            .ToList();
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

    private static string ResolveQuizTitle(string noteTitle, string noteText)
    {
        if (!IsGenericNoteTitle(noteTitle))
            return NormalizeTitle(noteTitle);

        var fallback = Regex
            .Split(noteText.Replace("\r", "\n", StringComparison.Ordinal), @"\n+|(?<=[.!?])\s+")
            .Select(NormalizeTitle)
            .FirstOrDefault(title => title.Length >= 5);

        return string.IsNullOrWhiteSpace(fallback)
            ? LocalizationService.GetString("QuizUntitled")
            : fallback;
    }

    private static bool IsGenericNoteTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return true;

        var normalized = NormalizeKey(title);
        string[] genericTitles =
        [
            "new note",
            "naujas uzrasas",
            "naujas užrašas",
            "untitled",
            "be pavadinimo"
        ];

        return genericTitles.Any(generic => string.Equals(normalized, NormalizeKey(generic), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeTitle(string value)
    {
        var normalized = NormalizeQuizText(value);
        normalized = Regex.Replace(normalized, @"^(?:TITLE|QUIZ TITLE|TEST TITLE|PAVADINIMAS)\s*[:：-]\s*", string.Empty, RegexOptions.IgnoreCase);
        normalized = normalized.Trim(' ', '"', '\'', '`', ':', '-', '•');

        if (normalized.Length > MaxQuizTitleLength)
            normalized = normalized[..MaxQuizTitleLength].TrimEnd() + "...";

        return normalized;
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

    private static string NormalizeKey(string value)
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

    private static bool IsLithuanianTrueFalseAnswer(string answerKey)
    {
        string[] markers = ["teisinga", "klaidinga", "tiesa", "netiesa", "taip", "ne"];
        return markers.Any(marker => Regex.IsMatch(
            answerKey,
            $@"(?:^|\s){Regex.Escape(marker)}(?:\s|$)",
            RegexOptions.IgnoreCase));
    }

    private static string RemoveThinkAndArtifacts(string text)
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

    private static string WriteParseDiagnostic(string sourceNote, string firstOutput, string repairedOutput)
    {
        try
        {
            var diagnosticsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Scriblucent",
                "AiCache",
                "diagnostics");

            Directory.CreateDirectory(diagnosticsDir);
            var path = Path.Combine(diagnosticsDir, $"quiz-parse-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

            var sb = new StringBuilder();
            sb.AppendLine($"TimestampUtc: {DateTime.UtcNow:O}");
            sb.AppendLine("--- SOURCE NOTE ---");
            sb.AppendLine(sourceNote);
            sb.AppendLine();
            sb.AppendLine("--- FIRST OUTPUT ---");
            sb.AppendLine(firstOutput);
            sb.AppendLine();
            sb.AppendLine("--- REPAIR OUTPUT ---");
            sb.AppendLine(repairedOutput);

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
        catch
        {
            return "(failed to write parse diagnostic log)";
        }
    }

    private sealed record ParsedQuiz(string Title, IReadOnlyList<QuizQuestion> Questions);

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
