using System.Globalization;
using Scriblucent.Models;

namespace Scriblucent.Services;

public enum CardMaturity
{
    New,
    Learning,
    Mature
}

public readonly record struct BucketCount(string Label, int Count);

public readonly record struct ForecastDay(DateTime Date, string Label, int Count);

public sealed record FlashcardStatisticsSnapshot(
    int TotalCount,
    int DueTodayCount,
    int NewCount,
    int LearningCount,
    int MatureCount,
    IReadOnlyList<ForecastDay> Forecast,
    IReadOnlyList<BucketCount> IntervalBuckets,
    string DifficultySignalLabel,
    IReadOnlyList<BucketCount> DifficultySignalBuckets);

/// <summary>
/// Pure, log-free flashcard statistics - everything derived from each card's current stored state
/// (DueDate/LastReviewedAt/EaseFactor/Stability/Difficulty), never from a review history log this
/// app doesn't keep. Mirrors FlashcardReviewScheduler/FsrsScheduler's stateless style so it stays
/// independently testable.
/// </summary>
public static class FlashcardStatisticsCalculator
{
    private const int MatureIntervalDays = 21; // matches Anki's own young/mature threshold
    private const int ForecastDayCount = 14;

    private static readonly string[] IntervalBucketLabels = { "<7d", "7-20d", "21-89d", "90-364d", "365d+" };
    private static readonly string[] EaseBucketLabels = { "<1.8", "1.8-2.2", "2.2-2.6", "2.6-3.0", "3.0+" };
    private static readonly string[] DifficultyBucketLabels = { "1-2", "2-4", "4-6", "6-8", "8-10" };

    /// <summary>New if never reviewed; otherwise Mature once the last-applied interval reaches
    /// MatureIntervalDays, else Learning. Algorithm-agnostic - both schedulers populate DueDate/
    /// LastReviewedAt the same way, so this works identically for SM-2 and FSRS cards.</summary>
    public static CardMaturity ClassifyMaturity(DateTime? dueDate, DateTime? lastReviewedAt)
    {
        if (lastReviewedAt is null || dueDate is null)
            return CardMaturity.New;

        var intervalDays = (dueDate.Value.Date - lastReviewedAt.Value.Date).Days;
        return intervalDays >= MatureIntervalDays ? CardMaturity.Mature : CardMaturity.Learning;
    }

    /// <summary>Mirrors FlashcardReviewScheduler.IsDue/FsrsScheduler.IsDue exactly (a null or
    /// past-or-present due date counts as due), inlined so this file has no dependency on either
    /// scheduler. Overdue and New cards both fold into "today", matching Anki's own forecast.</summary>
    public static bool IsDueOrOverdue(DateTime? dueDate, DateTime now)
        => dueDate is null || dueDate.Value.Date <= now.Date;

    public static FlashcardStatisticsSnapshot Calculate(IEnumerable<FlashcardItem> cards, SpacedRepetitionAlgorithm algorithm, DateTime now)
    {
        var cardList = cards as IReadOnlyCollection<FlashcardItem> ?? cards.ToList();

        var newCount = 0;
        var learningCount = 0;
        var matureCount = 0;
        foreach (var card in cardList)
        {
            switch (ClassifyMaturity(card.DueDate, card.LastReviewedAt))
            {
                case CardMaturity.New: newCount++; break;
                case CardMaturity.Learning: learningCount++; break;
                case CardMaturity.Mature: matureCount++; break;
            }
        }

        var (difficultyLabel, difficultyBuckets) = BuildDifficultySignalBuckets(cardList, algorithm);

        return new FlashcardStatisticsSnapshot(
            TotalCount: cardList.Count,
            DueTodayCount: cardList.Count(c => IsDueOrOverdue(c.DueDate, now)),
            NewCount: newCount,
            LearningCount: learningCount,
            MatureCount: matureCount,
            Forecast: BuildForecast(cardList, now),
            IntervalBuckets: BuildIntervalBuckets(cardList),
            DifficultySignalLabel: difficultyLabel,
            DifficultySignalBuckets: difficultyBuckets);
    }

    private static IReadOnlyList<ForecastDay> BuildForecast(IReadOnlyCollection<FlashcardItem> cards, DateTime now)
    {
        var today = now.Date;
        var days = new List<ForecastDay>(ForecastDayCount)
        {
            new(today, "Today", cards.Count(c => IsDueOrOverdue(c.DueDate, now)))
        };

        for (var offset = 1; offset < ForecastDayCount; offset++)
        {
            var date = today.AddDays(offset);
            var label = offset == 1 ? "Tomorrow" : date.ToString("ddd", CultureInfo.InvariantCulture);
            days.Add(new ForecastDay(date, label, cards.Count(c => c.DueDate?.Date == date)));
        }

        return days;
    }

    private static IReadOnlyList<BucketCount> BuildIntervalBuckets(IReadOnlyCollection<FlashcardItem> cards)
    {
        var counts = new int[IntervalBucketLabels.Length];

        foreach (var card in cards)
        {
            if (card.LastReviewedAt is null || card.DueDate is null)
                continue;

            var intervalDays = (card.DueDate.Value.Date - card.LastReviewedAt.Value.Date).Days;
            counts[ClassifyIntervalBucketIndex(intervalDays)]++;
        }

        return BuildBucketList(IntervalBucketLabels, counts);
    }

    private static (string Label, IReadOnlyList<BucketCount> Buckets) BuildDifficultySignalBuckets(
        IReadOnlyCollection<FlashcardItem> cards, SpacedRepetitionAlgorithm algorithm)
    {
        var reviewed = cards.Where(c => c.LastReviewedAt is not null);

        if (algorithm == SpacedRepetitionAlgorithm.Fsrs6)
        {
            var counts = new int[DifficultyBucketLabels.Length];
            foreach (var card in reviewed)
                counts[ClassifyDifficultyBucketIndex(card.Difficulty)]++;
            return ("Difficulty", BuildBucketList(DifficultyBucketLabels, counts));
        }

        var easeCounts = new int[EaseBucketLabels.Length];
        foreach (var card in reviewed)
            easeCounts[ClassifyEaseBucketIndex(card.EaseFactor)]++;
        return ("Ease factor", BuildBucketList(EaseBucketLabels, easeCounts));
    }

    private static IReadOnlyList<BucketCount> BuildBucketList(IReadOnlyList<string> labels, IReadOnlyList<int> counts)
    {
        var result = new List<BucketCount>(labels.Count);
        for (var i = 0; i < labels.Count; i++)
            result.Add(new BucketCount(labels[i], counts[i]));
        return result;
    }

    private static int ClassifyIntervalBucketIndex(int days)
    {
        if (days < 7) return 0;
        if (days < 21) return 1;
        if (days < 90) return 2;
        if (days < 365) return 3;
        return 4;
    }

    private static int ClassifyEaseBucketIndex(double ease)
    {
        if (ease < 1.8) return 0;
        if (ease < 2.2) return 1;
        if (ease < 2.6) return 2;
        if (ease < 3.0) return 3;
        return 4;
    }

    private static int ClassifyDifficultyBucketIndex(double difficulty)
    {
        if (difficulty < 2) return 0;
        if (difficulty < 4) return 1;
        if (difficulty < 6) return 2;
        if (difficulty < 8) return 3;
        return 4;
    }
}
