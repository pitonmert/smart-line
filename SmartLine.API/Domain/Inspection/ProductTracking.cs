namespace SmartLine.API.Domain.Inspection;

public sealed class InvalidTrackingTransitionException(string from, string to)
    : Exception($"Invalid product tracking transition {from} -> {to}.")
{
    public string From { get; } = from;
    public string To { get; } = to;
}

public sealed class ProductTrackState
{
    public Guid ProductId { get; set; }
    public string Stage { get; set; } = InspectionVocabulary.StageDetected;
    public bool Simulation { get; set; }
    public DateTime DetectedAt { get; set; }
    public DateTime? CapturedAt { get; set; }
    public DateTime? InspectingAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime? QueuedAt { get; set; }
    public DateTime? SortedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? ManualReviewAt { get; set; }
    public DateTime? SortDueAt { get; set; }
    public string? DestinationBin { get; set; }
    public string? Decision { get; set; }
    public string? FailureReason { get; set; }
}

public static class ProductTracker
{
    private static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<
        string,
        string[]
    >(StringComparer.Ordinal)
    {
        [InspectionVocabulary.StageDetected] =
        [
            InspectionVocabulary.StageCaptured,
            InspectionVocabulary.StageFailed,
        ],
        [InspectionVocabulary.StageCaptured] =
        [
            InspectionVocabulary.StageInspecting,
            InspectionVocabulary.StageFailed,
        ],
        [InspectionVocabulary.StageInspecting] =
        [
            InspectionVocabulary.StageDecided,
            InspectionVocabulary.StageManualReview,
            InspectionVocabulary.StageFailed,
        ],
        [InspectionVocabulary.StageDecided] =
        [
            InspectionVocabulary.StageQueued,
            InspectionVocabulary.StageManualReview,
            InspectionVocabulary.StageFailed,
        ],
        [InspectionVocabulary.StageQueued] =
        [
            InspectionVocabulary.StageSorted,
            InspectionVocabulary.StageFailed,
        ],
        [InspectionVocabulary.StageManualReview] =
        [
            InspectionVocabulary.StageDecided,
            InspectionVocabulary.StageFailed,
        ],
        [InspectionVocabulary.StageSorted] = [],
        [InspectionVocabulary.StageFailed] = [],
    };

    public static ProductTrackState Create(Guid productId, bool simulation, DateTime utcNow) =>
        new()
        {
            ProductId = productId,
            Stage = InspectionVocabulary.StageDetected,
            Simulation = simulation,
            DetectedAt = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
        };

    public static void Advance(
        ProductTrackState state,
        string nextStage,
        DateTime utcNow,
        string? reason = null
    )
    {
        if (
            !Allowed.TryGetValue(state.Stage, out var next)
            || !next.Contains(nextStage, StringComparer.Ordinal)
        )
            throw new InvalidTrackingTransitionException(state.Stage, nextStage);

        var timestamp = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        // Yeni aşama, ürünün daha önce kaydedilmiş bir aşamasından önce olamaz.
        var latest = new DateTime?[]
        {
            state.DetectedAt,
            state.CapturedAt,
            state.InspectingAt,
            state.DecidedAt,
            state.QueuedAt,
            state.SortedAt,
            state.FailedAt,
            state.ManualReviewAt,
        }.Max();
        if (timestamp < latest)
            throw new ArgumentOutOfRangeException(
                nameof(utcNow),
                "Tracking time must not move backwards."
            );
        state.Stage = nextStage;
        // İnsan kontrolü tamamlandıktan sonra eski bekleme gerekçesi aktif hata gibi kalmasın.
        if (nextStage == InspectionVocabulary.StageDecided)
            state.FailureReason = null;
        switch (nextStage)
        {
            case InspectionVocabulary.StageCaptured:
                state.CapturedAt ??= timestamp;
                break;
            case InspectionVocabulary.StageInspecting:
                state.InspectingAt ??= timestamp;
                break;
            case InspectionVocabulary.StageDecided:
                // İnsan kontrolünden sonra yeniden karar verilebilir; son karar zamanı saklanır.
                state.DecidedAt = timestamp;
                break;
            case InspectionVocabulary.StageQueued:
                state.QueuedAt ??= timestamp;
                break;
            case InspectionVocabulary.StageSorted:
                state.SortedAt ??= timestamp;
                break;
            case InspectionVocabulary.StageFailed:
                state.FailedAt ??= timestamp;
                state.FailureReason = reason;
                break;
            case InspectionVocabulary.StageManualReview:
                state.ManualReviewAt = timestamp;
                state.FailureReason = reason;
                break;
        }
    }

    public static bool IsTerminal(string stage) =>
        stage is InspectionVocabulary.StageSorted or InspectionVocabulary.StageFailed;
}
