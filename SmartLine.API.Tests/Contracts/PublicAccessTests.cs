using System.Net.WebSockets;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Contracts;

public sealed class PublicAccessTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Theory]
    [InlineData("/api/dashboard")]
    [InlineData("/api/products")]
    [InlineData("/api/logs")]
    [InlineData("/api/conveyor/status")]
    [InlineData("/api/recipes/active/snapshot")]
    public async Task ReadsDoNotRequireCredentials(string path)
    {
        await Fixture.WithDbContextAsync(db =>
            SmartLine.API.Features.Inspection.ContractSeeder.EnsureAsync(db)
        );
        using var client = Fixture.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SensorWritesAndConveyorControlDoNotRequireCredentials()
    {
        using var client = Fixture.CreateClient();
        await AssertSuccessAsync(
            await client.PostAsJsonAsync(
                "/api/sensors/environment",
                new { temperature = 24m, humidity = 48m }
            )
        );
        await AssertSuccessAsync(
            await client.PostAsJsonAsync(
                "/api/sensors/barcode",
                new { barcode = "PUBLIC-1", color = "RED" }
            )
        );
        await AssertSuccessAsync(await client.PostAsync("/api/conveyor/start", null));
        await AssertSuccessAsync(await client.PostAsync("/api/conveyor/emergency-stop", null));
        await AssertErrorAsync(
            await client.PostAsync("/api/conveyor/start", null),
            HttpStatusCode.Conflict,
            "CONVEYOR_EMERGENCY_LATCHED"
        );
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/auth/logout")]
    [InlineData("/api/admin/users")]
    public async Task RetiredIdentityEndpointsReturnNotFound(string path)
    {
        using var client = Fixture.CreateClient();
        using var response = await client.PostAsJsonAsync(path, new { });
        await AssertErrorAsync(response, HttpStatusCode.NotFound, "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task SignalRConnectsWithoutAToken()
    {
        using var client = Fixture.CreateClient();
        using var negotiate = await client.PostAsync(
            "/hubs/dashboard/negotiate?negotiateVersion=1",
            null
        );
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await Fixture
            .CreateHubWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/hubs/dashboard"), timeout.Token);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"),
            WebSocketMessageType.Text,
            true,
            timeout.Token
        );
        var buffer = new byte[128];
        var received = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal("{}\u001e", Encoding.UTF8.GetString(buffer, 0, received.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test complete", timeout.Token);
    }
}
