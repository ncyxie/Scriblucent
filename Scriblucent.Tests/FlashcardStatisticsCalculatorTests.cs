using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public class FlashcardStatisticsCalculatorTests
{
    private static readonly DateTime Anchor = new(2026, 1, 15);

    [Fact]
    public void ClassifyMaturity_NeverReviewed_ReturnsNew()
    {
        Assert.Equal(CardMaturity.New, FlashcardStatisticsCalculator.ClassifyMaturity(null, null));
        Assert.Equal(CardMaturity.New, FlashcardStatisticsCalculator.ClassifyMaturity(Anchor, null));
    }

    [Fact]
    public void ClassifyMaturity_IntervalBelowThreshold_ReturnsLearning()
    {
        var maturity = FlashcardStatisticsCalculator.ClassifyMaturity(Anchor.AddDays(20), Anchor);
        Assert.Equal(CardMaturity.Learning, maturity);
    }

    [Fact]
    public void ClassifyMaturity_IntervalAtThreshold_ReturnsMature()
    {
        var maturity = FlashcardStatisticsCalculator.ClassifyMaturity(Anchor.AddDays(21), Anchor);
        Assert.Equal(CardMaturity.Mature, maturity);
    }

    [Fact]
    public void Calculate_ForecastDayZero_IncludesOverdueAndNewCards()
    {
        var cards = new[]
        {
            new FlashcardItem { DueDate = Anchor.AddDays(-3), LastReviewedAt = Anchor.AddDays(-10) }, // overdue
            new FlashcardItem { DueDate = null, LastReviewedAt = null },                              // never reviewed
            new FlashcardItem { DueDate = Anchor.AddDays(1), LastReviewedAt = Anchor.AddDays(-5) }     // due tomorrow, not today
        };

        var snapshot = FlashcardStatisticsCalculator.Calculate(cards, SpacedRepetitionAlgorithm.Sm2, Anchor);

        Assert.Equal("Today", snapshot.Forecast[0].Label);
        Assert.Equal(2, snapshot.Forecast[0].Count);
        Assert.Equal(2, snapshot.DueTodayCount);
        Assert.Equal(1, snapshot.Forecast[1].Count);
    }

    [Fact]
    public void Calculate_DifficultySignalBuckets_ExcludeNeverReviewedCards()
    {
        var cards = new[]
        {
            new FlashcardItem { LastReviewedAt = Anchor.AddDays(-1), DueDate = Anchor.AddDays(5), EaseFactor = 2.5 },
            new FlashcardItem { LastReviewedAt = null, DueDate = null, EaseFactor = 2.5 } // never reviewed - excluded
        };

        var snapshot = FlashcardStatisticsCalculator.Calculate(cards, SpacedRepetitionAlgorithm.Sm2, Anchor);

        Assert.Equal(1, snapshot.DifficultySignalBuckets.Sum(bucket => bucket.Count));
        Assert.Equal("Ease factor", snapshot.DifficultySignalLabel);
    }

    [Fact]
    public void Calculate_Fsrs6_UsesDifficultyAsTheSignal()
    {
        var cards = new[]
        {
            new FlashcardItem { LastReviewedAt = Anchor.AddDays(-1), DueDate = Anchor.AddDays(5), Difficulty = 7 }
        };

        var snapshot = FlashcardStatisticsCalculator.Calculate(cards, SpacedRepetitionAlgorithm.Fsrs6, Anchor);

        Assert.Equal("Difficulty", snapshot.DifficultySignalLabel);
        var bucket = Assert.Single(snapshot.DifficultySignalBuckets, b => b.Label == "6-8");
        Assert.Equal(1, bucket.Count);
    }

    [Fact]
    public void Calculate_IntervalBucket_ExactlySevenDays_FallsInSecondBucket()
    {
        var cards = new[]
        {
            new FlashcardItem { LastReviewedAt = Anchor, DueDate = Anchor.AddDays(7) }
        };

        var snapshot = FlashcardStatisticsCalculator.Calculate(cards, SpacedRepetitionAlgorithm.Sm2, Anchor);

        Assert.Equal(0, snapshot.IntervalBuckets.Single(b => b.Label == "<7d").Count);
        Assert.Equal(1, snapshot.IntervalBuckets.Single(b => b.Label == "7-20d").Count);
    }

    [Fact]
    public void Calculate_EmptySet_ReturnsZeroedSnapshotWithoutThrowing()
    {
        var snapshot = FlashcardStatisticsCalculator.Calculate(Array.Empty<FlashcardItem>(), SpacedRepetitionAlgorithm.Sm2, Anchor);

        Assert.Equal(0, snapshot.TotalCount);
        Assert.Equal(0, snapshot.DueTodayCount);
        Assert.Equal(14, snapshot.Forecast.Count);
        Assert.All(snapshot.IntervalBuckets, bucket => Assert.Equal(0, bucket.Count));
    }
}
