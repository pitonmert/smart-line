namespace SmartLine.API.Tests.Infrastructure;

public sealed class MqttReconnectTests(ApiFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Reconnect_republishes_the_persisted_desired_state()
    {
        await Fixture.WithDbContextAsync(async db =>
        {
            var setting = await db.ConveyorSettings.SingleAsync();
            setting.Speed = 73;
            setting.IsRunning = false;
            setting.IsEmergencyStopped = true;
            setting.EmergencyResetGeneration = 7;
            setting.FireDeviceEpoch = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            setting.LastFireGeneration = 9;
            setting.FanActive = true;
            await db.SaveChangesAsync();
        });

        var desiredState = Fixture.GetRequiredService<MqttDesiredStatePublisher>();
        await desiredState.PublishAsync();
        await desiredState.PublishAsync();

        var calls = Fixture.Mqtt.Calls;
        Assert.Equal(
            2,
            calls.Count(call =>
                call.Operation == nameof(IMqttService.PublishConveyorStateAsync)
                && call.Payload
                    == "running=False;emergency=True;resetGeneration=7;acknowledgedFireEpoch=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;acknowledgedFireGeneration=9"
            )
        );
        Assert.Equal(
            2,
            calls.Count(call =>
                call.Operation == nameof(IMqttService.PublishSpeedChangeAsync)
                && call.Payload == "73"
            )
        );
        Assert.Equal(
            2,
            calls.Count(call =>
                call.Operation == nameof(IMqttService.PublishFanAsync)
                && call.Payload == bool.TrueString
            )
        );
    }
}
