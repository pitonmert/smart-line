using Microsoft.EntityFrameworkCore;
using SmartLine.API.Infrastructure.Messaging.Mqtt;
using SmartLine.API.Infrastructure.Persistence;

namespace SmartLine.API.Infrastructure.Hosting;

public sealed class MqttDesiredStatePublisher(
    IServiceScopeFactory scopeFactory,
    IMqttService mqttService,
    ConveyorCommandGate commandGate
)
{
    private const int ConveyorId = 1;

    public async Task PublishAsync(CancellationToken ct = default)
    {
        using var lease = await commandGate.EnterAsync(ct);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var desired = await db
            .ConveyorSettings.AsNoTracking()
            .SingleAsync(setting => setting.Id == ConveyorId, ct);
        if (!commandGate.IsEmergencyPending)
        {
            await mqttService.PublishConveyorStateAsync(
                desired.IsRunning,
                desired.IsEmergencyStopped,
                desired.EmergencyResetGeneration,
                desired.FireDeviceEpoch,
                desired.LastFireGeneration,
                ct
            );
            if (commandGate.IsEmergencyPending)
                await mqttService.PublishConveyorStateAsync(
                    false,
                    true,
                    desired.EmergencyResetGeneration,
                    desired.FireDeviceEpoch,
                    desired.LastFireGeneration,
                    ct
                );
        }
        await mqttService.PublishSpeedChangeAsync(desired.Speed, ct);
        await mqttService.PublishFanAsync(desired.FanActive, ct);
    }
}
