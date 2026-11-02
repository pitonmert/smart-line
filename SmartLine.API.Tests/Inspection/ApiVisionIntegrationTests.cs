using System.Diagnostics;
using Microsoft.Extensions.Options;
using SmartLine.API.Features.Inspection;
using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Inspection;

public sealed class ApiVisionIntegrationTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task ActualVisionHttpServerPersistsOneResultForRetriedRequest()
    {
        await Fixture.WithDbContextAsync(db => ContractSeeder.EnsureAsync(db));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            root is not null
            && !Directory.Exists(Path.Combine(root.FullName, "test-support", "vision"))
        )
            root = root.Parent;
        Assert.NotNull(root);
        using var server = Process.Start(
            new ProcessStartInfo(
                "python3",
                Path.Combine(root.FullName, "test-support", "vision", "inspection_server.py")
            )
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                Environment = { ["SMARTLINE_TEST_CAPTURE_DELAY"] = "3.2" },
            }
        )!;
        var serverErrors = server.StandardError.ReadToEndAsync();
        var options = Fixture.GetRequiredService<IOptions<VisionEndpointOptions>>().Value;
        var oldUrl = options.HealthUrl;
        try
        {
            string? port = null;
            try
            {
                port = await server
                    .StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                // Terminate the stalled process below before awaiting its error stream.
            }
            if (!int.TryParse(port, out var portNumber) || portNumber is < 1 or > 65535)
            {
                if (!server.HasExited)
                    server.Kill(true);
                await server.WaitForExitAsync();
                Assert.Fail(
                    $"Vision server did not return a valid port within 10 seconds. Exit code: {server.ExitCode}.\nStandard output: {port ?? "<none>"}\nStandard error:\n{await serverErrors}"
                );
            }
            options.HealthUrl = $"http://127.0.0.1:{port}/health";
            var client = Fixture.CreateClient();
            var id = Guid.NewGuid();
            var initial = await client.PostAsJsonAsync(
                "/api/inspections/vision",
                new { requestId = id }
            );
            Assert.True(
                initial.StatusCode == HttpStatusCode.Created,
                await initial.Content.ReadAsStringAsync()
            );
            var first = await AssertSuccessAsync(initial, HttpStatusCode.Created);
            Assert.Equal(
                "model",
                first.GetProperty("inspection").GetProperty("observationSource").GetString()
            );
            var again = await AssertSuccessAsync(
                await client.PostAsJsonAsync("/api/inspections/vision", new { requestId = id })
            );
            Assert.Equal(id, again.GetProperty("inspection").GetProperty("inspectionId").GetGuid());
            Assert.Equal(
                1,
                await Fixture.WithDbContextAsync(db => db.InspectionRecords.CountAsync())
            );
            Assert.Equal(
                1,
                await Fixture.WithDbContextAsync(db => db.InspectionRecords.CountAsync())
            );
        }
        finally
        {
            options.HealthUrl = oldUrl;
            if (!server.HasExited)
                server.Kill(true);
            await server.WaitForExitAsync();
            await serverErrors;
        }
    }
}
