namespace Scriblucent.Services;

/// <summary>
/// FSRS-6 spaced repetition, ported against the reference implementation
/// (open-spaced-repetition/py-fsrs) and its own known-answer test vectors - see
/// Scriblucent.Tests/FsrsSchedulerTests.cs. Pure and stateless like FlashcardReviewScheduler:
/// callers own reading current state off whatever object they hold and writing ReviewResult back.
/// FSRS ratings are 1-based (Again=1..Easy=4); this app's FlashcardReviewGrade enum is 0-based, so
/// every entry point below maps via (int)grade + 1.
/// </summary>
public static class FsrsScheduler
{
    private static readonly double[] Parameters =
    {
        0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194, 0.001, 1.8722, 0.1666,
        0.796, 1.4835, 0.0614, 0.2629, 1.6483, 0.6014, 1.8729, 0.5425, 0.0912, 0.0658,
        0.1542
    };

    private const double MinStability = 0.001;
    private const double MinDifficulty = 1.0;
    private const double MaxDifficulty = 10.0;
    private const double DesiredRetention = 0.9;
    private const int MaximumIntervalDays = 36500;

    // DECAY/FACTOR are derived once from parameters[20] and anchor the forgetting curve so that
    // retrievability equals exactly 90% when elapsed time equals stability.
    private static readonly double Decay = -Parameters[20];
    private static readonly double Factor = Math.Pow(0.9, 1.0 / Decay) - 1.0;

    public readonly record struct ReviewState(double Stability, double Difficulty, DateTime? LastReviewedAt);

    public readonly record struct ReviewResult(
        double Stability,
        double Difficulty,
        DateTime DueDate,
        bool IsKnown,
        bool IsUnknown);

    public static ReviewResult ApplyGrade(ReviewState current, FlashcardReviewGrade grade, DateTime now)
    {
        var rating = (int)grade + 1;

        double stability;
        double difficulty;

        if (current.LastReviewedAt is null)
        {
            stability = InitialStability(rating);
            difficulty = InitialDifficulty(rating);
        }
        else
        {
            difficulty = NextDifficulty(current.Difficulty, rating);

            var elapsedDays = (now - current.LastReviewedAt.Value).TotalDays;
            if (elapsedDays < 1.0)
            {
                // Same-day/short-term path: two real reviews of the same card within one sitting.
                // Distinct from SM-2, which has no concept of sub-day review timing.
                stability = ShortTermStability(current.Stability, rating);
            }
            else
            {
                var retrievability = Retrievability(elapsedDays, current.Stability);
                stability = grade == FlashcardReviewGrade.Again
                    ? NextStabilityAfterForgetting(current.Stability, current.Difficulty, retrievability)
                    : NextStabilityAfterRecall(current.Stability, current.Difficulty, retrievability, grade);
            }
        }

        var intervalDays = NextIntervalDays(stability);

        return new ReviewResult(
            Stability: stability,
            Difficulty: difficulty,
            DueDate: now.Date.AddDays(intervalDays),
            IsKnown: grade != FlashcardReviewGrade.Again,
            IsUnknown: grade == FlashcardReviewGrade.Again);
    }

    /// <summary>A null due date means "new/never reviewed" - always due.</summary>
    public static bool IsDue(DateTime? dueDate, DateTime now)
        => dueDate is null || dueDate.Value.Date <= now.Date;

    private static double InitialStability(int rating)
        => Math.Max(MinStability, Parameters[rating - 1]);

    private static double InitialDifficulty(int rating)
        => Math.Clamp(UnclampedInitialDifficulty(rating), MinDifficulty, MaxDifficulty);

    private static double UnclampedInitialDifficulty(int rating)
        => Parameters[4] - (Math.Exp(Parameters[5] * (rating - 1)) - 1);

    /// <summary>Mean-reversion toward the (fixed, unclamped) Easy-rating baseline difficulty -
    /// not toward Good, despite that being how some secondary sources describe older FSRS versions.</summary>
    private static double NextDifficulty(double difficulty, int rating)
    {
        var easyBaseline = UnclampedInitialDifficulty(rating: 4);
        var deltaDifficulty = -(Parameters[6] * (rating - 3));
        var reverted = difficulty + (10.0 - difficulty) * deltaDifficulty / 9.0;
        var next = Parameters[7] * easyBaseline + (1 - Parameters[7]) * reverted;
        return Math.Clamp(next, MinDifficulty, MaxDifficulty);
    }

    private static double Retrievability(double elapsedDays, double stability)
        => Math.Pow(1 + Factor * elapsedDays / stability, Decay);

    private static double NextStabilityAfterRecall(double stability, double difficulty, double retrievability, FlashcardReviewGrade grade)
    {
        var hardPenalty = grade == FlashcardReviewGrade.Hard ? Parameters[15] : 1.0;
        var easyBonus = grade == FlashcardReviewGrade.Easy ? Parameters[16] : 1.0;

        var next = stability * (1
            + Math.Exp(Parameters[8])
            * (11 - difficulty)
            * Math.Pow(stability, -Parameters[9])
            * (Math.Exp((1 - retrievability) * Parameters[10]) - 1)
            * hardPenalty
            * easyBonus);

        return Math.Max(MinStability, next);
    }

    /// <summary>The min(...) floor here is a real FSRS-6 detail several secondary sources omit:
    /// stability after forgetting can't drop further than a fraction of its pre-forgetting value.</summary>
    private static double NextStabilityAfterForgetting(double stability, double difficulty, double retrievability)
    {
        var longTerm = Parameters[11]
            * Math.Pow(difficulty, -Parameters[12])
            * (Math.Pow(stability + 1, Parameters[13]) - 1)
            * Math.Exp((1 - retrievability) * Parameters[14]);

        var shortTermFloor = stability / Math.Exp(Parameters[17] * Parameters[18]);

        return Math.Max(MinStability, Math.Min(longTerm, shortTermFloor));
    }

    /// <summary>Hard/Good/Easy same-day reviews are floored at a 1.0x multiplier so stability never
    /// decreases within a sitting (verified: matches the reference test asserting a same-day Hard
    /// review after a Good does not decrease stability). Again has no such floor.</summary>
    private static double ShortTermStability(double stability, int rating)
    {
        var increase = Math.Exp(Parameters[17] * (rating - 3 + Parameters[18])) * Math.Pow(stability, -Parameters[19]);
        if (rating != 1)
            increase = Math.Max(increase, 1.0);

        return Math.Max(MinStability, stability * increase);
    }

    private static int NextIntervalDays(double stability)
    {
        var rawInterval = stability / Factor * (Math.Pow(DesiredRetention, 1.0 / Decay) - 1.0);
        var rounded = (int)Math.Round(rawInterval, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 1, MaximumIntervalDays);
    }
}
