using SmartLine.API.Domain.Enums;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Conveyor;

public sealed class EmergencyStopTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Normal_stop_does_not_set_the_emergency_latch()
    {
        using var client = Fixture.CreateClient();

        var response = await client.PostAsync("/api/conveyor/stop", null);
        var status = await AssertSuccessAsync(response);

        Assert.False(status.GetProperty("isRunning").GetBoolean());
        Assert.False(status.GetProperty("isEmergencyStopped").GetBoolean());
        Assert.Contains(
            Fixture.Mqtt.Calls,
            call =>
                call.Operation == nameof(IMqttService.PublishConveyorStateAsync)
                && call.Payload
                    == "running=False;emergency=False;resetGeneration=0;acknowledgedFireEpoch=;acknowledgedFireGeneration=0"
        );
        Assert.DoesNotContain(
            Fixture.Mqtt.Calls,
            call =>
                call.Payload
                == "running=False;emergency=True;resetGeneration=0;acknowledgedFireEpoch=;acknowledgedFireGeneration=0"
        );
    }

    [Fact]
    public async Task Emergency_stop_latches_state_and_uses_the_dedicated_mqtt_command()
    {
        using var client = Fixture.CreateClient();

        var response = await client.PostAsync("/api/conveyor/emergency-stop", null);
        var status = await AssertSuccessAsync(response);

        Assert.False(status.GetProperty("isRunning").GetBoolean());
        Assert.True(status.GetProperty("isEmergencyStopped").GetBoolean());
        Assert.Equal(
            2,
            Fixture.Mqtt.Calls.Count(call =>
                call.Operation == nameof(IMqttService.PublishConveyorStateAsync)
                && call.Payload
                    == "running=False;emergency=True;resetGeneration=0;acknowledgedFireEpoch=;acknowledgedFireGeneration=0"
            )
        );

        var audit = await Fixture.WithDbContextAsync(async db => new
        {
            Alerts = await db.Alerts.CountAsync(alert =>
                alert.AlertType == AlertType.EMERGENCY_STOP && alert.IsActive
            ),
            Logs = await db.SystemLogs.CountAsync(log => log.EventType == EventType.EMERGENCY_STOP),
        });
        Assert.Equal(1, audit.Alerts);
        Assert.Equal(1, audit.Logs);
    }

    [Fact]
    public async Task Start_is_rejected_while_emergency_stop_is_latched()
    {
        using var client = Fixture.CreateClient();
        await AssertSuccessAsync(await client.PostAsync("/api/conveyor/emergency-stop", null));

        var response = await client.PostAsync("/api/conveyor/start", null);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "CONVEYOR_EMERGENCY_LATCHED");
    }

    [Fact]
    public async Task Emergency_reset_does_not_require_an_account()
    {
        using var operatorClient = Fixture.CreateClient();

        var response = await operatorClient.PostAsync("/api/conveyor/emergency-reset", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Emergency_reset_is_blocked_while_fire_sensor_is_active()
    {
        using var operatorClient = Fixture.CreateClient();
        using var adminClient = Fixture.CreateClient();
        using var deviceClient = Fixture.CreateClient();
        await AssertSuccessAsync(
            await operatorClient.PostAsync("/api/conveyor/emergency-stop", null)
        );
        await AssertSuccessAsync(
            await deviceClient.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = true,
                    deviceEpoch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    fireGeneration = 1,
                }
            )
        );

        var response = await adminClient.PostAsync("/api/conveyor/emergency-reset", null);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "FIRE_SENSOR_ACTIVE");
    }

    [Fact]
    public async Task Admin_can_reset_after_fire_sensor_clears_without_starting_the_conveyor()
    {
        using var operatorClient = Fixture.CreateClient();
        using var adminClient = Fixture.CreateClient();
        using var deviceClient = Fixture.CreateClient();
        await AssertSuccessAsync(
            await operatorClient.PostAsync("/api/conveyor/emergency-stop", null)
        );
        await AssertSuccessAsync(
            await deviceClient.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = true,
                    deviceEpoch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    fireGeneration = 1,
                }
            )
        );
        await AssertSuccessAsync(
            await deviceClient.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = false,
                    deviceEpoch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    fireGeneration = 1,
                }
            )
        );

        var response = await adminClient.PostAsync("/api/conveyor/emergency-reset", null);
        var status = await AssertSuccessAsync(response);

        Assert.False(status.GetProperty("isEmergencyStopped").GetBoolean());
        Assert.False(status.GetProperty("isFireDetected").GetBoolean());
        Assert.False(status.GetProperty("isRunning").GetBoolean());
        Assert.Contains(
            Fixture.Mqtt.Calls,
            call =>
                call.Operation == nameof(IMqttService.PublishConveyorStateAsync)
                && call.Payload
                    == "running=False;emergency=False;resetGeneration=1;acknowledgedFireEpoch=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;acknowledgedFireGeneration=1"
        );
    }

    [Fact]
    public async Task Mqtt_failure_returns_a_stable_service_unavailable_error()
    {
        using var client = Fixture.CreateClient();
        Fixture.Mqtt.FailNext();

        var response = await client.PostAsync("/api/conveyor/emergency-stop", null);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "MQTT_UNAVAILABLE");
    }

    [Fact]
    public async Task Every_admin_reset_advances_the_monotonic_reset_generation()
    {
        using var operatorClient = Fixture.CreateClient();
        using var adminClient = Fixture.CreateClient();

        await AssertSuccessAsync(
            await operatorClient.PostAsync("/api/conveyor/emergency-stop", null)
        );
        await AssertSuccessAsync(
            await adminClient.PostAsync("/api/conveyor/emergency-reset", null)
        );
        await AssertSuccessAsync(
            await operatorClient.PostAsync("/api/conveyor/emergency-stop", null)
        );
        await AssertSuccessAsync(
            await adminClient.PostAsync("/api/conveyor/emergency-reset", null)
        );

        var generation = await Fixture.WithDbContextAsync(db =>
            db.ConveyorSettings.Select(setting => setting.EmergencyResetGeneration).SingleAsync()
        );
        Assert.Equal(2, generation);
        Assert.Contains(
            Fixture.Mqtt.Calls,
            call =>
                call.Payload
                == "running=False;emergency=False;resetGeneration=1;acknowledgedFireEpoch=;acknowledgedFireGeneration=0"
        );
        Assert.Contains(
            Fixture.Mqtt.Calls,
            call =>
                call.Payload
                == "running=False;emergency=False;resetGeneration=2;acknowledgedFireEpoch=;acknowledgedFireGeneration=0"
        );
    }
}
