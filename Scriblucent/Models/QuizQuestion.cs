namespace Scriblucent.Models;

public enum QuizQuestionType
{
    SingleChoice = 0,
    MultipleChoice = 1,
    TrueFalse = 2,
    PlainText = 3
}

public sealed class QuizQuestion
{
    // Stable identity so per-question progress (LastAttemptCorrect) survives edits/combines -
    // same convention as MindMapNode.Id. Auto-generated, so every `new QuizQuestion()` (Add
    // Question, AI generation, ...) already gets a fresh one for free.
    public Guid Id { get; set; } = Guid.NewGuid();

    public QuizQuestionType Type { get; set; } = QuizQuestionType.SingleChoice;
    public string Question { get; set; } = string.Empty;
    public List<QuizOption> Options { get; set; } = new();
    public string Explanation { get; set; } = string.Empty;
    public string Hint { get; set; } = string.Empty;  
    public int SetIndex { get; set; } = 1;

    public string PlainTextAnswer { get; set; } = string.Empty;
    public int PlainTextCharLimit { get; set; } = 500;
    public bool PlainTextMatchCase { get; set; }
    public bool PlainTextIgnorePunctuation { get; set; }

    // Result of the most recently completed full attempt this question was part of - null means
    // never attempted. Mirrors FlashcardItem.IsKnown/IsUnknown's per-item persisted-progress
    // pattern, so "Practice Missed Questions" can filter to these across app restarts instead of
    // only within one still-open QuizModeWindow session.
    public bool? LastAttemptCorrect { get; set; }
}
