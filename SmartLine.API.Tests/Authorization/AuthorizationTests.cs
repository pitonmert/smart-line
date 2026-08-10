using System.Net.WebSockets;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Authorization;

public sealed class AuthorizationTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    public static TheoryData<HttpMethod, string, object?> ProtectedEndpoints =>
        new()
        {
            { HttpMethod.Get, "/api/dashboard", null },
            { HttpMethod.Get, "/api/products", null },
            { HttpMethod.Get, "/api/logs", null },
            { HttpMethod.Post, "/api/conveyor/start", null },
            { HttpMethod.Post, "/api/conveyor/emergency-stop", null },
            {
                HttpMethod.Post,
                "/api/sensors/environment",
                new { temperature = 24.5m, humidity = 48m }
            },
            {
                HttpMethod.Post,
                "/api/sensors/fire",
                new
                {
                    isDetected = true,
                    deviceEpoch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    fireGeneration = 1,
                }
            },
            {
                HttpMethod.Post,
                "/api/sensors/barcode",
                new { barcode = "AUTH-DEVICE-1", color = "RED" }
            },
            {
                HttpMethod.Post,
                "/api/products",
                new
                {
                    barcode = "AUTH-ADMIN-1",
                    color = "BLUE",
                    status = "OK",
                }
            },
        };

    public static TheoryData<string> ViewerEndpoints =>
        new()
        {
            "/api/dashboard",
            "/api/conveyor/status",
            "/api/sensors/status",
            "/api/products",
            "/api/alerts",
            "/api/statistics/daily",
            "/api/logs",
        };

    [Fact]
    public async Task Health_is_the_only_anonymous_endpoint()
    {
        using var client = Fixture.CreateAnonymousClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Protected_endpoints_return_structured_401(
        HttpMethod method,
        string path,
        object? body
    )
    {
        using var client = Fixture.CreateAnonymousClient();

        var response = await SendAsync(client, method, path, body);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "AUTHENTICATION_REQUIRED");
    }

    [Theory]
    [MemberData(nameof(ViewerEndpoints))]
    public async Task Viewer_can_read_operational_data(string path)
    {
        using var client = Fixture.CreateUserClient("Viewer");

        var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Viewer_cannot_control_the_conveyor()
    {
        using var client = Fixture.CreateUserClient("Viewer");

        var response = await client.PostAsync("/api/conveyor/stop", null);

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, "ACCESS_DENIED");
    }

    [Fact]
    public async Task Operator_can_control_the_conveyor()
    {
        using var client = Fixture.CreateUserClient("Operator");

        var stop = await client.PostAsync("/api/conveyor/stop", null);

        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
    }

    [Fact]
    public async Task Device_key_can_ingest_sensor_data_but_is_not_a_dashboard_identity()
    {
        using var client = Fixture.CreateDeviceClient();

        var ingest = await client.PostAsJsonAsync(
            "/api/sensors/environment",
            new { temperature = 24.5m, humidity = 48m }
        );
        var dashboard = await client.GetAsync("/api/dashboard");

        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);
        await AssertErrorAsync(dashboard, HttpStatusCode.Unauthorized, "AUTHENTICATION_REQUIRED");
    }

    [Fact]
    public async Task Invalid_device_key_is_rejected()
    {
        using var client = Fixture.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Device-Key", "invalid-device-key");

        var response = await client.PostAsJsonAsync(
            "/api/sensors/environment",
            new { temperature = 24.5m, humidity = 48m }
        );

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "AUTHENTICATION_REQUIRED");
    }

    [Fact]
    public async Task Missing_device_key_is_rejected()
    {
        using var client = Fixture.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/sensors/environment",
            new { temperature = 24.5m, humidity = 48m }
        );

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "AUTHENTICATION_REQUIRED");
    }

    [Fact]
    public async Task Operator_can_create_a_product()
    {
        using var client = Fixture.CreateUserClient("Operator");

        var response = await client.PostAsJsonAsync(
            "/api/products",
            new
            {
                barcode = "ADMIN-ONLY-1",
                color = "BLUE",
                status = "OK",
            }
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SignalR_negotiate_requires_a_viewer_identity()
    {
        using var anonymous = Fixture.CreateAnonymousClient();
        using var viewer = Fixture.CreateUserClient("Viewer");

        var rejected = await anonymous.PostAsync(
            "/hubs/dashboard/negotiate?negotiateVersion=1",
            null
        );
        var accepted = await viewer.PostAsync("/hubs/dashboard/negotiate?negotiateVersion=1", null);

        await AssertErrorAsync(rejected, HttpStatusCode.Unauthorized, "AUTHENTICATION_REQUIRED");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task SignalR_websocket_requires_authentication_and_accepts_a_viewer()
    {
        var anonymousClient = Fixture.CreateHubWebSocketClient();
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            anonymousClient.ConnectAsync(
                new Uri("ws://localhost/hubs/dashboard"),
                CancellationToken.None
            )
        );
        Assert.Contains("401", rejected.Message, StringComparison.Ordinal);

        var viewerClient = Fixture.CreateHubWebSocketClient("Viewer");
        using var socket = await viewerClient.ConnectAsync(
            new Uri("ws://localhost/hubs/dashboard"),
            CancellationToken.None
        );
        var handshake = Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e");
        await socket.SendAsync(
            handshake,
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None
        );
        var buffer = new byte[128];
        var received = await socket.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        Assert.Equal("{}\u001e", Encoding.UTF8.GetString(buffer, 0, received.Count));
        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "Test complete",
            CancellationToken.None
        );
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body
    )
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }
}
