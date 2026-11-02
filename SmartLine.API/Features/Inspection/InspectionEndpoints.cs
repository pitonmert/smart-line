using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Domain.Inspection;
using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Infrastructure.Messaging.Mqtt;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Inspection;

public static class InspectionEndpoints
{
    public static IEndpointRouteBuilder MapInspectionPlatform(this IEndpointRouteBuilder app)
    {
        MapRecipes(app);
        MapCalibration(app);
        MapInspections(app);
        MapTracking(app);
        MapSimulation(app);
        MapDataset(app);
        MapDiagnostics(app);
        return app;
    }

    private static void MapRecipes(IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/recipes/active/snapshot",
            async (ApplicationDbContext db, CancellationToken ct) =>
            {
                var recipe = await db
                    .ProductRecipes.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.IsActive, ct);
                if (recipe is null)
                    return Results.NotFound(
                        ApiResponse<ActiveRecipeSnapshot>.Error(
                            "Active product recipe was not found.",
                            ErrorCodes.ResourceNotFound
                        )
                    );
                var snapshot = new ActiveRecipeSnapshot(
                    recipe.Id,
                    recipe.Name,
                    recipe.Version,
                    recipe.UpdatedAt,
                    InspectionJson.Deserialize<ProductRecipeDefinition>(recipe.DefinitionJson)
                );
                return Results.Ok(ApiResponse<ActiveRecipeSnapshot>.Success(snapshot));
            }
        );

        var group = app.MapGroup("/api/recipes");
        group.MapGet(
            "",
            async (ApplicationDbContext db, CancellationToken ct) =>
                Results.Ok(ApiResponse<List<RecipeResponse>>.Success(await RecipesAsync(db, ct)))
        );
        group.MapGet(
            "/active",
            async (ApplicationDbContext db, CancellationToken ct) =>
            {
                var recipe = (await RecipesAsync(db, ct)).FirstOrDefault(item => item.IsActive);
                return recipe is null
                    ? Results.NotFound(
                        ApiResponse<RecipeResponse>.Error(
                            "Active product recipe was not found.",
                            ErrorCodes.ResourceNotFound
                        )
                    )
                    : Results.Ok(ApiResponse<RecipeResponse>.Success(recipe));
            }
        );
        group.MapGet(
            "/{id:guid}",
            async (Guid id, ApplicationDbContext db, CancellationToken ct) =>
            {
                var recipe = (await RecipesAsync(db, ct)).FirstOrDefault(item => item.Id == id);
                return recipe is null
                    ? Results.NotFound(
                        ApiResponse<RecipeResponse>.Error(
                            "Product recipe was not found.",
                            ErrorCodes.ResourceNotFound
                        )
                    )
                    : Results.Ok(ApiResponse<RecipeResponse>.Success(recipe));
            }
        );
        group.MapPost(
            "",
            async (
                RecipeWriteRequest request,
                ApplicationDbContext db,
                IHttpClientFactory factory,
                IOptions<VisionEndpointOptions> vision,
                CancellationToken ct
            ) =>
            {
                var saved = await SaveRecipeAsync(null, request, db, factory, vision.Value, ct);
                return Results.Created(
                    $"/api/recipes/{saved.Id}",
                    ApiResponse<RecipeResponse>.Success(saved, "Product recipe created.")
                );
            }
        );
        group.MapPut(
            "/{id:guid}",
            async (
                Guid id,
                RecipeWriteRequest request,
                ApplicationDbContext db,
                IHttpClientFactory factory,
                IOptions<VisionEndpointOptions> vision,
                CancellationToken ct
            ) =>
            {
                return Results.Ok(
                    ApiResponse<RecipeResponse>.Success(
                        await SaveRecipeAsync(id, request, db, factory, vision.Value, ct),
                        "Product recipe updated."
                    )
                );
            }
        );
        group.MapPost(
            "/{id:guid}/activate",
            async (
                Guid id,
                ApplicationDbContext db,
                IHttpClientFactory factory,
                IOptions<VisionEndpointOptions> vision,
                CancellationToken ct
            ) =>
            {
                await using var transaction = await InspectionTransaction.BeginAsync(db, ct);
                var recipes = await db.ProductRecipes.ToListAsync(ct);
                if (recipes.All(item => item.Id != id))
                    throw new NotFoundException("Product recipe", id);
                await VisionInspection.ValidateRecipeAsync(
                    InspectionJson.Deserialize<ProductRecipeDefinition>(
                        recipes.First(r => r.Id == id).DefinitionJson
                    ),
                    factory,
                    vision.Value,
                    ct
                );
                foreach (var recipe in recipes)
                    recipe.IsActive = false;
                await db.SaveChangesAsync(ct);
                var selected = recipes.First(item => item.Id == id);
                selected.IsActive = true;
                selected.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Ok(
                    ApiResponse<RecipeResponse>.Success(
                        (await RecipesAsync(db, ct)).First(item => item.Id == id),
                        "Product recipe activated."
                    )
                );
            }
        );
    }

    private static void MapCalibration(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/calibration-profiles");
        group.MapGet(
            "",
            async (ApplicationDbContext db, CancellationToken ct) =>
                Results.Ok(
                    ApiResponse<List<CalibrationResponse>>.Success(await CalibrationsAsync(db, ct))
                )
        );
        group.MapGet(
            "/active",
            async (ApplicationDbContext db, CancellationToken ct) =>
            {
                var profile = (await CalibrationsAsync(db, ct)).FirstOrDefault(item =>
                    item.IsActive
                );
                return profile is null
                    ? Results.NotFound(
                        ApiResponse<CalibrationResponse>.Error(
                            "No calibration profile is active.",
                            ErrorCodes.ResourceNotFound
                        )
                    )
                    : Results.Ok(ApiResponse<CalibrationResponse>.Success(profile));
            }
        );
        group.MapPost(
            "",
            async (
                CalibrationWriteRequest request,
                ApplicationDbContext db,
                CancellationToken ct
            ) =>
            {
                var saved = await SaveCalibrationAsync(null, request, db, ct);
                return Results.Created(
                    $"/api/calibration-profiles/{saved.Id}",
                    ApiResponse<CalibrationResponse>.Success(saved, "Calibration profile created.")
                );
            }
        );
        group.MapPut(
            "/{id:guid}",
            async (
                Guid id,
                CalibrationWriteRequest request,
                ApplicationDbContext db,
                CancellationToken ct
            ) =>
                Results.Ok(
                    ApiResponse<CalibrationResponse>.Success(
                        await SaveCalibrationAsync(id, request, db, ct),
                        "Calibration profile updated."
                    )
                )
        );
        group.MapPost(
            "/{id:guid}/activate",
            async (Guid id, ApplicationDbContext db, CancellationToken ct) =>
            {
                await using var transaction = await InspectionTransaction.BeginAsync(db, ct);
                var profiles = await db.CalibrationProfiles.ToListAsync(ct);
                if (profiles.All(item => item.Id != id))
                    throw new NotFoundException("Calibration profile", id);
                foreach (var profile in profiles)
                    profile.IsActive = false;
                await db.SaveChangesAsync(ct);
                var selected = profiles.First(item => item.Id == id);
                selected.IsActive = true;
                selected.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Ok(
                    ApiResponse<CalibrationResponse>.Success(
                        (await CalibrationsAsync(db, ct)).First(item => item.Id == id)
                    )
                );
            }
        );
    }

    private static void MapInspections(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/inspections");
        group.MapGet(
            "",
            async (
                int? pageNumber,
                int? pageSize,
                string? decision,
                Guid? recipeId,
                bool? simulation,
                string? productClass,
                string? q,
                DateTime? from,
                DateTime? to,
                ApplicationDbContext db,
                CancellationToken ct
            ) =>
            {
                var page = Pagination.Normalize(pageNumber ?? 1, pageSize ?? 20);
                var query = db.InspectionRecords.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(decision))
                    query = query.Where(item =>
                        item.Decision == decision.Trim().ToUpperInvariant()
                    );
                if (recipeId is not null)
                    query = query.Where(item => item.RecipeId == recipeId);
                if (simulation is not null)
                    query = query.Where(item => item.IsSimulation == simulation);
                if (!string.IsNullOrWhiteSpace(productClass))
                    query = query.Where(item =>
                        item.ProductClass == productClass.Trim().ToUpperInvariant()
                    );
                if (from is not null)
                    query = query.Where(item => item.ProcessedAt >= from);
                if (to is not null)
                    query = query.Where(item => item.ProcessedAt <= to);
                if (!string.IsNullOrWhiteSpace(q))
                {
                    var term = q.Trim();
                    var tracks = db
                        .ProductTracks.AsNoTracking()
                        .Where(track => track.PublicCode.Contains(term));
                    query = query.Where(item =>
                        item.ProductClass.Contains(term)
                        || item.DestinationBin.Contains(term)
                        || item.Decision.Contains(term)
                        || tracks.Any(track => track.Id == item.ProductId)
                    );
                }
                var rows = await query
                    .OrderByDescending(item => item.ProcessedAt)
                    .ThenByDescending(item => item.Id)
                    .Skip((page.PageNumber - 1) * page.PageSize)
                    .Take(page.PageSize + 1)
                    .ToListAsync(ct);
                var hasNext = rows.Count > page.PageSize;
                if (hasNext)
                    rows.RemoveAt(rows.Count - 1);
                return Results.Ok(
                    ApiResponse<PagedResult<InspectionResult>>.Success(
                        new PagedResult<InspectionResult>(
                            rows.Select(row =>
                                    InspectionJson.Deserialize<InspectionResult>(row.ResultJson)
                                )
                                .ToList(),
                            hasNext,
                            page.PageNumber,
                            page.PageSize
                        )
                    )
                );
            }
        );

        group.MapGet(
            "/{id:guid}",
            async (Guid id, ApplicationDbContext db, CancellationToken ct) =>
            {
                var row = await db
                    .InspectionRecords.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.Id == id, ct);
                return row is null
                    ? Results.NotFound(
                        ApiResponse<InspectionResult>.Error(
                            "Inspection was not found.",
                            ErrorCodes.ResourceNotFound
                        )
                    )
                    : Results.Ok(
                        ApiResponse<InspectionResult>.Success(
                            InspectionJson.Deserialize<InspectionResult>(row.ResultJson)
                        )
                    );
            }
        );

        group.MapPost(
            "",
            async (
                InspectionSubmitRequest request,
                InspectionWorkflow workflow,
                CancellationToken ct
            ) =>
            {
                var completed = await workflow.SubmitAsync(
                    request.ToObservation(),
                    request.RecipeId,
                    request.ProductId,
                    null,
                    null,
                    ct,
                    request.RecipeVersion
                );
                return Results.Created(
                    $"/api/inspections/{completed.Inspection.InspectionId}",
                    ApiResponse<CompletedInspection>.Success(completed, "Inspection recorded.")
                );
            }
        );
    }

    private static void MapTracking(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tracking");
        group.MapGet(
            "",
            async (bool? activeOnly, ApplicationDbContext db, CancellationToken ct) =>
            {
                var query = db.ProductTracks.AsNoTracking();
                if (activeOnly == true)
                    query = query.Where(track =>
                        track.Stage != InspectionVocabulary.StageSorted
                        && track.Stage != InspectionVocabulary.StageFailed
                    );
                var tracks = await query
                    .OrderBy(track => track.SortDueAt ?? DateTime.MaxValue)
                    .ThenBy(track => track.DetectedAt)
                    .Take(100)
                    .ToListAsync(ct);
                return Results.Ok(
                    ApiResponse<List<TrackResponse>>.Success(
                        tracks.Select(InspectionWorkflow.ToResponse).ToList()
                    )
                );
            }
        );
        group.MapGet(
            "/queue",
            async (ApplicationDbContext db, CancellationToken ct) =>
            {
                var queued = await db
                    .ProductTracks.AsNoTracking()
                    .Where(track => track.Stage == InspectionVocabulary.StageQueued)
                    .OrderBy(track => track.SortDueAt ?? DateTime.MaxValue)
                    .ThenBy(track => track.QueuedAt)
                    .ToListAsync(ct);
                return Results.Ok(
                    ApiResponse<List<TrackResponse>>.Success(
                        queued.Select(InspectionWorkflow.ToResponse).ToList()
                    )
                );
            }
        );
        group.MapPost(
            "/{id:guid}/release",
            async (Guid id, InspectionWorkflow workflow, CancellationToken ct) =>
            {
                var track = await workflow.ReleaseAsync(id, ct);
                return Results.Ok(
                    ApiResponse<ReleaseResponse>.Success(
                        new ReleaseResponse(track, workflow.Actuation()),
                        "Logical sort recorded. No actuator command was sent."
                    )
                );
            }
        );
    }

    private static void MapSimulation(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/simulation");
        group.MapGet(
            "/status",
            async (
                SimulationLatch latch,
                ApplicationDbContext db,
                IOptions<SimulationOptions> options,
                CancellationToken ct
            ) =>
                Results.Ok(
                    ApiResponse<SimulationStatusResponse>.Success(
                        await SimulationStatusAsync(latch, db, options.Value.Enabled, ct)
                    )
                )
        );
        group.MapPost(
            "/arrivals",
            async (
                SimulationArrivalRequest request,
                InspectionWorkflow workflow,
                SimulationLatch latch,
                IOptions<SimulationOptions> options,
                CancellationToken ct
            ) =>
            {
                EnsureSimulation(options.Value);
                var observation = request.ToObservation();
                observation.TriggerSource = InspectionVocabulary.TriggerSimulator;
                observation.Simulation = true;
                latch.Latch();
                var completed = await workflow.SubmitAsync(
                    observation,
                    request.RecipeId,
                    null,
                    request.DistanceMm,
                    request.BeltSpeedMmPerSecond,
                    ct
                );
                return Results.Created(
                    $"/api/inspections/{completed.Inspection.InspectionId}",
                    ApiResponse<CompletedInspection>.Success(
                        completed,
                        "SIMULATION arrival recorded."
                    )
                );
            }
        );
        group.MapPost(
            "/tick",
            async (
                SimulationTickRequest request,
                InspectionWorkflow workflow,
                IOptions<SimulationOptions> options,
                CancellationToken ct
            ) =>
            {
                EnsureSimulation(options.Value);
                var sorted = await workflow.AdvanceDueSimulatedAsync(
                    request.UtcNow ?? DateTime.UtcNow,
                    ct
                );
                return Results.Ok(
                    ApiResponse<SimulationTickResponse>.Success(
                        new SimulationTickResponse(sorted, workflow.Actuation()),
                        "SIMULATION queue advanced. No actuator command was sent."
                    )
                );
            }
        );
        group.MapPost(
            "/clear",
            (SimulationLatch latch) =>
            {
                latch.Clear();
                return Results.Ok(
                    ApiResponse<string>.Success(
                        "cleared",
                        "Simulation latch cleared. Historical SIMULATION records remain marked."
                    )
                );
            }
        );
    }

    private static void MapDataset(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dataset/sessions");
        group.MapGet(
            "",
            async (DatasetStore store, CancellationToken ct) =>
                Results.Ok(
                    ApiResponse<IReadOnlyList<DatasetSessionResponse>>.Success(
                        await store.ListAsync(ct)
                    )
                )
        );
        group.MapGet(
            "/active",
            async (DatasetStore store, CancellationToken ct) =>
            {
                var active = (await store.ListAsync(ct)).FirstOrDefault(item =>
                    item.Status == "ACTIVE"
                );
                return active is null
                    ? Results.NotFound(
                        ApiResponse<DatasetSessionResponse>.Error(
                            "No dataset session is active.",
                            ErrorCodes.ResourceNotFound
                        )
                    )
                    : Results.Ok(ApiResponse<DatasetSessionResponse>.Success(active));
            }
        );
        group.MapGet(
            "/{id:guid}",
            async (Guid id, DatasetStore store, CancellationToken ct) =>
            {
                var session = await store.GetAsync(id, ct);
                return session is null
                    ? Results.NotFound(
                        ApiResponse<DatasetSessionResponse>.Error(
                            "Dataset session was not found.",
                            ErrorCodes.ResourceNotFound
                        )
                    )
                    : Results.Ok(ApiResponse<DatasetSessionResponse>.Success(session));
            }
        );
        group.MapGet(
            "/{id:guid}/images/{fileName}",
            async (Guid id, string fileName, DatasetStore store, CancellationToken ct) =>
            {
                var image = await store.ReadImageAsync(id, fileName, ct);
                return image is null
                    ? Results.NotFound()
                    : Results.File(image.Value.Bytes, image.Value.ContentType);
            }
        );
        group.MapPost(
            "",
            async (DatasetStartRequest request, DatasetStore store, CancellationToken ct) =>
            {
                var session = await store.StartAsync(
                    request.RecipeId,
                    request.ProductClass,
                    request.QualityLabel,
                    request.ColorLabel,
                    request.SizeLabel,
                    request.DefectLabels ?? [],
                    request.Notes,
                    request.CaptureMode,
                    request.IntervalMs,
                    request.Simulation,
                    ct
                );
                return Results.Created(
                    $"/api/dataset/sessions/{session.SessionId}",
                    ApiResponse<DatasetSessionResponse>.Success(session, "Dataset session started.")
                );
            }
        );
        group.MapPost(
            "/{id:guid}/stop",
            async (Guid id, DatasetStore store, CancellationToken ct) =>
                Results.Ok(
                    ApiResponse<DatasetSessionResponse>.Success(
                        await store.StopAsync(id, ct),
                        "Dataset session stopped."
                    )
                )
        );
        group
            .MapPost(
                "/{id:guid}/captures/camera",
                async (
                    Guid id,
                    DatasetCameraCaptureRequest request,
                    DatasetStore store,
                    IVisionFrames frames,
                    CancellationToken ct
                ) =>
                {
                    var frame = await frames.ReadAsync("dataset", ct);
                    var captured = await store.CaptureAsync(
                        id,
                        frame.Jpeg,
                        "vision-dataset-frame.jpg",
                        new DatasetCaptureDetails(
                            request.ProductClass,
                            request.QualityLabel,
                            request.ColorLabel,
                            request.SizeLabel,
                            request.DefectLabels,
                            request.Notes,
                            "camera",
                            "dataset",
                            frame.Origin,
                            frame.Width,
                            frame.Height,
                            true,
                            frame.CaptureSource,
                            frame.PreviewFallback
                        ),
                        ct
                    );
                    return Results.Ok(
                        ApiResponse<DatasetCaptureResult>.Success(
                            captured,
                            "Camera frame stored. Bounding boxes were not invented."
                        )
                    );
                }
            )
            .DisableAntiforgery();
        group.MapDelete(
            "/{id:guid}/samples/{sampleId:guid}",
            async (Guid id, Guid sampleId, DatasetStore store, CancellationToken ct) =>
                Results.Ok(
                    ApiResponse<DatasetSessionResponse>.Success(
                        await store.RejectAsync(id, sampleId, ct),
                        "Capture discarded. The image file was removed."
                    )
                )
        );
    }

    private static void MapDiagnostics(IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/diagnostics",
            async (
                ApplicationDbContext db,
                IMqttService mqtt,
                IOptions<VisionEndpointOptions> vision,
                IVisionProbe visionProbe,
                IOptions<SortingOptions> sorting,
                IOptions<SimulationOptions> simulation,
                SimulationLatch latch,
                IHostEnvironment environment,
                CancellationToken ct
            ) =>
            {
                var database = "unknown";
                try
                {
                    database = await db.Database.CanConnectAsync(ct) ? "healthy" : "unavailable";
                }
                catch (Exception)
                {
                    database = "unavailable";
                }

                var probe = await visionProbe.ProbeAsync(vision.Value.HealthUrl, ct);
                var visionStatus = VisionDiagnostics.MapService(vision.Value.HealthUrl, probe);
                var cameraStatus = VisionDiagnostics.MapCamera(visionStatus, probe);

                var conveyor = await db.ConveyorSettings.AsNoTracking().FirstOrDefaultAsync(ct);
                var esp32 =
                    conveyor is null ? "unknown"
                    : DateTime.UtcNow - conveyor.LastUpdated < TimeSpan.FromSeconds(30) ? "online"
                    : "stale";
                var recipe = await db
                    .ProductRecipes.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.IsActive, ct);
                var calibration = await db
                    .CalibrationProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.IsActive, ct);
                var last = await db
                    .InspectionRecords.AsNoTracking()
                    .OrderByDescending(item => item.ProcessedAt)
                    .FirstOrDefaultAsync(ct);
                var inFlight = await db.ProductTracks.CountAsync(
                    track =>
                        track.IsSimulation
                        && track.Stage != InspectionVocabulary.StageSorted
                        && track.Stage != InspectionVocabulary.StageFailed,
                    ct
                );
                var definition = recipe is null
                    ? null
                    : InspectionJson.Deserialize<ProductRecipeDefinition>(recipe.DefinitionJson);
                var calibrationDefinition = calibration is null
                    ? null
                    : InspectionJson.Deserialize<CalibrationDefinition>(calibration.DefinitionJson);
                var artifactName = definition?.Model.ArtifactFileName;
                var modelConfigured =
                    probe.ModelConfigured == true || !string.IsNullOrWhiteSpace(artifactName);
                var modelLoaded = probe.ModelLoaded == true;
                var modelStatus =
                    modelLoaded ? "loaded"
                    : modelConfigured
                        ? probe.ModelLoadError is null ? "registered"
                            : "error"
                    : "not_loaded";
                var model = new ModelDiagnostics(
                    modelStatus,
                    definition?.Model.Name,
                    probe.ModelVersion ?? definition?.Model.Version,
                    modelConfigured,
                    modelLoaded,
                    probe.ModelBackend,
                    probe.ModelTask ?? definition?.Model.Task,
                    probe.ModelArtifact ?? artifactName,
                    probe.LastInferenceLatencyMs,
                    probe.ModelLoadError,
                    probe.ConfidenceThreshold
                );

                var response = new DiagnosticsResponse(
                    "healthy",
                    database,
                    mqtt.IsConnected ? "connected" : "disconnected",
                    visionStatus,
                    cameraStatus,
                    last?.ProcessedAt,
                    modelStatus,
                    definition?.Model.Name,
                    definition?.Model.Version,
                    esp32,
                    "hub_enabled",
                    new SimulationStatusResponse(
                        simulation.Value.Enabled,
                        latch.IsLatched || inFlight > 0,
                        latch.IsLatched,
                        inFlight
                    ),
                    sorting.Value.HardwareActuationEnabled,
                    environment.EnvironmentName,
                    recipe?.Name,
                    calibrationDefinition?.TofBaseline.Source
                        ?? InspectionVocabulary.TofNotIntegrated,
                    VisionEndpoint.PublicUrl(definition?.CameraProfile.StreamUrl),
                    probe.LastFrameAt,
                    probe.FrameWidth,
                    probe.FrameHeight,
                    probe.ConsecutiveFailures,
                    probe.StreamHost
                        ?? VisionEndpoint.HostOnly(definition?.CameraProfile.StreamUrl),
                    model,
                    probe.StillConfigured,
                    probe.ConfiguredStillWidth,
                    probe.ConfiguredStillHeight,
                    probe.LastStillWidth,
                    probe.LastStillHeight,
                    probe.LastStillError,
                    probe.PreviewFallbackAllowed,
                    probe.SensorPid,
                    recipe?.Version
                );
                return Results.Ok(ApiResponse<DiagnosticsResponse>.Success(response));
            }
        );

        app.MapGet(
            "/api/control-room",
            async (ApplicationDbContext db, SimulationLatch latch, CancellationToken ct) =>
            {
                var pass = await db
                    .InspectionRecords.Where(item => !item.IsSimulation)
                    .CountAsync(item => item.Decision == InspectionVocabulary.Pass, ct);
                var reject = await db
                    .InspectionRecords.Where(item => !item.IsSimulation)
                    .CountAsync(item => item.Decision == InspectionVocabulary.Reject, ct);
                var manual = await db
                    .InspectionRecords.Where(item => !item.IsSimulation)
                    .CountAsync(item => item.Decision == InspectionVocabulary.ManualReview, ct);
                var total = await db
                    .InspectionRecords.Where(item => !item.IsSimulation)
                    .CountAsync(ct);
                var bins = await db
                    .InspectionRecords.Where(item => !item.IsSimulation)
                    .AsNoTracking()
                    .GroupBy(item => item.DestinationBin)
                    .Select(group => new BinCount(group.Key, group.Count()))
                    .ToListAsync(ct);
                var latestRow = await db
                    .InspectionRecords.AsNoTracking()
                    .OrderByDescending(item => item.ProcessedAt)
                    .FirstOrDefaultAsync(ct);
                var latest = latestRow is null
                    ? null
                    : InspectionJson.Deserialize<InspectionResult>(latestRow.ResultJson);
                var recipe = await db
                    .ProductRecipes.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.IsActive, ct);
                var current = await db
                    .ProductTracks.AsNoTracking()
                    .Where(track =>
                        track.Stage != InspectionVocabulary.StageSorted
                        && track.Stage != InspectionVocabulary.StageFailed
                    )
                    .OrderByDescending(track => track.UpdatedAt)
                    .FirstOrDefaultAsync(ct);
                var inFlight = await db.ProductTracks.CountAsync(
                    track =>
                        track.IsSimulation
                        && track.Stage != InspectionVocabulary.StageSorted
                        && track.Stage != InspectionVocabulary.StageFailed,
                    ct
                );
                var logs = await db
                    .SystemLogs.AsNoTracking()
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(8)
                    .ToListAsync(ct);
                var response = new ControlRoomResponse(
                    recipe?.Name,
                    current is null ? null : InspectionWorkflow.ToResponse(current),
                    latest,
                    total,
                    pass,
                    reject,
                    manual,
                    ProductionMath.YieldPercent(pass, reject),
                    bins,
                    logs.Select(item => new ControlRoomEvent(
                            item.CreatedAt,
                            item.EventType.ToString(),
                            item.Message,
                            item.Severity
                        ))
                        .ToList(),
                    latch.IsLatched || inFlight > 0 || latest?.Simulation == true
                );
                return Results.Ok(ApiResponse<ControlRoomResponse>.Success(response));
            }
        );
    }

    private static async Task<List<RecipeResponse>> RecipesAsync(
        ApplicationDbContext db,
        CancellationToken ct
    ) =>
        (await db.ProductRecipes.AsNoTracking().OrderBy(item => item.Name).ToListAsync(ct))
            .Select(item => new RecipeResponse(
                item.Id,
                item.Name,
                item.Description,
                item.IsActive,
                item.Version,
                InspectionJson.Deserialize<ProductRecipeDefinition>(item.DefinitionJson),
                item.UpdatedAt
            ))
            .ToList();

    private static async Task<RecipeResponse> SaveRecipeAsync(
        Guid? id,
        RecipeWriteRequest request,
        ApplicationDbContext db,
        IHttpClientFactory factory,
        VisionEndpointOptions vision,
        CancellationToken ct
    )
    {
        await using var transaction = await InspectionTransaction.BeginAsync(db, ct);
        var definition =
            request.Definition
            ?? throw new ValidationException(
                "Recipe definition is required.",
                ErrorCodes.RecipeInvalid
            );
        definition.Name = string.IsNullOrWhiteSpace(request.Name)
            ? definition.Name
            : request.Name.Trim();
        definition.Description = request.Description ?? definition.Description;
        var errors = RecipeValidator.Validate(definition);
        if (errors.Count > 0)
            throw new ValidationException(string.Join(' ', errors), ErrorCodes.RecipeInvalid);
        if (
            await db.ProductRecipes.AnyAsync(
                r => r.Name == definition.Name && (id == null || r.Id != id),
                ct
            )
        )
            throw new ApiException(
                409,
                ErrorCodes.RecipeInvalid,
                "A recipe with this name already exists."
            );
        if (
            definition.CalibrationProfileId is not null
            && !await db.CalibrationProfiles.AnyAsync(
                p => p.Id == definition.CalibrationProfileId,
                ct
            )
        )
            throw new ValidationException(
                "Referenced calibration profile does not exist.",
                ErrorCodes.RecipeInvalid
            );
        var now = DateTime.UtcNow;
        ProductRecipe entity;
        if (id is null)
        {
            entity = new ProductRecipe
            {
                Id = Guid.NewGuid(),
                Name = definition.Name,
                Description = definition.Description,
                IsActive = false,
                Version = 1,
                DefinitionJson = InspectionJson.Serialize(definition),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ProductRecipes.Add(entity);
        }
        else
        {
            entity =
                await db.ProductRecipes.FirstOrDefaultAsync(item => item.Id == id, ct)
                ?? throw new NotFoundException("Product recipe", id);
            if (entity.IsActive)
                await VisionInspection.ValidateRecipeAsync(definition, factory, vision, ct);
            entity.Name = definition.Name;
            entity.Description = definition.Description;
            entity.Version += 1;
            entity.DefinitionJson = InspectionJson.Serialize(definition);
            entity.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new RecipeResponse(
            entity.Id,
            entity.Name,
            entity.Description,
            entity.IsActive,
            entity.Version,
            definition,
            entity.UpdatedAt
        );
    }

    private static async Task<List<CalibrationResponse>> CalibrationsAsync(
        ApplicationDbContext db,
        CancellationToken ct
    ) =>
        (await db.CalibrationProfiles.AsNoTracking().OrderBy(item => item.Name).ToListAsync(ct))
            .Select(item =>
            {
                var definition = InspectionJson.Deserialize<CalibrationDefinition>(
                    item.DefinitionJson
                );
                return new CalibrationResponse(
                    item.Id,
                    item.Name,
                    item.Description,
                    item.IsActive,
                    item.Version,
                    definition,
                    CalibrationValidator.UsesSimulation(definition),
                    item.UpdatedAt
                );
            })
            .ToList();

    private static async Task<CalibrationResponse> SaveCalibrationAsync(
        Guid? id,
        CalibrationWriteRequest request,
        ApplicationDbContext db,
        CancellationToken ct
    )
    {
        await using var transaction = await InspectionTransaction.BeginAsync(db, ct);
        var definition =
            request.Definition
            ?? throw new ValidationException(
                "Calibration definition is required.",
                ErrorCodes.CalibrationInvalid
            );
        var errors = CalibrationValidator.Validate(definition);
        if (errors.Count > 0)
            throw new ValidationException(string.Join(' ', errors), ErrorCodes.CalibrationInvalid);
        var name = string.IsNullOrWhiteSpace(request.Name) ? "Calibration" : request.Name.Trim();
        if (name.Length > 80 || request.Description?.Length > 1000)
            throw new ValidationException(
                "Calibration name or description is too long.",
                ErrorCodes.CalibrationInvalid
            );
        if (
            await db.CalibrationProfiles.AnyAsync(
                r => r.Name == name && (id == null || r.Id != id),
                ct
            )
        )
            throw new ApiException(
                409,
                ErrorCodes.CalibrationInvalid,
                "A calibration with this name already exists."
            );
        var now = DateTime.UtcNow;
        CalibrationProfile entity;
        if (id is null)
        {
            entity = new CalibrationProfile
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = request.Description ?? definition.Notes ?? "",
                IsActive = false,
                Version = 1,
                DefinitionJson = InspectionJson.Serialize(definition),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.CalibrationProfiles.Add(entity);
        }
        else
        {
            entity =
                await db.CalibrationProfiles.FirstOrDefaultAsync(item => item.Id == id, ct)
                ?? throw new NotFoundException("Calibration profile", id);
            entity.Name = name;
            entity.Description = request.Description ?? entity.Description;
            entity.Version += 1;
            entity.DefinitionJson = InspectionJson.Serialize(definition);
            entity.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new CalibrationResponse(
            entity.Id,
            entity.Name,
            entity.Description,
            entity.IsActive,
            entity.Version,
            definition,
            CalibrationValidator.UsesSimulation(definition),
            entity.UpdatedAt
        );
    }

    private static void EnsureSimulation(SimulationOptions options)
    {
        if (!options.Enabled)
            throw new ApiException(
                StatusCodes.Status403Forbidden,
                ErrorCodes.SimulationDisabled,
                "Simulation mode is disabled in this environment."
            );
    }

    private static async Task<SimulationStatusResponse> SimulationStatusAsync(
        SimulationLatch latch,
        ApplicationDbContext db,
        bool enabled,
        CancellationToken ct
    )
    {
        var inFlight = await db.ProductTracks.CountAsync(
            track =>
                track.IsSimulation
                && track.Stage != InspectionVocabulary.StageSorted
                && track.Stage != InspectionVocabulary.StageFailed,
            ct
        );
        return new SimulationStatusResponse(
            enabled,
            latch.IsLatched || inFlight > 0,
            latch.IsLatched,
            inFlight
        );
    }
}

public sealed record RecipeWriteRequest(
    string Name,
    string? Description,
    ProductRecipeDefinition? Definition
);

public sealed record RecipeResponse(
    Guid Id,
    string Name,
    string Description,
    bool IsActive,
    int Version,
    ProductRecipeDefinition Definition,
    DateTime UpdatedAt
);

public sealed record ActiveRecipeSnapshot(
    Guid Id,
    string Name,
    int Version,
    DateTime UpdatedAt,
    ProductRecipeDefinition Definition
);

public sealed record CalibrationWriteRequest(
    string Name,
    string? Description,
    CalibrationDefinition? Definition
);

public sealed record CalibrationResponse(
    Guid Id,
    string Name,
    string Description,
    bool IsActive,
    int Version,
    CalibrationDefinition Definition,
    bool SimulationDataPresent,
    DateTime UpdatedAt
);

public class InspectionSubmitRequest
{
    public Guid? RecipeId { get; set; }
    public Guid? ProductId { get; set; }
    public int? RecipeVersion { get; set; }
    public DateTime? CapturedAt { get; set; }
    public string? ProductClass { get; set; }
    public string? ColorClass { get; set; }
    public string? SizeClass { get; set; }
    public List<string>? DefectClasses { get; set; }
    public double Confidence { get; set; }
    public double? VisualWidth { get; set; }
    public double? VisualHeight { get; set; }
    public double? EstimatedWidthMm { get; set; }
    public double? EstimatedHeightMm { get; set; }
    public bool Simulation { get; set; }
    public List<BoundingBox>? BoundingBoxes { get; set; }
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
    public string? ModelName { get; set; }
    public string? ModelVersion { get; set; }
    public int? ProcessingLatencyMs { get; set; }
    public string? CameraSource { get; set; }
    public string? ImageReference { get; set; }
    public string? TriggerSource { get; set; }
    public string? FrameRole { get; set; }
    public string? CaptureSource { get; set; }
    public bool PreviewFallback { get; set; }

    public InspectionObservation ToObservation() =>
        new()
        {
            CapturedAt = CapturedAt,
            ProductClass = ProductClass,
            ColorClass = ColorClass,
            SizeClass = SizeClass,
            DefectClasses = DefectClasses ?? [],
            Confidence = Confidence,
            VisualWidth = VisualWidth,
            VisualHeight = VisualHeight,
            EstimatedWidthMm = EstimatedWidthMm,
            EstimatedHeightMm = EstimatedHeightMm,
            Simulation = Simulation,
            BoundingBoxes = BoundingBoxes ?? [],
            SourceWidth = SourceWidth,
            SourceHeight = SourceHeight,
            ModelName = ModelName,
            ModelVersion = ModelVersion,
            ProcessingLatencyMs = ProcessingLatencyMs,
            CameraSource = CameraSource,
            ImageReference = ImageReference,
            TriggerSource = TriggerSource ?? InspectionVocabulary.TriggerApi,
            FrameRole = FrameRole,
            CaptureSource = CaptureSource,
            PreviewFallback = PreviewFallback,
        };
}

public sealed class SimulationArrivalRequest : InspectionSubmitRequest
{
    public double? DistanceMm { get; set; }
    public double? BeltSpeedMmPerSecond { get; set; }
}

public sealed record SimulationTickRequest(DateTime? UtcNow);

public sealed record SimulationTickResponse(int SortedCount, SortingActuationResult Actuation);

public sealed record SimulationStatusResponse(
    bool Enabled,
    bool Active,
    bool Latched,
    int InFlightSimulatedProducts
);

public sealed record ReleaseResponse(TrackResponse Track, SortingActuationResult Actuation);

public sealed record DatasetStartRequest(
    Guid? RecipeId,
    string ProductClass,
    string QualityLabel,
    string ColorLabel,
    string SizeLabel,
    List<string>? DefectLabels,
    string? Notes,
    string CaptureMode,
    int? IntervalMs,
    bool Simulation
);

public sealed record DatasetCameraCaptureRequest(
    string? ProductClass,
    string? QualityLabel,
    string? ColorLabel,
    string? SizeLabel,
    List<string>? DefectLabels,
    string? Notes
);

public sealed record BinCount(string DestinationBin, int Count);

public sealed record ControlRoomEvent(
    DateTime CreatedAt,
    string EventType,
    string? Message,
    string Severity
);

public sealed record ControlRoomResponse(
    string? ActiveRecipe,
    TrackResponse? CurrentProduct,
    InspectionResult? LastInspection,
    int TotalCount,
    int AcceptedCount,
    int RejectedCount,
    int ManualReviewCount,
    double? YieldPercent,
    List<BinCount> BinCounts,
    List<ControlRoomEvent> RecentEvents,
    bool SimulationActive
);

public sealed record DiagnosticsResponse(
    string Api,
    string Database,
    string Mqtt,
    string Vision,
    string Camera,
    DateTime? LastInspectionAt,
    string ModelStatus,
    string? ModelName,
    string? ModelVersion,
    string Esp32,
    string SignalR,
    SimulationStatusResponse Simulation,
    bool HardwareActuationEnabled,
    string Environment,
    string? ActiveRecipe,
    string TimeOfFlightSource,
    string? CameraStreamUrl,
    string? LastFrameAt,
    int? FrameWidth,
    int? FrameHeight,
    int? CameraConsecutiveFailures,
    string? CameraStreamHost,
    ModelDiagnostics Model,
    bool? StillConfigured = null,
    int? ConfiguredStillWidth = null,
    int? ConfiguredStillHeight = null,
    int? LastStillWidth = null,
    int? LastStillHeight = null,
    string? LastStillError = null,
    bool? PreviewFallbackAllowed = null,
    string? SensorPid = null,
    int? ActiveRecipeVersion = null
);

public sealed record ModelDiagnostics(
    string Status,
    string? Name,
    string? Version,
    bool Configured,
    bool Loaded,
    string? Backend,
    string? Task,
    string? Artifact,
    double? LastInferenceLatencyMs,
    string? LoadError,
    double? ConfidenceThreshold
);
