using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartLine.API.Domain.Inspection;
using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Inspection;

/// <summary>Tek bir kamera incelemesini tanımlayan, tekrar gönderilebilir istek.</summary>
public sealed record VisionInspectionRequest(Guid RequestId);

public static class VisionInspection
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IEndpointRouteBuilder MapVisionInspection(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/inspections/vision", InspectAsync);
        app.MapGet("/api/camera/preview", async (IVisionFrames frames, CancellationToken ct) =>
        {
            var frame = await frames.ReadAsync("preview", ct);
            return Results.File(frame.Jpeg, "image/jpeg");
        });
        return app;
    }

    internal static async Task ValidateRecipeAsync(ProductRecipeDefinition recipe, IHttpClientFactory factory, VisionEndpointOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipe.Model.ArtifactFileName)) return;
        var url = VisionEndpoint.FrameUrl(options.HealthUrl, "inspection");
        if (url is null) throw new ApiException(409, ErrorCodes.RecipeInvalid, "Vision must be configured before activating a model recipe.");
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(url), "/validate-recipe"));
            message.Content = new StringContent(InspectionJson.Serialize(new { recipe = new { definition = recipe } }), System.Text.Encoding.UTF8, "application/json");
            using var response = await factory.CreateClient("vision-capture").SendAsync(message, ct);
            if (!response.IsSuccessStatusCode) throw new ApiException(409, ErrorCodes.RecipeInvalid, "Recipe does not match the configured detection artifact. Restart Vision with the matching artifact.");
        }
        catch (HttpRequestException) { throw new ApiException(503, "VISION_UNAVAILABLE", "Vision cannot validate the model recipe."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ApiException(504, "VISION_TIMEOUT", "Model validation timed out."); }
    }

    private static async Task<IResult> InspectAsync(VisionInspectionRequest request, ApplicationDbContext db,
        InspectionWorkflow workflow, IHttpClientFactory factory, IOptions<VisionEndpointOptions> options, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty) throw new ValidationException("RequestId is required.");
        await Gate.WaitAsync(ct);
        try
        {
            // Tekrar gönderilen olay aynı kalıcı sonucu döndürür; kamera yeniden çalıştırılmaz.
            var previous = await db.InspectionRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RequestId, ct);
            if (previous is not null)
            {
                var track = await db.ProductTracks.AsNoTracking().SingleAsync(t => t.Id == previous.ProductId, ct);
                return Results.Ok(ApiResponse<CompletedInspection>.Success(new(InspectionJson.Deserialize<InspectionResult>(previous.ResultJson), InspectionWorkflow.ToResponse(track), workflow.Actuation())));
            }
            var recipe = await db.ProductRecipes.AsNoTracking().SingleOrDefaultAsync(r => r.IsActive, ct)
                ?? throw new NotFoundException("Active product recipe");
            var definition = InspectionJson.Deserialize<ProductRecipeDefinition>(recipe.DefinitionJson);
            var profile = await db.CalibrationProfiles.AsNoTracking().SingleOrDefaultAsync(r => definition.CalibrationProfileId == null ? r.IsActive : r.Id == definition.CalibrationProfileId, ct);
            if (definition.CalibrationProfileId is not null && profile is null) throw new NotFoundException("Calibration profile");
            var calibration = profile is null ? null : InspectionJson.Deserialize<CalibrationDefinition>(profile.DefinitionJson);
            if (calibration is not null && CalibrationValidator.UsesSimulation(calibration)) throw new ApiException(409, ErrorCodes.CalibrationInvalid, "Real inspection cannot use simulated sensor calibration.");
            var url = VisionEndpoint.FrameUrl(options.Value.HealthUrl, "inspection");
            if (url is null) throw new ApiException(503, "VISION_UNAVAILABLE", "Vision is not configured.");
            url = new Uri(new Uri(url), "/inspect").ToString();
            using var message = new HttpRequestMessage(HttpMethod.Post, url);
            message.Content = new StringContent(JsonSerializer.Serialize(new { source = "api", recipe = new { id = recipe.Id, version = recipe.Version, definition }, calibration }, InspectionJson.Options), System.Text.Encoding.UTF8, "application/json");
            using var response = await factory.CreateClient("vision-capture").SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                var code = failure.TryGetProperty("error", out var error) ? error.GetString() : "unavailable";
                throw new ApiException(response.StatusCode == System.Net.HttpStatusCode.Conflict ? 409 : 503, "VISION_UNAVAILABLE", $"Vision inspection failed ({code}). Check model, recipe and camera status.");
            }
            if (response.Content.Headers.ContentLength > 1_000_000) throw new ApiException(503, "VISION_INVALID", "Vision result is too large.");
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (!payload.TryGetProperty("recipeId", out var id) || id.GetGuid() != recipe.Id || payload.GetProperty("recipeVersion").GetInt32() != recipe.Version)
                throw new ApiException(409, ErrorCodes.RecipeInvalid, "Vision returned a different recipe snapshot.");
            if (!payload.GetProperty("inference").GetProperty("available").GetBoolean())
                throw new ApiException(503, "VISION_UNAVAILABLE", "Inference did not run.");
            var observation = payload.GetProperty("observation").Deserialize<InspectionObservation>(InspectionJson.Options)
                ?? throw new ApiException(503, "VISION_INVALID", "Vision observation is missing.");
            observation.ObservationSource = "model";
            observation.TriggerSource = "api";
            observation.Simulation = false;
            observation.CapturedAt = payload.GetProperty("frame").GetProperty("capturedAt").GetDateTimeOffset().UtcDateTime;
            var completed = await workflow.SubmitAsync(observation, recipe.Id, null, null, null, ct, recipe.Version, request.RequestId, profile?.DefinitionJson, profile?.Id, profile?.Version);
            return Results.Created($"/api/inspections/{completed.Inspection.InspectionId}", ApiResponse<CompletedInspection>.Success(completed));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ApiException(504, "VISION_TIMEOUT", "Vision inspection exceeded its time budget."); }
        catch (HttpRequestException) { throw new ApiException(503, "VISION_UNAVAILABLE", "Vision cannot be reached."); }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException) { throw new ApiException(503, "VISION_INVALID", "Vision returned an incomplete result."); }
        catch (JsonException) { throw new ApiException(503, "VISION_INVALID", "Vision returned an invalid result."); }
        finally { Gate.Release(); }
    }
}
