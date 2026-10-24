using SmartLine.API.Domain.Inspection;

namespace SmartLine.API.Tests.Inspection;

public class TrackingAndSortingTests
{
    // Kalibrasyon testleri kendi modülü taşınırken ayrıca ele alınacak.
    private static readonly DateTime Start = new(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ProductMovesThroughTheHappyPath()
    {
        var state = ProductTracker.Create(Guid.NewGuid(), simulation: true, Start);
        ProductTracker.Advance(state, InspectionVocabulary.StageCaptured, Start.AddSeconds(1));
        ProductTracker.Advance(state, InspectionVocabulary.StageInspecting, Start.AddSeconds(2));
        ProductTracker.Advance(state, InspectionVocabulary.StageDecided, Start.AddSeconds(3));
        ProductTracker.Advance(state, InspectionVocabulary.StageQueued, Start.AddSeconds(4));
        ProductTracker.Advance(state, InspectionVocabulary.StageSorted, Start.AddSeconds(5));

        Assert.Equal(InspectionVocabulary.StageSorted, state.Stage);
        Assert.Equal(Start, state.DetectedAt);
        Assert.Equal(Start.AddSeconds(1), state.CapturedAt);
        Assert.Equal(Start.AddSeconds(2), state.InspectingAt);
        Assert.Equal(Start.AddSeconds(3), state.DecidedAt);
        Assert.Equal(Start.AddSeconds(4), state.QueuedAt);
        Assert.Equal(Start.AddSeconds(5), state.SortedAt);
        Assert.True(state.Simulation);
    }

    [Fact]
    public void IllegalAndTerminalTransitionsFail()
    {
        var state = ProductTracker.Create(Guid.NewGuid(), false, Start);
        Assert.Throws<InvalidTrackingTransitionException>(() =>
            ProductTracker.Advance(state, InspectionVocabulary.StageSorted, Start)
        );

        ProductTracker.Advance(state, InspectionVocabulary.StageFailed, Start.AddSeconds(1), "camera offline");
        Assert.Equal("camera offline", state.FailureReason);
        Assert.Throws<InvalidTrackingTransitionException>(() =>
            ProductTracker.Advance(state, InspectionVocabulary.StageDetected, Start.AddSeconds(2))
        );
    }

    [Fact]
    public void ManualReviewCanReturnToDecided()
    {
        var state = ProductTracker.Create(Guid.NewGuid(), true, Start);
        ProductTracker.Advance(state, InspectionVocabulary.StageCaptured, Start);
        ProductTracker.Advance(state, InspectionVocabulary.StageInspecting, Start);
        ProductTracker.Advance(state, InspectionVocabulary.StageManualReview, Start.AddSeconds(1), "operator");
        ProductTracker.Advance(state, InspectionVocabulary.StageDecided, Start.AddSeconds(2));
        Assert.Equal(InspectionVocabulary.StageDecided, state.Stage);
        Assert.NotNull(state.ManualReviewAt);
    }

    [Fact]
    public void SortDelayUsesBeltSpeedAndDistance()
    {
        Assert.Equal(5_000, SortingTiming.DelayMilliseconds(1_000, 200));
        Assert.Null(SortingTiming.DelayMilliseconds(1_000, 0));
        Assert.Null(SortingTiming.DelayMilliseconds(null, 200));
        Assert.Throws<ArgumentOutOfRangeException>(() => SortingTiming.DelayMilliseconds(-1, 200));
    }

    [Fact]
    public void QueueReleasesTheEarliestDueProductFirst()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var items = new[]
        {
            new QueuedSortItem(second, Start, Start.AddSeconds(8), "BIN_B", true),
            new QueuedSortItem(first, Start, Start.AddSeconds(2), "BIN_A", true),
            new QueuedSortItem(Guid.NewGuid(), Start, null, "BIN_C", true),
        };

        var ordered = SortingQueue.OrderPending(items);
        Assert.Equal(first, ordered[0].ProductId);
        Assert.Equal(second, ordered[1].ProductId);
        Assert.Null(ordered[2].SortDueAt);

        var due = SortingQueue.Due(items, Start.AddSeconds(3));
        Assert.Single(due);
        Assert.Equal(first, due[0].ProductId);
    }

    [Fact]
    public void ActuatorGuardNeverIssuesAHardwareCommand()
    {
        var disabled = SortingActuatorGuard.Release(hardwareActuationEnabled: false);
        var enabledButUnwired = SortingActuatorGuard.Release(hardwareActuationEnabled: true);

        Assert.False(disabled.HardwareCommandIssued);
        Assert.Equal("disabled", disabled.Actuation);
        Assert.False(enabledButUnwired.HardwareCommandIssued);
        Assert.Equal("not_wired", enabledButUnwired.Actuation);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(3, 1, 75.0)]
    [InlineData(0, 2, 0.0)]
    [InlineData(1, 2, 33.3)]
    public void YieldIgnoresAnEmptyDecidedPopulation(int passCount, int rejectCount, double? expected)
    {
        Assert.Equal(expected, ProductionMath.YieldPercent(passCount, rejectCount));
    }

}
