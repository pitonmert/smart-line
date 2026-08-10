using SmartLine.API.Domain.Enums;
using SmartLine.API.Tests.Infrastructure;

namespace SmartLine.API.Tests.Safety;

public sealed class FireTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    private const string EpochA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EpochB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task Repeated_fire_signal_is_idempotent_and_keeps_the_conveyor_emergency_stopped()
    {
        using var device = Fixture.CreateDeviceClient();
        using var viewer = Fixture.CreateUserClient("Viewer");

        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = true,
                    deviceEpoch = EpochA,
                    fireGeneration = 1,
                }
            )
        );
        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = true,
                    deviceEpoch = EpochA,
                    fireGeneration = 1,
                }
            )
        );
        var statusResponse = await viewer.GetAsync("/api/conveyor/status");
        var status = await AssertSuccessAsync(statusResponse);

        Assert.False(status.GetProperty("isRunning").GetBoolean());
        Assert.True(status.GetProperty("isEmergencyStopped").GetBoolean());
        Assert.True(status.GetProperty("isFireDetected").GetBoolean());

        var activeFireAlerts = await Fixture.WithDbContextAsync(db =>
            db.Alerts.CountAsync(alert => alert.AlertType == AlertType.FIRE && alert.IsActive)
        );
        Assert.Equal(1, activeFireAlerts);
    }

    [Fact]
    public async Task Fire_clear_then_admin_reset_requires_an_explicit_start()
    {
        using var device = Fixture.CreateDeviceClient();
        using var admin = Fixture.CreateUserClient("Admin");
        using var operatorClient = Fixture.CreateUserClient("Operator");

        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = true,
                    deviceEpoch = EpochA,
                    fireGeneration = 1,
                }
            )
        );
        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = false,
                    deviceEpoch = EpochA,
                    fireGeneration = 1,
                }
            )
        );
        var reset = await AssertSuccessAsync(
            await admin.PostAsync("/api/conveyor/emergency-reset", null)
        );
        Assert.False(reset.GetProperty("isEmergencyStopped").GetBoolean());
        Assert.False(reset.GetProperty("isFireDetected").GetBoolean());
        Assert.False(reset.GetProperty("isRunning").GetBoolean());

        var started = await AssertSuccessAsync(
            await operatorClient.PostAsync("/api/conveyor/start", null)
        );
        Assert.True(started.GetProperty("isRunning").GetBoolean());
        Assert.False(started.GetProperty("isEmergencyStopped").GetBoolean());
        Assert.False(started.GetProperty("isFireDetected").GetBoolean());
        Assert.Contains(
            Fixture.Mqtt.Calls,
            call =>
                call.Operation == nameof(IMqttService.PublishConveyorStateAsync)
                && call.Payload
                    == "running=True;emergency=False;resetGeneration=1;acknowledgedFireEpoch=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;acknowledgedFireGeneration=1"
        );
    }

    [Fact]
    public async Task Offline_fire_generation_requires_a_new_admin_reset_that_acknowledges_it()
    {
        using var device = Fixture.CreateDeviceClient();
        using var admin = Fixture.CreateUserClient("Admin");

        await AssertSuccessAsync(await admin.PostAsync("/api/conveyor/emergency-reset", null));
        Assert.Contains(
            Fixture.Mqtt.Calls,
            call =>
                call.Payload
                == "running=False;emergency=False;resetGeneration=1;acknowledgedFireEpoch=;acknowledgedFireGeneration=0"
        );

        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = false,
                    deviceEpoch = EpochA,
                    fireGeneration = 1,
                }
            )
        );
        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = false,
                    deviceEpoch = EpochA,
                    fireGeneration = 1,
                }
            )
        );
        var afterOfflineFire = await Fixture.WithDbContextAsync(async db => new
        {
            Conveyor = await db.ConveyorSettings.AsNoTracking().SingleAsync(),
            Alerts = await db.Alerts.CountAsync(alert => alert.AlertType == AlertType.FIRE),
            Logs = await db.SystemLogs.CountAsync(log => log.EventType == EventType.FIRE_DETECTED),
        });
        Assert.False(afterOfflineFire.Conveyor.IsFireDetected);
        Assert.True(afterOfflineFire.Conveyor.IsEmergencyStopped);
        Assert.Equal(1, afterOfflineFire.Conveyor.LastFireGeneration);
        Assert.Equal(1, afterOfflineFire.Alerts);
        Assert.Equal(1, afterOfflineFire.Logs);

        await AssertSuccessAsync(await admin.PostAsync("/api/conveyor/emergency-reset", null));
        var resetState = await Fixture.WithDbContextAsync(db =>
            db.ConveyorSettings.AsNoTracking().SingleAsync()
        );
        Assert.False(resetState.IsEmergencyStopped);
        Assert.Equal(2, resetState.EmergencyResetGeneration);
        Assert.Contains(
            Fixture.Mqtt.Calls,
            call =>
                call.Payload
                == "running=False;emergency=False;resetGeneration=2;acknowledgedFireEpoch=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;acknowledgedFireGeneration=1"
        );
    }

    [Fact]
    public async Task First_device_epoch_commissions_fail_safe_until_admin_acknowledges_it()
    {
        using var device = Fixture.CreateDeviceClient();
        using var admin = Fixture.CreateUserClient("Admin");

        var reading = await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = false,
                    deviceEpoch = EpochA,
                    fireGeneration = 0,
                }
            )
        );
        Assert.True(reading.ValueKind is JsonValueKind.String);

        var commissioned = await Fixture.WithDbContextAsync(db =>
            db.ConveyorSettings.AsNoTracking().SingleAsync()
        );
        Assert.Equal(EpochA, commissioned.FireDeviceEpoch);
        Assert.Equal(0, commissioned.LastFireGeneration);
        Assert.True(commissioned.IsEmergencyStopped);
        Assert.False(commissioned.IsFireDetected);
        Assert.False(commissioned.IsFireGenerationFault);

        await AssertSuccessAsync(await admin.PostAsync("/api/conveyor/emergency-reset", null));
        Assert.Contains(
            Fixture.Mqtt.Calls,
            call =>
                call.Payload
                == "running=False;emergency=False;resetGeneration=1;acknowledgedFireEpoch=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;acknowledgedFireGeneration=0"
        );
    }

    [Fact]
    public async Task New_epoch_after_nvs_erase_relatches_even_when_sensor_is_clear()
    {
        using var device = Fixture.CreateDeviceClient();
        using var admin = Fixture.CreateUserClient("Admin");

        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = false,
                    deviceEpoch = EpochA,
                    fireGeneration = 0,
                }
            )
        );
        await AssertSuccessAsync(await admin.PostAsync("/api/conveyor/emergency-reset", null));

        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = false,
                    deviceEpoch = EpochB,
                    fireGeneration = 0,
                }
            )
        );

        var afterErase = await Fixture.WithDbContextAsync(db =>
            db.ConveyorSettings.AsNoTracking().SingleAsync()
        );
        Assert.Equal(EpochB, afterErase.FireDeviceEpoch);
        Assert.Equal(0, afterErase.LastFireGeneration);
        Assert.True(afterErase.IsEmergencyStopped);
        Assert.False(afterErase.IsFireDetected);
        Assert.False(afterErase.IsFireGenerationFault);
    }

    [Fact]
    public async Task Same_epoch_generation_rollback_faults_closed_and_blocks_reset_and_start()
    {
        using var device = Fixture.CreateDeviceClient();
        using var admin = Fixture.CreateUserClient("Admin");
        using var operatorClient = Fixture.CreateUserClient("Operator");

        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = false,
                    deviceEpoch = EpochA,
                    fireGeneration = 2,
                }
            )
        );
        await AssertSuccessAsync(await admin.PostAsync("/api/conveyor/emergency-reset", null));

        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = false,
                    deviceEpoch = EpochA,
                    fireGeneration = 1,
                }
            )
        );

        var faulted = await Fixture.WithDbContextAsync(db =>
            db.ConveyorSettings.AsNoTracking().SingleAsync()
        );
        Assert.True(faulted.IsFireGenerationFault);
        Assert.True(faulted.IsEmergencyStopped);
        Assert.Equal(2, faulted.LastFireGeneration);

        await AssertErrorAsync(
            await admin.PostAsync("/api/conveyor/emergency-reset", null),
            HttpStatusCode.Conflict,
            "FIRE_GENERATION_MISMATCH"
        );
        await AssertErrorAsync(
            await operatorClient.PostAsync("/api/conveyor/start", null),
            HttpStatusCode.Conflict,
            "FIRE_GENERATION_MISMATCH"
        );
    }

    [Fact]
    public async Task Replayed_fire_identity_after_clear_relatches_without_duplicate_audit()
    {
        using var device = Fixture.CreateDeviceClient();

        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = true,
                    deviceEpoch = EpochA,
                    fireGeneration = 1,
                }
            )
        );
        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = false,
                    deviceEpoch = EpochA,
                    fireGeneration = 1,
                }
            )
        );
        await AssertSuccessAsync(
            await device.PostAsJsonAsync(
                "/api/sensors/fire",
                new
                {
                    isDetected = true,
                    deviceEpoch = EpochA,
                    fireGeneration = 1,
                }
            )
        );

        var persisted = await Fixture.WithDbContextAsync(async db => new
        {
            Conveyor = await db.ConveyorSettings.AsNoTracking().SingleAsync(),
            Alerts = await db.Alerts.CountAsync(alert => alert.AlertType == AlertType.FIRE),
            Logs = await db.SystemLogs.CountAsync(log => log.EventType == EventType.FIRE_DETECTED),
        });
        Assert.True(persisted.Conveyor.IsEmergencyStopped);
        Assert.True(persisted.Conveyor.IsFireDetected);
        Assert.Equal(1, persisted.Alerts);
        Assert.Equal(1, persisted.Logs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("gggggggggggggggggggggggggggggggg")]
    public async Task Invalid_device_epoch_is_rejected(string? deviceEpoch)
    {
        using var device = Fixture.CreateDeviceClient();

        var response = await device.PostAsJsonAsync(
            "/api/sensors/fire",
            new
            {
                isDetected = false,
                deviceEpoch,
                fireGeneration = 0,
            }
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
    }

    [Fact]
    public async Task Missing_fire_generation_is_rejected_instead_of_defaulting_to_zero()
    {
        using var device = Fixture.CreateDeviceClient();

        var response = await device.PostAsJsonAsync(
            "/api/sensors/fire",
            new { isDetected = false, deviceEpoch = EpochA }
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
    }

    [Fact]
    public async Task Missing_device_epoch_is_rejected()
    {
        using var device = Fixture.CreateDeviceClient();

        var response = await device.PostAsJsonAsync(
            "/api/sensors/fire",
            new { isDetected = false, fireGeneration = 0 }
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
    }

    [Fact]
    public async Task Missing_is_detected_is_rejected()
    {
        using var device = Fixture.CreateDeviceClient();

        var response = await device.PostAsJsonAsync(
            "/api/sensors/fire",
            new { deviceEpoch = EpochA, fireGeneration = 0 }
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "REQUEST_INVALID");
    }
}
