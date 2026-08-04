namespace Scriblucent.Services;

public enum FlashcardReviewGrade
{
    Again,
    Hard,
    Good,
    Easy
}

/// <summary>
/// Classic SM-2 spaced repetition, at day granularity (not Anki's sub-day learning steps - that
/// level of precision isn't worth the added complexity here). Pure and stateless: callers own
/// reading the current state off whatever object they hold (FlashcardItem, FlashcardPreviewItem,
/// ...) and writing ReviewResult back onto it.
/// </summary>
public static class FlashcardReviewScheduler
{
    private const double MinEaseFactor = 1.3;

    public readonly record struct ReviewState(double EaseFactor, int IntervalDays, int Repetitions);

    public readonly record struct ReviewResult(
        double EaseFactor,
        int IntervalDays,
        int Repetitions,
        DateTime DueDate,
        bool IsKnown,
        bool IsUnknown);

    /// <summary>
    /// Standard 4-button-to-SM2 quality mapping: Again=0, Hard=3, Good=4, Easy=5.
    /// </summary>
    public static ReviewResult ApplyGrade(ReviewState current, FlashcardReviewGrade grade, DateTime now)
    {
        var quality = grade switch
        {
            FlashcardReviewGrade.Again => 0,
            FlashcardReviewGrade.Hard => 3,
            FlashcardReviewGrade.Good => 4,
            FlashcardReviewGrade.Easy => 5,
            _ => 4
        };

        int intervalDays;
        int repetitions;

        if (quality < 3)
        {
            repetitions = 0;
            intervalDays = 1;
        }
        else
        {
            repetitions = current.Repetitions + 1;
            intervalDays = repetitions switch
            {
                1 => 1,
                2 => 6,
                _ => Math.Max(1, (int)Math.Round(current.IntervalDays * current.EaseFactor))
            };
        }

        var easeFactor = Math.Max(
            MinEaseFactor,
            current.EaseFactor + (0.1 - (5 - quality) * (0.08 + (5 - quality) * 0.02)));

        return new ReviewResult(
            EaseFactor: easeFactor,
            IntervalDays: intervalDays,
            Repetitions: repetitions,
            DueDate: now.Date.AddDays(intervalDays),
            IsKnown: quality >= 3,
            IsUnknown: quality < 3);
    }

    /// <summary>A null due date means "new/never reviewed" - always due.</summary>
    public static bool IsDue(DateTime? dueDate, DateTime now)
        => dueDate is null || dueDate.Value.Date <= now.Date;
}
