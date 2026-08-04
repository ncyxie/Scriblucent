namespace Scriblucent.Models;

public sealed class FlashcardItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Question { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Hint { get; set; } = string.Empty;
    public int SetIndex { get; init; } = 1;
    public bool IsKnown { get; set; }
    public bool IsUnknown { get; set; }
    public Guid? LinkedNoteId { get; set; } = null;

    /// <summary>SM-2 spaced-repetition state. Defaults mean "new/never reviewed" - due immediately.</summary>
    public double EaseFactor { get; set; } = 2.5;
    public int IntervalDays { get; set; }
    public int Repetitions { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? LastReviewedAt { get; set; }

    /// <summary>FSRS-6 spaced-repetition state. Coexists with the SM-2 fields above; only the
    /// fields for the set's active SpacedRepetitionAlgorithm are read/written. Zero means
    /// "new/never reviewed" under FSRS too - FsrsScheduler seeds real values on first review.</summary>
    public double Stability { get; set; }
    public double Difficulty { get; set; }

    /// <summary>Optional image/audio attachments, stored as filenames under
    /// FlashcardMediaStorageService's media folder (not embedded in this document) - null/empty
    /// means no attachment.</summary>
    public string? QuestionImageFileName { get; set; }
    public string? QuestionAudioFileName { get; set; }
    public string? AnswerImageFileName { get; set; }
    public string? AnswerAudioFileName { get; set; }
}
