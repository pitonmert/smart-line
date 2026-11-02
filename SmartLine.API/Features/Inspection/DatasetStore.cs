using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Domain.Inspection;
using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Inspection;

public sealed record DatasetSessionResponse(
    Guid SessionId,
    Guid? RecipeId,
    string RecipeName,
    string Status,
    string ProductClass,
    string QualityLabel,
    string ColorLabel,
    string SizeLabel,
    IReadOnlyList<string> DefectLabels,
    string? Notes,
    string CaptureMode,
    int? IntervalMs,
    int ImageCount,
    IReadOnlyDictionary<string, int> ClassCounts,
    DateTime StartedAt,
    DateTime? StoppedAt,
    bool Simulation,
    string LabelStatus,
    IReadOnlyDictionary<string, int> QualityCounts,
    IReadOnlyDictionary<string, int> DefectCounts,
    IReadOnlyList<DatasetSampleResponse> RecentSamples
);

public sealed record DatasetSampleResponse(
    Guid SampleId,
    string FileName,
    DateTime CapturedAt,
    string ProductClass,
    string QualityLabel,
    string ColorLabel,
    string SizeLabel,
    IReadOnlyList<string> DefectLabels,
    string? Notes,
    string Trigger,
    string FrameRole,
    int? SourceWidth,
    int? SourceHeight,
    string CaptureSource = "import",
    bool PreviewFallback = false
);

public sealed record DatasetCaptureResult(
    DatasetSessionResponse Session,
    DatasetSampleResponse Sample
);

public sealed record DatasetCaptureDetails(
    string? ProductClass = null,
    string? QualityLabel = null,
    string? ColorLabel = null,
    string? SizeLabel = null,
    IReadOnlyList<string>? DefectLabels = null,
    string? Notes = null,
    string Trigger = "manual",
    string FrameRole = "import",
    string Origin = "import",
    int? SourceWidth = null,
    int? SourceHeight = null,
    bool ReplaceLabels = false,
    string CaptureSource = "import",
    bool PreviewFallback = false
);

public sealed class DatasetStore(ApplicationDbContext db, IOptions<DatasetOptions> options)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();
    private static readonly SemaphoreSlim Writes = new(1, 1);

    public async Task<DatasetSessionResponse> StartAsync(
        Guid? recipeId,
        string productClass,
        string qualityLabel,
        string colorLabel,
        string sizeLabel,
        IReadOnlyList<string> defectLabels,
        string? notes,
        string captureMode,
        int? intervalMs,
        bool simulation,
        CancellationToken ct
    ) =>
        await WriteAsync(
            () =>
                StartCoreAsync(
                    recipeId,
                    productClass,
                    qualityLabel,
                    colorLabel,
                    sizeLabel,
                    defectLabels,
                    notes,
                    captureMode,
                    intervalMs,
                    simulation,
                    ct
                ),
            ct
        );

    private async Task<DatasetSessionResponse> StartCoreAsync(
        Guid? recipeId,
        string productClass,
        string qualityLabel,
        string colorLabel,
        string sizeLabel,
        IReadOnlyList<string> defectLabels,
        string? notes,
        string captureMode,
        int? intervalMs,
        bool simulation,
        CancellationToken ct
    )
    {
        var mode = (captureMode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("manual" or "interval" or "hardware"))
            throw new ValidationException(
                "Capture mode must be manual, interval, or hardware.",
                ErrorCodes.DatasetInvalid
            );
        if (mode == "hardware" && !simulation)
            throw new ValidationException(
                "Hardware capture is not integrated.",
                ErrorCodes.DatasetInvalid
            );
        if (mode == "interval" && (intervalMs is null or < 200 or > 60_000))
            throw new ValidationException(
                "Interval capture must be between 200 and 60000 milliseconds.",
                ErrorCodes.DatasetInvalid
            );
        if (await db.DatasetSessions.AnyAsync(session => session.Status == "ACTIVE", ct))
            throw new ApiException(
                StatusCodes.Status409Conflict,
                ErrorCodes.DatasetInvalid,
                "A dataset session is already active. Stop it before starting another."
            );

        var recipe = recipeId is null
            ? await db.ProductRecipes.AsNoTracking().FirstOrDefaultAsync(item => item.IsActive, ct)
            : await db
                .ProductRecipes.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == recipeId, ct);
        if (recipe is null)
            throw new NotFoundException("Product recipe");

        var session = new DatasetSession
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            RecipeName = recipe.Name,
            Status = "ACTIVE",
            ProductClass = Normalize(productClass),
            QualityLabel = Normalize(qualityLabel),
            ColorLabel = Normalize(colorLabel),
            SizeLabel = Normalize(sizeLabel),
            DefectLabelsJson = InspectionJson.Serialize(
                defectLabels.Select(Normalize).Where(item => item.Length > 0).Distinct().ToList()
            ),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CaptureMode = mode,
            IntervalMs = mode == "interval" ? intervalMs : null,
            ImageCount = 0,
            StoragePath = Path.Combine(Root(), sessionPath(Guid.Empty)),
            StartedAt = DateTime.UtcNow,
            IsSimulation = simulation,
        };
        await ApplyLabelsAsync(
            session,
            new DatasetCaptureDetails(
                productClass,
                qualityLabel,
                colorLabel,
                sizeLabel,
                defectLabels,
                notes
            ),
            ct
        );
        session.StoragePath = Path.Combine(Root(), session.Id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(session.StoragePath, "images"));
        await File.WriteAllTextAsync(
            Path.Combine(session.StoragePath, "session.json"),
            InspectionJson.Serialize(ToResponse(session, [])),
            ct
        );
        db.DatasetSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return ToResponse(session, []);

        static string sessionPath(Guid id) => id.ToString("N");
    }

    public async Task<DatasetSessionResponse> StopAsync(Guid sessionId, CancellationToken ct) =>
        await WriteAsync(() => StopCoreAsync(sessionId, ct), ct, sessionId);

    private async Task<DatasetSessionResponse> StopCoreAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await RequiredAsync(sessionId, ct);
        if (session.Status != "ACTIVE")
            throw new ValidationException(
                "The dataset session is already stopped.",
                ErrorCodes.DatasetInvalid
            );
        session.Status = "STOPPED";
        session.StoppedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return await RespondAsync(session);
    }

    public async Task<DatasetCaptureResult> CaptureAsync(
        Guid sessionId,
        byte[] image,
        string? sourceFileName,
        DatasetCaptureDetails? details,
        CancellationToken ct
    ) =>
        await WriteAsync(
            () => CaptureCoreAsync(sessionId, image, sourceFileName, details, ct),
            ct,
            sessionId
        );

    private async Task<DatasetCaptureResult> CaptureCoreAsync(
        Guid sessionId,
        byte[] image,
        string? sourceFileName,
        DatasetCaptureDetails? details,
        CancellationToken ct
    )
    {
        var session = await RequiredAsync(sessionId, ct);
        if (session.Status != "ACTIVE")
            throw new ValidationException(
                "The dataset session is stopped.",
                ErrorCodes.DatasetInvalid
            );
        if (session.CaptureMode == "hardware" && !session.IsSimulation)
            throw new ValidationException(
                "Hardware-trigger capture is reserved for a future VL53L1X event. Use manual capture or mark the session as simulation.",
                ErrorCodes.DatasetInvalid
            );
        if (image.Length == 0 || image.Length > 900_000)
            throw new ValidationException(
                "Image must be a non-empty file under 900 KB.",
                ErrorCodes.DatasetInvalid
            );
        // Vision owns image decoding; the API checks the camera payload before storing it.
        if (
            image.Length < 4
            || image[0] != 0xFF
            || image[1] != 0xD8
            || image[2] != 0xFF
            || image[^2] != 0xFF
            || image[^1] != 0xD9
        )
            throw new ValidationException(
                "Vision must return a JPEG frame.",
                ErrorCodes.DatasetInvalid
            );
        var capture = details ?? new DatasetCaptureDetails();
        var gate = Gates.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await ApplyLabelsAsync(
                session,
                capture.ReplaceLabels
                    ? capture
                    : new DatasetCaptureDetails(
                        session.ProductClass,
                        session.QualityLabel,
                        session.ColorLabel,
                        session.SizeLabel,
                        InspectionJson.Deserialize<List<string>>(session.DefectLabelsJson),
                        session.Notes
                    ),
                ct
            );
            var sampleId = Guid.NewGuid();
            var capturedAt = DateTime.UtcNow;
            var fileName = $"{capturedAt:yyyyMMddTHHmmssfff}-{sampleId:N}.jpg";
            var relative = Path.Combine("images", fileName);
            var fullPath = Path.Combine(session.StoragePath, relative);
            await File.WriteAllBytesAsync(fullPath, image, ct);
            var defects = InspectionJson.Deserialize<List<string>>(session.DefectLabelsJson);
            var entry = new DatasetManifestEntry(
                sampleId,
                session.Id,
                capturedAt,
                session.RecipeId,
                session.RecipeName,
                session.ProductClass,
                session.QualityLabel,
                session.ColorLabel,
                session.SizeLabel,
                defects,
                session.Notes,
                relative.Replace('\\', '/'),
                Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
                string.IsNullOrWhiteSpace(sourceFileName) ? null : Path.GetFileName(sourceFileName),
                Annotated: false,
                BoundingBoxes: [],
                Trigger: capture.Trigger,
                Simulation: session.IsSimulation,
                LabelsComplete: session.ProductClass.Length > 0,
                Camera: new Dictionary<string, string>
                {
                    ["sensor"] = "GC2145",
                    ["pixelFormat"] = "RGB565",
                    ["transport"] = "MJPEG",
                    ["frameRole"] = capture.FrameRole,
                    ["origin"] = capture.Origin,
                    ["captureSource"] = capture.CaptureSource,
                    ["previewFallback"] = capture.PreviewFallback ? "true" : "false",
                },
                Rejected: false,
                FrameRole: capture.FrameRole,
                SourceWidth: capture.SourceWidth,
                SourceHeight: capture.SourceHeight,
                CaptureSource: capture.CaptureSource,
                PreviewFallback: capture.PreviewFallback
            );
            var manifestPath = Path.Combine(session.StoragePath, "manifest.jsonl");
            var previous = File.Exists(manifestPath)
                ? await File.ReadAllTextAsync(manifestPath, ct)
                : "";
            await AtomicWriteAsync(
                manifestPath,
                previous + InspectionJson.Serialize(entry) + "\n",
                ct
            );
            var entries = await ReadManifestAsync(session);
            session.ImageCount = entries.Count(item => !item.Rejected);
            await db.SaveChangesAsync(ct);
            var response = ToResponse(session, entries);
            var sample = response.RecentSamples.First(item => item.SampleId == sampleId);
            return new DatasetCaptureResult(response, sample);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DatasetSessionResponse> RejectAsync(
        Guid sessionId,
        Guid sampleId,
        CancellationToken ct
    ) => await WriteAsync(() => RejectCoreAsync(sessionId, sampleId, ct), ct, sessionId);

    private async Task<DatasetSessionResponse> RejectCoreAsync(
        Guid sessionId,
        Guid sampleId,
        CancellationToken ct
    )
    {
        var session = await RequiredAsync(sessionId, ct);
        if (session.Status != "ACTIVE")
            throw new ValidationException(
                "A stopped dataset session cannot discard captures.",
                ErrorCodes.DatasetInvalid
            );
        var gate = Gates.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var entries = await ReadManifestAsync(session);
            var target = entries.FirstOrDefault(item =>
                item.SampleId == sampleId && !item.Rejected
            );
            if (target is null)
                throw new NotFoundException("Dataset sample", sampleId);
            var fileName = Path.GetFileName(target.FileName);
            var fullPath = Path.Combine(session.StoragePath, "images", fileName);
            // Dosya commit sonrasında kaldırılır; hata olursa manifest geri yüklenebilir.
            _pendingDelete = fullPath;
            var updated = entries
                .Select(item => item.SampleId == sampleId ? item with { Rejected = true } : item)
                .ToList();
            var manifestPath = Path.Combine(session.StoragePath, "manifest.jsonl");
            var body = string.Join('\n', updated.Select(InspectionJson.Serialize));
            if (body.Length > 0)
                body += "\n";
            await AtomicWriteAsync(manifestPath, body, ct);
            session.ImageCount = updated.Count(item => !item.Rejected);
            await db.SaveChangesAsync(ct);
            return ToResponse(session, updated);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DatasetSessionResponse?> GetAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db
            .DatasetSessions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == sessionId, ct);
        return session is null ? null : await RespondAsync(session);
    }

    public async Task<IReadOnlyList<DatasetSessionResponse>> ListAsync(CancellationToken ct)
    {
        var sessions = await db
            .DatasetSessions.AsNoTracking()
            .OrderByDescending(item => item.Status == "ACTIVE")
            .ThenByDescending(item => item.StartedAt)
            .Take(50)
            .ToListAsync(ct);
        var results = new List<DatasetSessionResponse>(sessions.Count);
        foreach (var session in sessions)
            results.Add(await RespondAsync(session));
        return results;
    }

    public async Task<(byte[] Bytes, string ContentType)?> ReadImageAsync(
        Guid sessionId,
        string fileName,
        CancellationToken ct
    )
    {
        var session = await db
            .DatasetSessions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == sessionId, ct);
        if (session is null)
            return null;
        var safeName = Path.GetFileName(fileName);
        if (safeName != fileName || safeName.Contains("..", StringComparison.Ordinal))
            throw new ValidationException("Image name is invalid.", ErrorCodes.DatasetInvalid);
        var path = Path.Combine(session.StoragePath, "images", safeName);
        if (!File.Exists(path))
            return null;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var type = safeName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";
        return (bytes, type);
    }

    private async Task<DatasetSession> RequiredAsync(Guid sessionId, CancellationToken ct) =>
        await db.DatasetSessions.FirstOrDefaultAsync(item => item.Id == sessionId, ct)
        ?? throw new NotFoundException("Dataset session", sessionId);

    private async Task<DatasetSessionResponse> RespondAsync(DatasetSession session) =>
        ToResponse(session, await ReadManifestAsync(session));

    private static DatasetSessionResponse ToResponse(
        DatasetSession session,
        IReadOnlyList<DatasetManifestEntry> entries
    )
    {
        var defects = InspectionJson.Deserialize<List<string>>(session.DefectLabelsJson);
        var active = entries.Where(item => !item.Rejected).ToList();
        var labelStatus = session.ProductClass.Length == 0 ? "unlabeled" : "class-labeled";
        return new DatasetSessionResponse(
            session.Id,
            session.RecipeId,
            session.RecipeName,
            session.Status,
            session.ProductClass,
            session.QualityLabel,
            session.ColorLabel,
            session.SizeLabel,
            defects,
            session.Notes,
            session.CaptureMode,
            session.IntervalMs,
            active.Count,
            Count(
                active,
                item =>
                    string.IsNullOrWhiteSpace(item.ProductClass) ? "(unlabeled)" : item.ProductClass
            ),
            session.StartedAt,
            session.StoppedAt,
            session.IsSimulation,
            labelStatus + "; bounding boxes are not inferred",
            Count(
                active,
                item =>
                    string.IsNullOrWhiteSpace(item.QualityLabel) ? "(unlabeled)" : item.QualityLabel
            ),
            CountDefects(active),
            active.OrderByDescending(item => item.CapturedAt).Take(12).Select(ToSample).ToList()
        );
    }

    private static DatasetSampleResponse ToSample(DatasetManifestEntry entry) =>
        new(
            entry.SampleId,
            Path.GetFileName(entry.FileName),
            entry.CapturedAt,
            entry.ProductClass,
            entry.QualityLabel,
            entry.ColorLabel,
            entry.SizeLabel,
            entry.DefectLabels,
            entry.Notes,
            entry.Trigger,
            string.IsNullOrWhiteSpace(entry.FrameRole) ? "dataset" : entry.FrameRole,
            entry.SourceWidth,
            entry.SourceHeight,
            string.IsNullOrWhiteSpace(entry.CaptureSource) ? "import" : entry.CaptureSource,
            entry.PreviewFallback
        );

    private async Task ApplyLabelsAsync(
        DatasetSession session,
        DatasetCaptureDetails details,
        CancellationToken ct
    )
    {
        if (session.RecipeId is null)
            throw new ValidationException(
                "The dataset session has no product recipe.",
                ErrorCodes.DatasetInvalid
            );
        var recipe =
            await db
                .ProductRecipes.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == session.RecipeId, ct)
            ?? throw new NotFoundException("Product recipe", session.RecipeId);
        var definition = InspectionJson.Deserialize<ProductRecipeDefinition>(recipe.DefinitionJson);
        var product = Normalize(details.ProductClass);
        var quality = Normalize(details.QualityLabel);
        var color = Normalize(details.ColorLabel);
        var size = Normalize(details.SizeLabel);
        var defects = (details.DefectLabels ?? [])
            .Select(Normalize)
            .Where(item => item.Length > 0)
            .Distinct()
            .ToList();
        var errors = new List<string>();
        if (details.Notes?.Length > 1000)
            errors.Add("Notes exceed 1000 characters.");
        if (
            definition.ProductClasses.Count > 0
            && definition.ProductClasses.All(item => item.Code != product)
        )
            errors.Add("Product class is not defined by the recipe.");
        if (quality is not (InspectionVocabulary.Good or InspectionVocabulary.Defective))
            errors.Add("Quality label must be GOOD or DEFECTIVE.");
        if (
            definition.AllowedColors.Count > 0
            && definition.AllowedColors.All(item => item.Code != color)
        )
            errors.Add("Color is not defined by the recipe.");
        if (
            definition.SizeClasses.Count > 0
            && definition.SizeClasses.All(item => item.Code != size)
        )
            errors.Add("Size is not defined by the recipe.");
        var knownDefects = definition
            .DefectClasses.Select(item => item.Code)
            .ToHashSet(StringComparer.Ordinal);
        if (defects.Any(item => !knownDefects.Contains(item)))
            errors.Add("A defect label is not defined by the recipe.");
        if (errors.Count > 0)
            throw new ValidationException(string.Join(' ', errors), ErrorCodes.DatasetInvalid);
        session.ProductClass = product;
        session.QualityLabel = quality;
        session.ColorLabel = color;
        session.SizeLabel = size;
        session.DefectLabelsJson = InspectionJson.Serialize(defects);
        session.Notes = string.IsNullOrWhiteSpace(details.Notes) ? null : details.Notes.Trim();
    }

    private static async Task<List<DatasetManifestEntry>> ReadManifestAsync(DatasetSession session)
    {
        var manifest = Path.Combine(session.StoragePath, "manifest.jsonl");
        var entries = new List<DatasetManifestEntry>();
        if (!File.Exists(manifest))
            return entries;
        var lines = await File.ReadAllLinesAsync(manifest);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var entry = JsonSerializer.Deserialize<DatasetManifestEntry>(
                line,
                InspectionJson.Options
            );
            if (entry is not null)
                entries.Add(entry);
        }

        return entries;
    }

    private static Dictionary<string, int> Count(
        IEnumerable<DatasetManifestEntry> entries,
        Func<DatasetManifestEntry, string> key
    )
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = key(entry);
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        return counts;
    }

    private static Dictionary<string, int> CountDefects(IReadOnlyList<DatasetManifestEntry> entries)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.DefectLabels.Count == 0)
            {
                counts["(none)"] = counts.GetValueOrDefault("(none)") + 1;
                continue;
            }

            foreach (var defect in entry.DefectLabels)
                counts[defect] = counts.GetValueOrDefault(defect) + 1;
        }

        return counts;
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        // Yeniden başlatmada manifest esas alınır; yarım kalan dosyalar ve sayaçlar uzlaştırılır.
        await WriteAsync(
            async () =>
            {
                var sessions = await db.DatasetSessions.ToListAsync(ct);
                foreach (var session in sessions)
                {
                    Directory.CreateDirectory(Path.Combine(session.StoragePath, "images"));
                    var entries = await ReadManifestAsync(session);
                    var active = entries.Where(e => !e.Rejected).ToList();
                    if (
                        active.Any(e => !File.Exists(Path.Combine(session.StoragePath, e.FileName)))
                    )
                    {
                        // Dataset kaybı diğer API işlevlerinin açılmasını engellemez.
                        session.Status = "RECOVERY_REQUIRED";
                        session.StoppedAt ??= DateTime.UtcNow;
                        session.ImageCount = active.Count;
                        continue;
                    }
                    var kept = active.Select(e => Path.GetFileName(e.FileName)).ToHashSet();
                    foreach (
                        var file in Directory.GetFiles(Path.Combine(session.StoragePath, "images"))
                    )
                        if (!kept.Contains(Path.GetFileName(file)))
                            File.Delete(file);
                    session.ImageCount = active.Count;
                }
                await db.SaveChangesAsync(ct);
                return true;
            },
            ct
        );
    }

    private string? _pendingDelete;

    private async Task<T> WriteAsync<T>(
        Func<Task<T>> action,
        CancellationToken ct,
        Guid? sessionId = null
    )
    {
        await Writes.WaitAsync(ct);
        try
        {
            await using var transaction = await InspectionTransaction.BeginAsync(db, ct);
            var root = Root();
            var folders = Directory.GetDirectories(root).ToHashSet();
            var scope = sessionId is null
                ? null
                : Path.Combine(root, sessionId.Value.ToString("N"));
            var files =
                scope is not null && Directory.Exists(scope)
                    ? Directory.GetFiles(scope, "*", SearchOption.AllDirectories)
                    : [];
            var manifests = files
                .Where(path => path.EndsWith(".json") || path.EndsWith(".jsonl"))
                .ToDictionary(path => path, File.ReadAllBytes);
            var images = files.ToHashSet();
            try
            {
                var result = await action();
                foreach (var entry in db.ChangeTracker.Entries<DatasetSession>())
                    await AtomicWriteAsync(
                        Path.Combine(entry.Entity.StoragePath, "session.json"),
                        InspectionJson.Serialize(await RespondAsync(entry.Entity)),
                        ct
                    );
                await transaction.CommitAsync(ct);
                // Commit başarılı; silinemeyen gereksiz dosya manifestte rejected kalır.
                if (_pendingDelete is not null)
                {
                    try
                    {
                        File.Delete(_pendingDelete);
                    }
                    catch (Exception error)
                        when (error is IOException or UnauthorizedAccessException) { }
                }
                return result;
            }
            catch
            {
                foreach (
                    var path in scope is not null && Directory.Exists(scope)
                        ? Directory.GetFiles(scope, "*", SearchOption.AllDirectories)
                        : []
                )
                    if (!images.Contains(path))
                        File.Delete(path);
                foreach (var item in manifests)
                    await File.WriteAllBytesAsync(item.Key, item.Value, CancellationToken.None);
                foreach (var folder in Directory.GetDirectories(root))
                    if (!folders.Contains(folder))
                        Directory.Delete(folder, true);
                throw;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ApiException(
                503,
                ErrorCodes.DatasetInvalid,
                "Dataset storage is unavailable. Check storage and backup status.",
                error
            );
        }
        finally
        {
            _pendingDelete = null;
            Writes.Release();
        }
    }

    private static async Task AtomicWriteAsync(string path, string body, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, body, new UTF8Encoding(false), ct);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private string Root()
    {
        var root = options.Value.Root;
        if (string.IsNullOrWhiteSpace(root))
            throw new ValidationException(
                "Dataset root is not configured.",
                ErrorCodes.DatasetInvalid
            );
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();
}

public sealed record DatasetManifestEntry(
    Guid SampleId,
    Guid SessionId,
    DateTime CapturedAt,
    Guid? RecipeId,
    string RecipeName,
    string ProductClass,
    string QualityLabel,
    string ColorLabel,
    string SizeLabel,
    IReadOnlyList<string> DefectLabels,
    string? Notes,
    string FileName,
    string Sha256,
    string? SourceFileName,
    bool Annotated,
    IReadOnlyList<BoundingBox> BoundingBoxes,
    string Trigger,
    bool Simulation,
    bool LabelsComplete,
    IReadOnlyDictionary<string, string> Camera,
    bool Rejected = false,
    string FrameRole = "dataset",
    int? SourceWidth = null,
    int? SourceHeight = null,
    string CaptureSource = "import",
    bool PreviewFallback = false
);
