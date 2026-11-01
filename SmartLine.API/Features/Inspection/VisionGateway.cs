using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Inspection;

public interface IVisionFrames
{
    Task<VisionCapturedFrame> ReadAsync(string role, CancellationToken ct);
}

public interface IVisionProbe
{
    Task<VisionProbeResult> ProbeAsync(string? healthUrl, CancellationToken ct);
}

public sealed record VisionCapturedFrame(
    byte[] Jpeg,
    DateTime CapturedAt,
    int? Width,
    int? Height,
    string Role,
    string Origin,
    string CaptureSource = "mjpeg_preview",
    bool PreviewFallback = false
);

public sealed record VisionProbeResult(
    bool Configured,
    bool Reachable,
    string? Status,
    bool? CameraConnected,
    string? StreamHost,
    string? LastFrameAt,
    int? FrameWidth,
    int? FrameHeight,
    int? ConsecutiveFailures,
    bool? ModelConfigured,
    bool? ModelLoaded,
    string? ModelBackend,
    string? ModelTask,
    string? ModelVersion,
    string? ModelArtifact,
    double? LastInferenceLatencyMs,
    string? ModelLoadError,
    double? ConfidenceThreshold,
    bool? StillConfigured = null,
    int? ConfiguredStillWidth = null,
    int? ConfiguredStillHeight = null,
    int? LastStillWidth = null,
    int? LastStillHeight = null,
    string? LastStillError = null,
    bool? PreviewFallbackAllowed = null,
    string? SensorPid = null
)
{
    public static VisionProbeResult NotConfigured() => new(false, false, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

    public static VisionProbeResult Unreachable() => new(true, false, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
}

public static class VisionEndpoint
{
    public static string? FrameUrl(string? healthUrl, string role)
    {
        if (!Uri.TryCreate(healthUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;
        var builder = new UriBuilder(uri)
        {
            Path = $"/frames/{role}",
            Query = "",
        };
        return builder.Uri.ToString();
    }

    public static string? PublicUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return uri.Host;
        return uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    }

    public static string? HostOnly(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return null;
        return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
    }
}

public static class VisionDiagnostics
{
    public const string Online = "ONLINE";
    public const string Offline = "OFFLINE";
    public const string NotConfigured = "NOT_CONFIGURED";
    public const string Unknown = "UNKNOWN";

    public static string MapService(string? healthUrl, VisionProbeResult probe)
    {
        if (string.IsNullOrWhiteSpace(healthUrl) || !probe.Configured)
            return NotConfigured;
        if (!probe.Reachable)
            return Offline;
        return string.Equals(probe.Status, "healthy", StringComparison.OrdinalIgnoreCase) ? Online : Offline;
    }

    public static string MapCamera(string visionStatus, VisionProbeResult probe)
    {
        if (visionStatus != Online)
            return Unknown;
        return probe.CameraConnected switch
        {
            true => Online,
            false => Offline,
            _ => Unknown,
        };
    }
}

public sealed class HttpVisionFrames(IHttpClientFactory factory, IOptions<VisionEndpointOptions> options) : IVisionFrames
{
    public async Task<VisionCapturedFrame> ReadAsync(string role, CancellationToken ct)
    {
        var healthUrl = options.Value.HealthUrl;
        if (string.IsNullOrWhiteSpace(healthUrl))
        {
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                ErrorCodes.DatasetInvalid,
                "Vision is not configured. Set Vision:HealthUrl before capturing from the camera."
            );
        }

        var url = VisionEndpoint.FrameUrl(healthUrl, role)
            ?? throw new ApiException(StatusCodes.Status503ServiceUnavailable, ErrorCodes.DatasetInvalid, "Vision:HealthUrl is not an absolute URL.");
        var client = factory.CreateClient("vision-capture");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ApiException(504, ErrorCodes.DatasetInvalid, "Camera capture timed out."); }
        catch (HttpRequestException) { throw new ApiException(503, ErrorCodes.DatasetInvalid, "Vision cannot be reached."); }
        using var responseScope = response;
        if ((int)response.StatusCode == StatusCodes.Status409Conflict)
        {
            var errorCode = await ReadErrorCodeAsync(response, ct);
            var message = errorCode == "frame_not_available"
                ? "The camera has not produced a frame yet."
                : AppendCode("High-quality still capture failed and preview fallback is disabled.", errorCode);
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.DatasetInvalid, message);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorCode = await ReadErrorCodeAsync(response, ct);
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                ErrorCodes.DatasetInvalid,
                AppendCode("Vision did not return a camera frame.", errorCode)
            );
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length < 2 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            throw new ApiException(
                StatusCodes.Status503ServiceUnavailable,
                ErrorCodes.DatasetInvalid,
                "Vision returned a response that is not a JPEG frame."
            );
        }

        var capturedAt = response.Headers.TryGetValues("X-Captured-At", out var stamps) && DateTimeOffset.TryParse(stamps.FirstOrDefault(), out var parsed)
            ? parsed.UtcDateTime
            : DateTime.UtcNow;
        var origin = Header(response, "X-Frame-Origin") ?? "live_stream";
        var captureSource = Header(response, "X-Capture-Source");
        if (string.IsNullOrWhiteSpace(captureSource))
            captureSource = origin == "still_capture" ? "esp32_capture" : "mjpeg_preview";
        var previewFallback = string.Equals(Header(response, "X-Preview-Fallback"), "true", StringComparison.OrdinalIgnoreCase);
        return new VisionCapturedFrame(
            bytes,
            capturedAt,
            HeaderInt(response, "X-Frame-Width"),
            HeaderInt(response, "X-Frame-Height"),
            role,
            origin,
            captureSource,
            previewFallback
        );
    }

    private static int? HeaderInt(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var parsed) ? parsed : null;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body) || body.Length > 500)
                return null;
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private static string AppendCode(string message, string? code) =>
        string.IsNullOrWhiteSpace(code) ? message : $"{message} ({code})";
}

public sealed class HttpVisionProbe(IHttpClientFactory factory) : IVisionProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<VisionProbeResult> ProbeAsync(string? healthUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(healthUrl))
            return VisionProbeResult.NotConfigured();
        try
        {
            var client = factory.CreateClient("vision-health");
            using var response = await client.GetAsync(healthUrl, ct);
            if (!response.IsSuccessStatusCode)
                return VisionProbeResult.Unreachable();
            var payload = await response.Content.ReadFromJsonAsync<VisionHealthDocument>(Json, ct);
            if (payload is null)
                return VisionProbeResult.Unreachable();
            var camera = payload.Camera;
            var model = payload.Model;
            return new VisionProbeResult(
                true,
                true,
                payload.Status,
                camera?.Connected ?? payload.CameraOnline,
                camera?.StreamHost,
                camera?.LastFrameAt ?? payload.LastFrameAt,
                camera?.FrameWidth ?? payload.FrameWidth,
                camera?.FrameHeight ?? payload.FrameHeight,
                camera?.ConsecutiveFailures,
                model?.Configured,
                model?.Loaded,
                model?.Backend,
                model?.Task,
                model?.ModelVersion,
                model?.Artifact,
                model?.LastInferenceLatencyMs,
                model?.LoadError,
                model?.ConfidenceThreshold,
                camera?.Still?.Configured,
                camera?.Still?.CameraStatus?.ConfiguredStillWidth,
                camera?.Still?.CameraStatus?.ConfiguredStillHeight,
                camera?.Still?.LastWidth ?? camera?.Still?.CameraStatus?.LastStillWidth,
                camera?.Still?.LastHeight ?? camera?.Still?.CameraStatus?.LastStillHeight,
                camera?.Still?.LastError ?? camera?.Still?.CameraStatus?.LastCaptureError,
                camera?.Still?.PreviewFallbackAllowed,
                camera?.Still?.CameraStatus?.SensorPid
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return VisionProbeResult.Unreachable();
        }
    }
}

public sealed class VisionHealthDocument
{
    public string? Status { get; set; }
    public bool? CameraOnline { get; set; }
    public string? LastFrameAt { get; set; }
    public int? FrameWidth { get; set; }
    public int? FrameHeight { get; set; }
    public VisionCameraDocument? Camera { get; set; }
    public VisionModelDocument? Model { get; set; }
}

public sealed class VisionCameraDocument
{
    public bool? Connected { get; set; }
    public string? StreamHost { get; set; }
    public string? LastFrameAt { get; set; }
    public int? FrameWidth { get; set; }
    public int? FrameHeight { get; set; }
    public int? ConsecutiveFailures { get; set; }
    public VisionStillDocument? Still { get; set; }
}

public sealed class VisionStillDocument
{
    public bool? Configured { get; set; }
    public int? LastWidth { get; set; }
    public int? LastHeight { get; set; }
    public string? LastError { get; set; }
    public bool? PreviewFallbackAllowed { get; set; }
    public VisionCameraStatusDocument? CameraStatus { get; set; }
}

public sealed class VisionCameraStatusDocument
{
    public int? ConfiguredStillWidth { get; set; }
    public int? ConfiguredStillHeight { get; set; }
    public int? LastStillWidth { get; set; }
    public int? LastStillHeight { get; set; }
    public string? LastCaptureError { get; set; }
    public string? SensorPid { get; set; }
    public bool? PsramDetected { get; set; }
    public int? PreviewWidth { get; set; }
    public int? PreviewHeight { get; set; }
}

public sealed class VisionModelDocument
{
    public bool? Configured { get; set; }
    public bool? Loaded { get; set; }
    public string? Backend { get; set; }
    public string? Task { get; set; }
    public string? ModelVersion { get; set; }
    public string? Artifact { get; set; }
    public double? LastInferenceLatencyMs { get; set; }
    public string? LoadError { get; set; }
    public double? ConfidenceThreshold { get; set; }
}
