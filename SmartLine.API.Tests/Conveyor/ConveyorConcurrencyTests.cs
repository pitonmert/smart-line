using SmartLine.API.Domain.Enums;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Conveyor;

public sealed class ConveyorConcurrencyTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Concurrent_start_and_emergency_requests_leave_mqtt_and_database_safe()
    {
        using var client = Fixture.CreateUserClient("Operator");

        var requests = Enumerable
            .Range(0, 12)
            .SelectMany(_ =>
                new[]
                {
                    client.PostAsync("/api/conveyor/start", null),
                    client.PostAsync("/api/conveyor/emergency-stop", null),
                }
            );
        var responses = await Task.WhenAll(requests);

        Assert.All(
            responses.Where((_, index) => index % 2 == 1),
            response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        );
        Assert.All(
            responses.Where((_, index) => index % 2 == 0),
            response =>
                Assert.Contains(
                    response.StatusCode,
                    new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }
                )
        );

        var persisted = await Fixture.WithDbContextAsync(async db => new
        {
            Conveyor = await db.ConveyorSettings.AsNoTracking().SingleAsync(),
            Alerts = await db.Alerts.CountAsync(alert =>
                alert.AlertType == AlertType.EMERGENCY_STOP
            ),
            Logs = await db.SystemLogs.CountAsync(log => log.EventType == EventType.EMERGENCY_STOP),
        });
        Assert.False(persisted.Conveyor.IsRunning);
        Assert.True(persisted.Conveyor.IsEmergencyStopped);
        Assert.Equal(1, persisted.Alerts);
        Assert.Equal(1, persisted.Logs);

        var stateCalls = Fixture
            .Mqtt.Calls.Where(call =>
                call.Operation == nameof(IMqttService.PublishConveyorStateAsync)
            )
            .ToArray();
        Assert.NotEmpty(stateCalls);
        Assert.Equal(
            "running=False;emergency=True;resetGeneration=0;acknowledgedFireEpoch=;acknowledgedFireGeneration=0",
            stateCalls[^1].Payload
        );
    }

    [Fact]
    public async Task Concurrent_fire_messages_create_one_transition_audit()
    {
        using var device = Fixture.CreateDeviceClient();

        var responses = await Task.WhenAll(
            Enumerable
                .Range(0, 16)
                .Select(_ =>
                    device.PostAsJsonAsync(
                        "/api/sensors/fire",
                        new
                        {
                            isDetected = true,
                            deviceEpoch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                            fireGeneration = 1,
                        }
                    )
                )
        );
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        var persisted = await Fixture.WithDbContextAsync(async db => new
        {
            Conveyor = await db.ConveyorSettings.AsNoTracking().SingleAsync(),
            Alerts = await db.Alerts.CountAsync(alert => alert.AlertType == AlertType.FIRE),
            Logs = await db.SystemLogs.CountAsync(log => log.EventType == EventType.FIRE_DETECTED),
        });
        Assert.False(persisted.Conveyor.IsRunning);
        Assert.True(persisted.Conveyor.IsEmergencyStopped);
        Assert.True(persisted.Conveyor.IsFireDetected);
        Assert.Equal(1, persisted.Alerts);
        Assert.Equal(1, persisted.Logs);
    }

    [Fact]
    public async Task Emergency_preempts_an_in_flight_start_publish()
    {
        using var startClient = Fixture.CreateUserClient("Operator");
        using var emergencyClient = Fixture.CreateUserClient("Operator");
        using var publishBlock = Fixture.Mqtt.BlockNextConveyorPublish(call =>
            call.Payload
            == "running=True;emergency=False;resetGeneration=0;acknowledgedFireEpoch=;acknowledgedFireGeneration=0"
        );

        var startTask = startClient.PostAsync("/api/conveyor/start", null);
        await publishBlock.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        var emergencyTask = emergencyClient.PostAsync("/api/conveyor/emergency-stop", null);
        await WaitForMqttCallAsync(call =>
            call.Payload
            == "running=False;emergency=True;resetGeneration=0;acknowledgedFireEpoch=;acknowledgedFireGeneration=0"
        );
        publishBlock.Release();

        Assert.Equal(HttpStatusCode.OK, (await startTask).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await emergencyTask).StatusCode);

        var persisted = await Fixture.WithDbContextAsync(db =>
            db.ConveyorSettings.AsNoTracking().SingleAsync()
        );
        Assert.False(persisted.IsRunning);
        Assert.True(persisted.IsEmergencyStopped);

        var stateCalls = Fixture
            .Mqtt.Calls.Where(call =>
                call.Operation == nameof(IMqttService.PublishConveyorStateAsync)
            )
            .ToArray();
        Assert.Equal(
            "running=False;emergency=True;resetGeneration=0;acknowledgedFireEpoch=;acknowledgedFireGeneration=0",
            stateCalls[^1].Payload
        );
    }

    [Fact]
    public async Task Client_cancellation_cannot_abort_an_authorized_emergency_transition()
    {
        using var client = Fixture.CreateUserClient("Operator");
        using var requestCancellation = new CancellationTokenSource();
        using var publishBlock = Fixture.Mqtt.BlockNextConveyorPublish(call =>
            call.Payload
            == "running=False;emergency=True;resetGeneration=0;acknowledgedFireEpoch=;acknowledgedFireGeneration=0"
        );

        var requestTask = client.PostAsync(
            "/api/conveyor/emergency-stop",
            null,
            requestCancellation.Token
        );
        await publishBlock.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        requestCancellation.Cancel();
        publishBlock.Release();

        try
        {
            _ = await requestTask;
        }
        catch (OperationCanceledException) { }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var state = await Fixture.WithDbContextAsync(db =>
                db.ConveyorSettings.AsNoTracking().SingleAsync()
            );
            if (state.IsEmergencyStopped && !state.IsRunning)
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException("Emergency state was not persisted after client cancellation.");
    }

    private async Task WaitForMqttCallAsync(Func<MqttCall, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (Fixture.Mqtt.Calls.Any(predicate))
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException("Expected MQTT call was not recorded.");
    }
}
