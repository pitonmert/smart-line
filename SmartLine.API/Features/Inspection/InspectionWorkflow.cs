using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Domain.Enums;
using SmartLine.API.Domain.Inspection;
using SmartLine.API.Features.Monitoring;
using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Infrastructure.Realtime;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Inspection;

public sealed record TrackResponse(
    Guid ProductId,
    string PublicCode,
    Guid? RecipeId,
    Guid? InspectionId,
    string Stage,
    bool Simulation,
    string? DestinationBin,
    string? Decision,
    string? FailureReason,
    DateTime DetectedAt,
    DateTime? CapturedAt,
    DateTime? InspectingAt,
    DateTime? DecidedAt,
    DateTime? QueuedAt,
    DateTime? SortedAt,
    DateTime? FailedAt,
    DateTime? ManualReviewAt,
    DateTime? SortDueAt
);

public sealed record CompletedInspection(
    InspectionResult Inspection,
    TrackResponse Track,
    SortingActuationResult Actuation
);

public sealed class SimulationLatch
{
    private int _latched;

    public bool IsLatched => Volatile.Read(ref _latched) == 1;

    public void Latch() => Volatile.Write(ref _latched, 1);

    public void Clear() => Volatile.Write(ref _latched, 0);
}

public sealed class InspectionWorkflow(
    ApplicationDbContext db,
    LogHandler logs,
    IHubContext<DashboardHub> hub,
    IOptions<SortingOptions> sortingOptions,
    ILogger<InspectionWorkflow> logger
)
{
    public async Task<CompletedInspection> SubmitAsync(
        InspectionObservation observation,
        Guid? recipeId,
        Guid? existingProductId,
        double? simulatedDistanceMm,
        double? simulatedBeltSpeedMmPerSecond,
        CancellationToken ct,
        int? expectedRecipeVersion = null,
        Guid? requestedInspectionId = null,
        string? expectedCalibrationJson = null,
        Guid? expectedCalibrationId = null,
        int? expectedCalibrationVersion = null
    )
    {
        if (
            string.Equals(
                observation.TriggerSource,
                InspectionVocabulary.TriggerHardware,
                StringComparison.OrdinalIgnoreCase
            ) && !observation.Simulation
        )
        {
            throw new ValidationException(
                "VL53L1X hardware trigger is not integrated. Use a manual, API, or simulator trigger."
            );
        }

        if (
            string.Equals(
                observation.TriggerSource,
                InspectionVocabulary.TriggerSimulator,
                StringComparison.OrdinalIgnoreCase
            )
        )
            observation.Simulation = true;

        await using var transaction = await InspectionTransaction.BeginAsync(db, ct);
        var recipe = await LoadRecipeAsync(recipeId, ct);
        var definition = InspectionJson.Deserialize<ProductRecipeDefinition>(recipe.DefinitionJson);
        var selectedCalibration = await db
            .CalibrationProfiles.AsNoTracking()
            .SingleOrDefaultAsync(
                p =>
                    definition.CalibrationProfileId == null
                        ? p.IsActive
                        : p.Id == definition.CalibrationProfileId,
                ct
            );
        if (definition.CalibrationProfileId is not null && selectedCalibration is null)
            throw new NotFoundException("Calibration profile");
        if (observation.ObservationSource == "model")
        {
            if (
                !recipe.IsActive
                || selectedCalibration?.DefinitionJson != expectedCalibrationJson
                || selectedCalibration?.Id != expectedCalibrationId
                || selectedCalibration?.Version != expectedCalibrationVersion
            )
                throw new ApiException(
                    409,
                    ErrorCodes.RecipeInvalid,
                    "Active recipe or calibration changed. Inspect again."
                );
        }
        if (expectedRecipeVersion is not null && recipe.Version != expectedRecipeVersion)
            throw new ApiException(
                409,
                ErrorCodes.RecipeInvalid,
                "Recipe changed. Inspect again using the current version."
            );
        if (observation.ObservationSource is not ("manual" or "model" or "simulation"))
            throw new ValidationException("Observation source is invalid.");
        if (observation.CapturedAt is { Kind: DateTimeKind.Local } captured)
            observation.CapturedAt = captured.ToUniversalTime();
        if (
            observation.ProcessingLatencyMs < 0
            || observation.ModelName?.Length > 80
            || observation.ModelVersion?.Length > 80
            || observation.ImageReference?.Length > 300
        )
            throw new ValidationException("Observation metadata is invalid.");
        if (
            observation.BoundingBoxes?.Any(box =>
                box is null
                || !double.IsFinite(box.X)
                || !double.IsFinite(box.Y)
                || !double.IsFinite(box.Width)
                || !double.IsFinite(box.Height)
                || box.X < 0
                || box.Y < 0
                || box.Width <= 0
                || box.Height <= 0
            ) == true
        )
            throw new ValidationException("Bounding boxes must have finite positive dimensions.");
        if (observation.CapturedAt > DateTime.UtcNow.AddSeconds(5))
            throw new ValidationException("Capture time cannot be in the future.");
        var recipeErrors = RecipeValidator.Validate(definition);
        if (recipeErrors.Count > 0)
            throw new ValidationException(string.Join(' ', recipeErrors), ErrorCodes.RecipeInvalid);

        ProductTrack? existing = null;
        if (existingProductId is not null)
        {
            existing = await db.ProductTracks.FirstOrDefaultAsync(
                track => track.Id == existingProductId,
                ct
            );
            if (existing is null)
                throw new NotFoundException("Product", existingProductId);
            if (existing.IsSimulation)
                observation.Simulation = true;
            else if (observation.Simulation)
                throw new ValidationException(
                    "A real product cannot accept a simulated inspection."
                );
        }

        DecisionResult decision;
        var started = Stopwatch.StartNew();
        try
        {
            decision = DecisionEngine.Decide(definition, observation);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ValidationException(exception.Message);
        }

        var now = DateTime.UtcNow;
        var track = existing ?? CreateTrack(decision.Simulation, now);
        if (existing is null)
        {
            db.ProductTracks.Add(track);
            var created = ToState(track);
            ProductTracker.Advance(created, InspectionVocabulary.StageCaptured, now);
            ProductTracker.Advance(created, InspectionVocabulary.StageInspecting, now);
            Copy(track, created);
        }
        else
        {
            var current = ToState(track);
            try
            {
                if (current.Stage == InspectionVocabulary.StageDetected)
                    ProductTracker.Advance(current, InspectionVocabulary.StageCaptured, now);
                if (current.Stage == InspectionVocabulary.StageCaptured)
                    ProductTracker.Advance(current, InspectionVocabulary.StageInspecting, now);
                if (current.Stage != InspectionVocabulary.StageInspecting)
                    throw new InvalidTrackingTransitionException(
                        current.Stage,
                        InspectionVocabulary.StageInspecting
                    );
            }
            catch (InvalidTrackingTransitionException exception)
            {
                throw new ValidationException(
                    exception.Message,
                    ErrorCodes.TrackingTransitionInvalid
                );
            }

            Copy(track, current);
        }

        started.Stop();
        var latency =
            observation.ObservationSource == "model" ? observation.ProcessingLatencyMs : null;
        var inspectionId = requestedInspectionId ?? Guid.NewGuid();
        var result = InspectionResultFactory.Create(
            inspectionId,
            track.Id,
            recipe.Id,
            definition,
            observation,
            decision,
            observation.CapturedAt ?? now,
            now,
            latency,
            recipe.Version
        );
        result = result with
        {
            CalibrationProfileId = selectedCalibration?.Id,
            CalibrationVersion = selectedCalibration?.Version,
        };
        if (result.Simulation && !result.Reasons.Contains("SIMULATION"))
            result = result with { Reasons = result.Reasons.Append("SIMULATION").ToArray() };

        db.InspectionRecords.Add(ToRecord(result));
        var decided = ToState(track);
        try
        {
            ProductTracker.Advance(decided, InspectionVocabulary.StageDecided, now);
            decided.DestinationBin = result.DestinationBin;
            decided.Decision = result.Decision;
            if (result.Decision == InspectionVocabulary.ManualReview)
            {
                ProductTracker.Advance(
                    decided,
                    InspectionVocabulary.StageManualReview,
                    now,
                    "Recipe decision requires manual review."
                );
            }
            else
            {
                ProductTracker.Advance(decided, InspectionVocabulary.StageQueued, now);
                var calibration = await ActiveCalibrationAsync(definition.CalibrationProfileId, ct);
                var distance = decision.Simulation
                    ? simulatedDistanceMm ?? calibration?.InspectionToSorterDistanceMm
                    : calibration?.InspectionToSorterDistanceMm;
                var speed = decision.Simulation
                    ? simulatedBeltSpeedMmPerSecond ?? calibration?.BeltSpeedMmPerSecond
                    : calibration?.BeltSpeedMmPerSecond;
                decided.SortDueAt = SortingTiming.SortDueAt(
                    now,
                    SortingTiming.DelayMilliseconds(distance, speed)
                );
            }
        }
        catch (InvalidTrackingTransitionException exception)
        {
            throw new ValidationException(exception.Message, ErrorCodes.TrackingTransitionInvalid);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ValidationException(exception.Message, ErrorCodes.CalibrationInvalid);
        }

        Copy(track, decided);
        track.RecipeId = recipe.Id;
        track.InspectionId = inspectionId;
        var log = await logs.StageAsync(
            result.Simulation ? EventType.SIMULATION_ARRIVAL : EventType.INSPECTION_RECORDED,
            result.Simulation
                ? $"[SIMULATION] {track.PublicCode} {result.Decision} -> {result.DestinationBin}"
                : $"{track.PublicCode} {result.Decision} -> {result.DestinationBin}",
            "Info",
            ct
        );
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await logs.NotifyAsync(log, ct);
        await PublishAsync(result, ToResponse(track), ct);
        return new CompletedInspection(result, ToResponse(track), Actuation());
    }

    public async Task<TrackResponse> ReleaseAsync(Guid productId, CancellationToken ct)
    {
        await using var transaction = await InspectionTransaction.BeginAsync(db, ct);
        var track = await db.ProductTracks.FirstOrDefaultAsync(item => item.Id == productId, ct);
        if (track is null)
            throw new NotFoundException("Product", productId);
        var state = ToState(track);
        try
        {
            ProductTracker.Advance(state, InspectionVocabulary.StageSorted, DateTime.UtcNow);
        }
        catch (InvalidTrackingTransitionException exception)
        {
            throw new ValidationException(exception.Message, ErrorCodes.TrackingTransitionInvalid);
        }

        Copy(track, state);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var response = ToResponse(track);
        await PublishTrackAsync(response, ct);
        return response;
    }

    public async Task<int> AdvanceDueSimulatedAsync(DateTime utcNow, CancellationToken ct)
    {
        await using var transaction = await InspectionTransaction.BeginAsync(db, ct);
        var due = await db
            .ProductTracks.Where(track =>
                track.IsSimulation
                && track.Stage == InspectionVocabulary.StageQueued
                && track.SortDueAt != null
                && track.SortDueAt <= utcNow
            )
            .ToListAsync(ct);
        foreach (var track in due)
        {
            var state = ToState(track);
            ProductTracker.Advance(state, InspectionVocabulary.StageSorted, utcNow);
            Copy(track, state);
        }

        if (due.Count > 0)
            await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        foreach (var track in due)
            await PublishTrackAsync(ToResponse(track), ct);
        return due.Count;
    }

    public SortingActuationResult Actuation() =>
        SortingActuatorGuard.Release(sortingOptions.Value.HardwareActuationEnabled);

    private async Task<ProductRecipe> LoadRecipeAsync(Guid? recipeId, CancellationToken ct)
    {
        var recipe = recipeId is null
            ? await db.ProductRecipes.FirstOrDefaultAsync(item => item.IsActive, ct)
            : await db.ProductRecipes.FirstOrDefaultAsync(item => item.Id == recipeId, ct);
        if (recipe is null)
            throw new NotFoundException(
                recipeId is null ? "Active product recipe" : "Product recipe"
            );
        return recipe;
    }

    private async Task<CalibrationDefinition?> ActiveCalibrationAsync(
        Guid? profileId,
        CancellationToken ct
    )
    {
        var profile = await db
            .CalibrationProfiles.AsNoTracking()
            .FirstOrDefaultAsync(
                item => profileId == null ? item.IsActive : item.Id == profileId,
                ct
            );
        if (profile is null && profileId is not null)
            throw new NotFoundException("Calibration profile", profileId);
        return profile is null
            ? null
            : InspectionJson.Deserialize<CalibrationDefinition>(profile.DefinitionJson);
    }

    private static ProductTrack CreateTrack(bool simulation, DateTime now)
    {
        var id = Guid.NewGuid();
        return new ProductTrack
        {
            Id = id,
            PublicCode = "PRD-" + id.ToString("N")[..16].ToUpperInvariant(),
            Stage = InspectionVocabulary.StageDetected,
            IsSimulation = simulation,
            DetectedAt = now,
            UpdatedAt = now,
        };
    }

    private static ProductTrackState ToState(ProductTrack track) =>
        new()
        {
            ProductId = track.Id,
            Stage = track.Stage,
            Simulation = track.IsSimulation,
            DetectedAt = track.DetectedAt,
            CapturedAt = track.CapturedAt,
            InspectingAt = track.InspectingAt,
            DecidedAt = track.DecidedAt,
            QueuedAt = track.QueuedAt,
            SortedAt = track.SortedAt,
            FailedAt = track.FailedAt,
            ManualReviewAt = track.ManualReviewAt,
            SortDueAt = track.SortDueAt,
            DestinationBin = track.DestinationBin,
            Decision = track.Decision,
            FailureReason = track.FailureReason,
        };

    private static void Copy(ProductTrack track, ProductTrackState state)
    {
        track.Stage = state.Stage;
        track.CapturedAt = state.CapturedAt;
        track.InspectingAt = state.InspectingAt;
        track.DecidedAt = state.DecidedAt;
        track.QueuedAt = state.QueuedAt;
        track.SortedAt = state.SortedAt;
        track.FailedAt = state.FailedAt;
        track.ManualReviewAt = state.ManualReviewAt;
        track.SortDueAt = state.SortDueAt;
        track.DestinationBin = state.DestinationBin;
        track.Decision = state.Decision;
        track.FailureReason = state.FailureReason;
        track.UpdatedAt = DateTime.UtcNow;
    }

    private static InspectionRecord ToRecord(InspectionResult result) =>
        new()
        {
            Id = result.InspectionId,
            ProductId = result.ProductId,
            RecipeId = result.RecipeId,
            CapturedAt = result.CapturedAt,
            ProcessedAt = result.ProcessedAt,
            ProductClass = result.ProductClass,
            ColorClass = result.ColorClass,
            SizeClass = result.SizeClass,
            DefectClassesJson = InspectionJson.Serialize(result.DefectClasses),
            QualityStatus = result.QualityStatus,
            Confidence = result.Confidence,
            Decision = result.Decision,
            DestinationBin = result.DestinationBin,
            ModelName = result.ModelName,
            ModelVersion = result.ModelVersion,
            ProcessingLatencyMs = result.ProcessingLatencyMs,
            ImageReference = result.ImageReference,
            IsSimulation = result.Simulation,
            ResultJson = InspectionJson.Serialize(result),
        };

    public static TrackResponse ToResponse(ProductTrack track) =>
        new(
            track.Id,
            track.PublicCode,
            track.RecipeId,
            track.InspectionId,
            track.Stage,
            track.IsSimulation,
            track.DestinationBin,
            track.Decision,
            track.FailureReason,
            track.DetectedAt,
            track.CapturedAt,
            track.InspectingAt,
            track.DecidedAt,
            track.QueuedAt,
            track.SortedAt,
            track.FailedAt,
            track.ManualReviewAt,
            track.SortDueAt
        );

    private async Task PublishAsync(
        InspectionResult result,
        TrackResponse track,
        CancellationToken ct
    )
    {
        try
        {
            await hub.Clients.All.SendAsync("ReceiveInspectionUpdate", result, ct);
            await hub.Clients.All.SendAsync("ReceiveTrackingUpdate", track, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "SignalR inspection notification failed for {InspectionId}.",
                result.InspectionId
            );
        }
    }

    private async Task PublishTrackAsync(TrackResponse track, CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("ReceiveTrackingUpdate", track, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "SignalR tracking notification failed for {ProductId}.",
                track.ProductId
            );
        }
    }
}
