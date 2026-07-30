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
}
