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
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public int? TimeLimitSeconds { get; set; } // null = be limito
    public int PassingScorePercent { get; set; } = 70;

    // Quiz-taking preferences - default false/off so existing quizzes behave exactly as before
    // until a user opts in via the Options dialog (QuizStartOptionsDialog).
    public bool ShuffleQuestions { get; set; }
    public bool ShuffleOptions { get; set; }
    public bool PartialCreditMultipleChoice { get; set; }

    // Exam conditions: no going back to a previous question once you've moved past it, and no
    // resuming a closed-mid-way attempt (closing it counts as forfeiting - starting over from
    // question 1 is the only option). ProgressSession is never captured while this is on.
    public bool ExamMode { get; set; }

    public List<QuizAttempt> Attempts { get; set; } = new();

    // Set while a quiz attempt is in progress and cleared the moment it's submitted - lets
    // reopening the quiz resume exactly where you left off, mirroring FlashcardStudySession.
    public QuizProgressSession? ProgressSession { get; set; }
}

public sealed class QuizProgressSession
{
    // Full snapshot of the questions as they were actually presented (already shuffled, if
    // shuffle was on) - resuming rebuilds QuizModeWindow from this exact list instead of the
    // live document, so stored answers/positions stay aligned no matter what shuffle settings
    // say by the time you come back.
    public List<QuizQuestion> Questions { get; set; } = new();
    public int CurrentQuestionIndex { get; set; }

    // QuestionId -> selected option indexes within that question's Questions[i].Options above.
    public Dictionary<Guid, List<int>> SelectedOptionIndexesByQuestionId { get; set; } = new();

    // QuestionId -> raw typed text, for PlainText questions only.
    public Dictionary<Guid, string> PlainTextAnswersByQuestionId { get; set; } = new();

    // QuestionIds the student flagged for later review (Moodle-style).
    public List<Guid> FlaggedQuestionIds { get; set; } = new();

    public int TimeLimitSeconds { get; set; }
    public long ElapsedSeconds { get; set; }
}

public sealed class QuizAttempt
{
    public DateTime Date { get; set; } = DateTime.Now;
    public int CorrectCount { get; set; }
    public int TotalQuestions { get; set; }

    // Fractional running score - QuizModeWindow.ShowResults always populates this on new
    // attempts (equal to CorrectCount when no partial credit was awarded, the common case).
    // Defaults to 0 for attempts recorded before this field existed, which is why Percentage only
    // trusts it when it's not a whole number - a fractional value can only come from real partial
    // credit, never from an old/missing record or a normal all-or-nothing attempt.
    public double EarnedPoints { get; set; }

    public double Percentage => TotalQuestions == 0
        ? 0
        : (EarnedPoints == Math.Floor(EarnedPoints) ? CorrectCount : EarnedPoints) / TotalQuestions * 100;

    public TimeSpan TimeTaken { get; set; }
    public string QuizTitle { get; set; } = string.Empty;
    public int PassingScorePercent { get; set; } = 50;
}


