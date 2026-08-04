using Scriblucent.Services;

namespace Scriblucent.Tests;

/// <summary>
/// Known-answer tests ported from the reference FSRS-6 implementation's own test suite
/// (open-spaced-repetition/py-fsrs, tests/test_basic.py), default parameters, no fuzz. These check
/// FsrsScheduler against the actual numbers the official implementation produces for the same
/// inputs, not just a second read-through of the formulas.
/// </summary>
public class FsrsSchedulerTests
{
    private static readonly DateTime Anchor = new(2022, 11, 29, 12, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void AgainThenFiveGoods_MatchesReferenceStabilityAndDifficulty()
    {
        var ratings = new[]
        {
            FlashcardReviewGrade.Again,
            FlashcardReviewGrade.Good,
            FlashcardReviewGrade.Good,
            FlashcardReviewGrade.Good,
            FlashcardReviewGrade.Good,
            FlashcardReviewGrade.Good
        };

        // Gap (in days) immediately before each review, relative to the previous one. The second
        // review lands the same day as the first (same-day/short-term path), matching the
        // reference test's own review timing.
        var gapsBeforeEachReview = new[] { 0, 0, 1, 3, 8, 21 };

        var reviewTime = Anchor;
        var state = new FsrsScheduler.ReviewState(Stability: 0, Difficulty: 0, LastReviewedAt: null);
        FsrsScheduler.ReviewResult result = default;

        for (var i = 0; i < ratings.Length; i++)
        {
            reviewTime = i == 0 ? Anchor : reviewTime.AddDays(gapsBeforeEachReview[i]);
            result = FsrsScheduler.ApplyGrade(state, ratings[i], reviewTime);
            state = new FsrsScheduler.ReviewState(result.Stability, result.Difficulty, reviewTime);
        }

        Assert.Equal(53.62691, result.Stability, 4);
        Assert.Equal(6.3574867, result.Difficulty, 4);
    }

    [Fact]
    public void SameDayHardReview_DoesNotDecreaseStabilityFromPriorGood()
    {
        var afterGood = FsrsScheduler.ApplyGrade(
            new FsrsScheduler.ReviewState(0, 0, null),
            FlashcardReviewGrade.Good,
            Anchor);

        var afterHardSameDay = FsrsScheduler.ApplyGrade(
            new FsrsScheduler.ReviewState(afterGood.Stability, afterGood.Difficulty, Anchor),
            FlashcardReviewGrade.Hard,
            Anchor);

        Assert.True(afterHardSameDay.Stability >= afterGood.Stability);
    }

    [Fact]
    public void RepeatedEasyReviews_DifficultyClampsToOne()
    {
        var state = new FsrsScheduler.ReviewState(0, 0, null);
        FsrsScheduler.ReviewResult result = default;
        var reviewTime = Anchor;

        for (var i = 0; i < 10; i++)
        {
            result = FsrsScheduler.ApplyGrade(state, FlashcardReviewGrade.Easy, reviewTime);
            state = new FsrsScheduler.ReviewState(result.Stability, result.Difficulty, reviewTime);
            reviewTime = reviewTime.AddSeconds(1);
        }

        Assert.Equal(1.0, result.Difficulty);
    }

    [Theory]
    [InlineData(FlashcardReviewGrade.Again, false, true)]
    [InlineData(FlashcardReviewGrade.Hard, true, false)]
    [InlineData(FlashcardReviewGrade.Good, true, false)]
    [InlineData(FlashcardReviewGrade.Easy, true, false)]
    public void ApplyGrade_SetsKnownUnknownFlagsFromRating(FlashcardReviewGrade grade, bool expectedKnown, bool expectedUnknown)
    {
        var result = FsrsScheduler.ApplyGrade(new FsrsScheduler.ReviewState(0, 0, null), grade, Anchor);

        Assert.Equal(expectedKnown, result.IsKnown);
        Assert.Equal(expectedUnknown, result.IsUnknown);
    }

    [Fact]
    public void ApplyGrade_NeverProducesASameDayDueDate()
    {
        var result = FsrsScheduler.ApplyGrade(new FsrsScheduler.ReviewState(0, 0, null), FlashcardReviewGrade.Again, Anchor);

        Assert.True(result.DueDate >= Anchor.Date.AddDays(1));
    }

    [Fact]
    public void ApplyGrade_ClampsIntervalToMaximum()
    {
        var lastReviewedAt = Anchor.AddDays(-30);
        var hugeStabilityState = new FsrsScheduler.ReviewState(Stability: 1_000_000_000, Difficulty: 5, LastReviewedAt: lastReviewedAt);

        var result = FsrsScheduler.ApplyGrade(hugeStabilityState, FlashcardReviewGrade.Good, Anchor);

        Assert.Equal(Anchor.Date.AddDays(36500), result.DueDate);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void IsDue_ComparesDueDateAgainstNow(int? dueDateOffsetDays, bool expectedDue)
    {
        var now = Anchor;
        DateTime? dueDate = dueDateOffsetDays is null ? null : now.Date.AddDays(dueDateOffsetDays.Value);

        Assert.Equal(expectedDue, FsrsScheduler.IsDue(dueDate, now));
    }
}
