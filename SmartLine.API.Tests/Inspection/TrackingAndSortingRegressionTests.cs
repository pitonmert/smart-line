using SmartLine.API.Domain.Inspection;

namespace SmartLine.API.Tests.Inspection;

public class TrackingAndSortingRegressionTests
{
    private static readonly DateTime Start = new(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteDistanceIsRejected(double distance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SortingTiming.DelayMilliseconds(distance, 200)
        );
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    public void InvalidSpeedLeavesTimingUnknown(double speed)
    {
        Assert.Null(SortingTiming.DelayMilliseconds(1000, speed));
    }

    [Fact]
    public void OverflowingDelayIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SortingTiming.DelayMilliseconds(1000, double.Epsilon)
        );
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidDelayCannotProduceADueDate(double delay)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SortingTiming.SortDueAt(Start, delay));
    }

    [Fact]
    public void TimingSupportsExactBoundsZeroAndMissingValues()
    {
        Assert.Equal(4000d, SortingTiming.DelayMilliseconds(20_000, 5000));
        Assert.Equal(0d, SortingTiming.DelayMilliseconds(0, 200));
        Assert.Null(SortingTiming.SortDueAt(Start, null));
        Assert.Equal(Start, SortingTiming.SortDueAt(Start, 0));
        Assert.Equal(Start.AddSeconds(5), SortingTiming.SortDueAt(Start, 5000));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SortingTiming.SortDueAt(DateTime.MaxValue, 1)
        );
    }

    [Fact]
    public void LargeProductionCountsDoNotOverflow()
    {
        Assert.Equal(50d, ProductionMath.YieldPercent(int.MaxValue, int.MaxValue));
        Assert.Equal(100d, ProductionMath.YieldPercent(int.MaxValue, 0));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void NegativeProductionCountsAreRejected(int passCount, int rejectCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductionMath.YieldPercent(passCount, rejectCount)
        );
    }

    [Fact]
    public void BackwardsTransitionDoesNotMutateTheTrack()
    {
        var state = ProductTracker.Create(Guid.NewGuid(), false, Start);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductTracker.Advance(state, InspectionVocabulary.StageCaptured, Start.AddSeconds(-1))
        );
        Assert.Equal(InspectionVocabulary.StageDetected, state.Stage);
        Assert.Null(state.CapturedAt);
    }

    [Fact]
    public void ReturningFromManualReviewClearsTheReasonAndUpdatesDecisionTime()
    {
        var state = ProductTracker.Create(Guid.NewGuid(), false, Start);
        ProductTracker.Advance(state, InspectionVocabulary.StageCaptured, Start);
        ProductTracker.Advance(state, InspectionVocabulary.StageInspecting, Start);
        ProductTracker.Advance(state, InspectionVocabulary.StageDecided, Start.AddSeconds(1));
        ProductTracker.Advance(
            state,
            InspectionVocabulary.StageManualReview,
            Start.AddSeconds(2),
            "operator review"
        );
        ProductTracker.Advance(state, InspectionVocabulary.StageDecided, Start.AddSeconds(3));
        Assert.Equal(Start.AddSeconds(3), state.DecidedAt);
        Assert.Null(state.FailureReason);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductTracker.Advance(state, InspectionVocabulary.StageQueued, Start.AddSeconds(2.5))
        );

        ProductTracker.Advance(
            state,
            InspectionVocabulary.StageManualReview,
            Start.AddSeconds(4),
            "second review"
        );
        Assert.Equal(Start.AddSeconds(4), state.ManualReviewAt);
    }

    [Fact]
    public void QueueIncludesExactDueTimeAndKeepsUnknownTimesPending()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var items = new[]
        {
            new QueuedSortItem(second, Start, Start, "BIN_B", false),
            new QueuedSortItem(first, Start, Start, "BIN_A", true),
            new QueuedSortItem(Guid.NewGuid(), Start, null, "BIN_C", true),
        };
        var due = SortingQueue.Due(items, Start);
        Assert.Equal(new[] { first, second }, due.Select(item => item.ProductId));
        Assert.True(due[0].Simulation);
        Assert.False(due[1].Simulation);
        Assert.Empty(SortingQueue.Due(items, Start.AddTicks(-1)));
    }
}
