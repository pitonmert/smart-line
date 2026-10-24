using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Domain.Inspection;
using SmartLine.API.Features.Inspection;
using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Shared;

namespace SmartLine.API.Tests.Inspection;

public class VisionGatewayTests
{
    [Fact]
    public void FrameUrlUsesTheVisionControlRoot()
    {
        Assert.Equal(
            "http://vision:8090/frames/dataset",
            VisionEndpoint.FrameUrl("http://vision:8090/health", "dataset")
        );
    }

    [Fact]
    public void PublicUrlDropsCredentials()
    {
        Assert.Equal("192.168.1.50", VisionEndpoint.PublicUrl("http://user:secret@192.168.1.50:81/stream"));
        Assert.Equal("http://192.168.1.50:81/stream", VisionEndpoint.PublicUrl("http://192.168.1.50:81/stream"));
    }

    [Fact]
    public void ServiceStatusIsOnlineOnlyAfterAHealthyResponse()
    {
        Assert.Equal(VisionDiagnostics.NotConfigured, VisionDiagnostics.MapService("", VisionProbeResult.NotConfigured()));
        Assert.Equal(VisionDiagnostics.Offline, VisionDiagnostics.MapService("http://vision:8090/health", VisionProbeResult.Unreachable()));
        var healthy = new VisionProbeResult(true, true, "healthy", false, "192.168.1.50:81", null, 240, 176, 2, false, false, null, "detect", null, null, null, null, null);
        Assert.Equal(VisionDiagnostics.Online, VisionDiagnostics.MapService("http://vision:8090/health", healthy));
        Assert.Equal(VisionDiagnostics.Offline, VisionDiagnostics.MapCamera(VisionDiagnostics.Online, healthy));
        Assert.Equal(VisionDiagnostics.Unknown, VisionDiagnostics.MapCamera(VisionDiagnostics.Offline, healthy));
    }

    [Fact]
    public async Task ProbeDoesNotClaimOnlineWhenThePortIsClosed()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var probe = new HttpVisionProbe(new SingleClientFactory(new HttpClient { Timeout = TimeSpan.FromMilliseconds(400) }));
        var result = await probe.ProbeAsync($"http://127.0.0.1:{port}/health", CancellationToken.None);
        Assert.Equal(VisionDiagnostics.Offline, VisionDiagnostics.MapService($"http://127.0.0.1:{port}/health", result));
    }

    [Fact]
    public async Task ProbeReportsOnlineFromARealHealthResponse()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var body = Encoding.UTF8.GetBytes("""
                {"status":"healthy","camera":{"connected":true,"streamHost":"192.168.1.50:81","frameWidth":240,"frameHeight":176,"consecutiveFailures":0,"lastFrameAt":"2026-10-06T00:00:00Z"},"model":{"configured":true,"loaded":false,"backend":"openvino","task":"detect","artifact":"demobox.xml","loadError":null}}
                """);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });
        try
        {
            var probe = new HttpVisionProbe(new SingleClientFactory(new HttpClient()));
            var result = await probe.ProbeAsync($"http://127.0.0.1:{port}/health", CancellationToken.None);
            Assert.Equal(VisionDiagnostics.Online, VisionDiagnostics.MapService($"http://127.0.0.1:{port}/health", result));
            Assert.Equal(VisionDiagnostics.Online, VisionDiagnostics.MapCamera(VisionDiagnostics.Online, result));
            Assert.Equal("openvino", result.ModelBackend);
            Assert.Equal(240, result.FrameWidth);
            Assert.False(result.ModelLoaded);
            Assert.Null(result.StillConfigured);
            Assert.Null(result.SensorPid);
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CaptureClientRejectsAMissingHealthUrl()
    {
        var frames = new HttpVisionFrames(
            new SingleClientFactory(new HttpClient()),
            Options.Create(new VisionEndpointOptions { HealthUrl = "" })
        );
        var error = await Assert.ThrowsAsync<ApiException>(() => frames.ReadAsync("dataset", CancellationToken.None));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, error.StatusCode);
        Assert.Equal(ErrorCodes.DatasetInvalid, error.Code);
    }

    [Fact]
    public void LegacyCorrectionTouchesOnlyTheUntouchedSeed()
    {
        var legacy = new ProductRecipe
        {
            Name = ContractSeeder.DemoBoxName,
            Version = 1,
            DefinitionJson = InspectionJson.Serialize(ContractCatalog.LegacyDemoBox()),
        };
        Assert.True(LegacyRecipeCorrection.TryCorrect(legacy));
        var corrected = InspectionJson.Deserialize<ProductRecipeDefinition>(legacy.DefinitionJson);
        Assert.Equal(240, corrected.CameraProfile.SourceWidth);
        Assert.Equal(176, corrected.CameraProfile.SourceHeight);
        Assert.Equal(2, legacy.Version);
        Assert.Contains("240x176", corrected.CameraProfile.Notes, StringComparison.Ordinal);

        var customized = ContractCatalog.LegacyDemoBox();
        customized.CameraProfile.StreamUrl = "http://10.0.0.8:81/stream";
        var edited = new ProductRecipe
        {
            Name = ContractSeeder.DemoBoxName,
            Version = 3,
            DefinitionJson = InspectionJson.Serialize(customized),
        };
        Assert.False(LegacyRecipeCorrection.TryCorrect(edited));
        Assert.Equal(3, edited.Version);
        Assert.Equal(320, InspectionJson.Deserialize<ProductRecipeDefinition>(edited.DefinitionJson).CameraProfile.SourceWidth);
    }

    [Fact]
    public async Task ProbeReadsStillMetadataWithoutReplacingThePreviewSize()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var body = Encoding.UTF8.GetBytes("""
                {"status":"healthy","camera":{"connected":true,"streamHost":"192.168.1.50:81","frameWidth":240,"frameHeight":176,"consecutiveFailures":0,"still":{"configured":true,"lastWidth":640,"lastHeight":480,"lastError":null,"previewFallbackAllowed":false,"cameraStatus":{"sensorPid":"0x2145","psramDetected":true,"previewWidth":240,"previewHeight":176,"configuredStillWidth":640,"configuredStillHeight":480,"lastStillWidth":640,"lastStillHeight":480,"lastCaptureError":null}}}}
                """);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        });
        try
        {
            var probe = new HttpVisionProbe(new SingleClientFactory(new HttpClient()));
            var result = await probe.ProbeAsync($"http://127.0.0.1:{port}/health", CancellationToken.None);
            Assert.Equal(240, result.FrameWidth);
            Assert.Equal(176, result.FrameHeight);
            Assert.True(result.StillConfigured);
            Assert.Equal(640, result.ConfiguredStillWidth);
            Assert.Equal(480, result.LastStillHeight);
            Assert.Equal("0x2145", result.SensorPid);
            Assert.False(result.PreviewFallbackAllowed);
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task CaptureClientKeepsTheMeasuredStillAndRejectsAFailedStill()
    {
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var serve = Task.Run(async () =>
        {
            var success = await listener.GetContextAsync();
            success.Response.ContentType = "image/jpeg";
            success.Response.Headers["X-Frame-Width"] = "640";
            success.Response.Headers["X-Frame-Height"] = "480";
            success.Response.Headers["X-Frame-Origin"] = "still_capture";
            success.Response.Headers["X-Capture-Source"] = "esp32_capture";
            success.Response.Headers["X-Preview-Fallback"] = "false";
            success.Response.ContentLength64 = jpeg.Length;
            await success.Response.OutputStream.WriteAsync(jpeg);
            success.Response.Close();

            var failed = await listener.GetContextAsync();
            var body = Encoding.UTF8.GetBytes("""{"error":"still_capture_failed","previewFallback":false}""");
            failed.Response.StatusCode = 409;
            failed.Response.ContentType = "application/json";
            failed.Response.ContentLength64 = body.Length;
            await failed.Response.OutputStream.WriteAsync(body);
            failed.Response.Close();
        });
        try
        {
            var frames = new HttpVisionFrames(
                new SingleClientFactory(new HttpClient()),
                Options.Create(new VisionEndpointOptions { HealthUrl = $"http://127.0.0.1:{port}/health" })
            );
            var frame = await frames.ReadAsync("dataset", CancellationToken.None);
            Assert.Equal(640, frame.Width);
            Assert.Equal(480, frame.Height);
            Assert.Equal("still_capture", frame.Origin);
            Assert.Equal("esp32_capture", frame.CaptureSource);
            Assert.False(frame.PreviewFallback);
            var error = await Assert.ThrowsAsync<ApiException>(() => frames.ReadAsync("inspection", CancellationToken.None));
            Assert.Equal(StatusCodes.Status409Conflict, error.StatusCode);
            Assert.Contains("High-quality still capture failed", error.Message, StringComparison.Ordinal);
            Assert.Contains("still_capture_failed", error.Message, StringComparison.Ordinal);
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void OlderInspectionAndDatasetRowsKeepTheirDefaults()
    {
        var inspection = InspectionJson.Deserialize<InspectionResult>("""
            {"inspectionId":"11111111-1111-1111-1111-111111111111","productId":"22222222-2222-2222-2222-222222222222","recipeId":"33333333-3333-3333-3333-333333333333","capturedAt":"2026-10-06T00:00:00Z","processedAt":"2026-10-06T00:00:00Z","productClass":"BOX_RED","colorClass":"RED","sizeClass":"LARGE","defectClasses":[],"qualityStatus":"GOOD","confidence":0.9,"boundingBoxes":[],"decision":"PASS","destinationBin":"BIN_A","cameraSource":"","simulation":false,"reasons":[]}
            """);
        Assert.Equal(0, inspection.RecipeVersion);
        Assert.Null(inspection.FrameRole);
        Assert.False(inspection.PreviewFallback);

        var sample = InspectionJson.Deserialize<DatasetManifestEntry>("""
            {"sampleId":"44444444-4444-4444-4444-444444444444","sessionId":"55555555-5555-5555-5555-555555555555","capturedAt":"2026-10-06T00:00:00Z","recipeName":"DemoBox","productClass":"BOX_RED","qualityLabel":"GOOD","colorLabel":"RED","sizeLabel":"LARGE","defectLabels":[],"fileName":"images/a.jpg","sha256":"abc","annotated":false,"boundingBoxes":[],"trigger":"camera","simulation":false,"labelsComplete":true,"camera":{"sensor":"GC2145"},"frameRole":"dataset","sourceWidth":240,"sourceHeight":176}
            """);
        Assert.Equal("import", sample.CaptureSource);
        Assert.False(sample.PreviewFallback);
        Assert.Equal(240, sample.SourceWidth);
    }

    [Fact]
    public async Task CaptureTimeoutAndUnreachableServiceHaveControlledResponses()
    {
        using var client = new HttpClient(new FailingHandler()) { Timeout = TimeSpan.FromMilliseconds(30) };
        var frames = new HttpVisionFrames(new SingleClientFactory(client), Options.Create(new VisionEndpointOptions { HealthUrl = "http://127.0.0.1/health" }));
        var timeout = await Assert.ThrowsAsync<ApiException>(() => frames.ReadAsync("dataset", CancellationToken.None));
        Assert.Equal(504, timeout.StatusCode);
        using var offline = new HttpClient(new FailingHandler(true));
        var unavailable = new HttpVisionFrames(new SingleClientFactory(offline), Options.Create(new VisionEndpointOptions { HealthUrl = "http://127.0.0.1/health" }));
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiException>(() => unavailable.ReadAsync("dataset", CancellationToken.None))).StatusCode);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => frames.ReadAsync("dataset", cancelled.Token));
    }

    private sealed class FailingHandler(bool offline = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (offline) throw new HttpRequestException("offline");
            await Task.Delay(1000, ct);
            throw new HttpRequestException("offline");
        }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
