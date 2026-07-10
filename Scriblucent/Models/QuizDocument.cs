namespace Scriblucent.Models;

public sealed class QuizDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<QuizQuestion> Questions { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModified { get; set; } = DateTime.Now;
    public string AiModelDisplayName { get; set; } = string.Empty;
    public Guid? SourceNoteId { get; set; }
    public Guid? GroupId { get; set; }
    public List<NoteScheduleEntry> Schedules { get; set; } = new();
    public bool IsPinned { get; set; }
    public int? TimeLimitSeconds { get; set; } // null = be limito
    public int PassingScorePercent { get; set; } = 70;

    public List<QuizAttempt> Attempts { get; set; } = new();
}

public sealed class QuizAttempt
{
    public DateTime Date { get; set; } = DateTime.Now;
    public int CorrectCount { get; set; }
    public int TotalQuestions { get; set; }
    public double Percentage => TotalQuestions == 0 ? 0 : (double)CorrectCount / TotalQuestions * 100;
    public TimeSpan TimeTaken { get; set; }
    public string QuizTitle { get; set; } = string.Empty;
    public int PassingScorePercent { get; set; } = 50;
}


