using SmartLine.API.Domain.Enums;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Safety;

public sealed class EnvironmentTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task High_temperature_updates_status_activates_fan_and_creates_alert()
    {
        using var device = Fixture.CreateClient();
        using var viewer = Fixture.CreateClient();

        var response = await device.PostAsJsonAsync(
            "/api/sensors/environment",
            new { temperature = 45m, humidity = 70m }
        );
        await AssertSuccessAsync(response);
        var status = await AssertSuccessAsync(await viewer.GetAsync("/api/conveyor/status"));

        Assert.Equal(45m, status.GetProperty("temperature").GetDecimal());
        Assert.Equal(70m, status.GetProperty("humidity").GetDecimal());
        Assert.True(status.GetProperty("fanActive").GetBoolean());
        Assert.Contains(
            Fixture.Mqtt.Calls,
            call =>
                call.Operation == nameof(IMqttService.PublishFanAsync)
                && string.Equals(call.Payload, bool.TrueString, StringComparison.Ordinal)
        );

        var alertCount = await Fixture.WithDbContextAsync(db =>
            db.Alerts.CountAsync(alert =>
                alert.AlertType == AlertType.HIGH_TEMPERATURE && alert.IsActive
            )
        );
        Assert.Equal(1, alertCount);
    }

    [Fact]
    public async Task Mqtt_failure_returns_503_after_committed_environment_state_and_audit()
    {
        using var device = Fixture.CreateClient();
        Fixture.Mqtt.FailNext();

        var response = await device.PostAsJsonAsync(
            "/api/sensors/environment",
            new { temperature = 45m, humidity = 70m }
        );

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "MQTT_UNAVAILABLE");
        var state = await Fixture.WithDbContextAsync(async db => new
        {
            Conveyor = await db.ConveyorSettings.AsNoTracking().SingleAsync(),
            Alerts = await db.Alerts.CountAsync(),
            Logs = await db.SystemLogs.CountAsync(),
        });
        Assert.Equal(45m, state.Conveyor.Temperature);
        Assert.Equal(70m, state.Conveyor.Humidity);
        Assert.True(state.Conveyor.FanActive);
        Assert.Equal(1, state.Alerts);
        Assert.Equal(1, state.Logs);
    }

    [Fact]
    public async Task Fan_hysteresis_turns_on_above_the_upper_bound_and_off_below_the_lower_bound()
    {
        using var device = Fixture.CreateClient();
        using var viewer = Fixture.CreateClient();

        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/environment",
                new { temperature = 38m, humidity = 50m }
            )
        );
        var fanOn = await AssertSuccessAsync(await viewer.GetAsync("/api/conveyor/status"));
        Assert.True(fanOn.GetProperty("fanActive").GetBoolean());

        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/environment",
                new { temperature = 35m, humidity = 50m }
            )
        );
        var insideBand = await AssertSuccessAsync(await viewer.GetAsync("/api/conveyor/status"));
        Assert.True(insideBand.GetProperty("fanActive").GetBoolean());

        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/environment",
                new { temperature = 32m, humidity = 50m }
            )
        );
        var fanOff = await AssertSuccessAsync(await viewer.GetAsync("/api/conveyor/status"));
        Assert.False(fanOff.GetProperty("fanActive").GetBoolean());

        var fanCommands = Fixture
            .Mqtt.Calls.Where(call => call.Operation == nameof(IMqttService.PublishFanAsync))
            .Select(call => Assert.IsType<string>(call.Payload))
            .ToArray();
        Assert.Equal([bool.TrueString, bool.FalseString], fanCommands);
    }

    [Fact]
    public async Task Persistence_failure_rolls_back_the_high_temperature_use_case()
    {
        await Fixture.WithDbContextAsync(async db =>
        {
            var conveyor = await db.ConveyorSettings.SingleAsync();
            conveyor.Temperature = 22m;
            conveyor.Humidity = 45m;
            conveyor.FanActive = false;
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE FUNCTION fail_high_temperature_log() RETURNS trigger
                LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."EventType" = 'HIGH_TEMPERATURE_DETECTED' THEN
                        RAISE EXCEPTION 'synthetic audit persistence failure';
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER fail_high_temperature_log_trigger
                BEFORE INSERT ON "SystemLogs"
                FOR EACH ROW EXECUTE FUNCTION fail_high_temperature_log();
                """
            );
        });
        using var device = Fixture.CreateClient();

        var response = await device.PostAsJsonAsync(
            "/api/sensors/environment",
            new { temperature = 45m, humidity = 70m }
        );

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "DATABASE_CONFLICT");
        var state = await Fixture.WithDbContextAsync(async db => new
        {
            Conveyor = await db.ConveyorSettings.AsNoTracking().SingleAsync(),
            Alerts = await db.Alerts.CountAsync(),
            Logs = await db.SystemLogs.CountAsync(),
        });
        Assert.Equal(22m, state.Conveyor.Temperature);
        Assert.Equal(45m, state.Conveyor.Humidity);
        Assert.False(state.Conveyor.FanActive);
        Assert.Equal(0, state.Alerts);
        Assert.Equal(0, state.Logs);
        Assert.DoesNotContain(
            Fixture.Mqtt.Calls,
            call => call.Operation == nameof(IMqttService.PublishFanAsync)
        );
    }
}
