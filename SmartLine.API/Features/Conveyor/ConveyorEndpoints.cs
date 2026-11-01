using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Domain.Enums;
using SmartLine.API.Features.Monitoring;
using SmartLine.API.Features.Safety;
using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Infrastructure.Messaging.Mqtt;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Infrastructure.Realtime;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Conveyor;

public static class ConveyorEndpoints
{
    public static RouteGroupBuilder MapConveyorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conveyor");
        group.MapGet(
            "/status",
            async (ConveyorHandler handler, CancellationToken ct) =>
                Results.Ok(
                    ApiResponse<ConveyorStatusResponse>.Success(await handler.GetStatusAsync(ct))
                )
        );
        group
            .MapPost(
                "/speed",
                async (UpdateSpeedRequest request, ConveyorHandler handler, CancellationToken ct) =>
                    Results.Ok(
                        ApiResponse<ConveyorStatusResponse>.Success(
                            await handler.UpdateSpeedAsync(request, ct),
                            $"Hız {request.Speed} olarak güncellendi"
                        )
                    )
            );
        group
            .MapPost(
                "/start",
                async (ConveyorHandler handler, CancellationToken ct) =>
                    Results.Ok(
                        ApiResponse<ConveyorStatusResponse>.Success(
                            await handler.StartAsync(ct),
                            "Konveyör başlatıldı"
                        )
                    )
            );
        group
            .MapPost(
                "/stop",
                async (ConveyorHandler handler, CancellationToken ct) =>
                    Results.Ok(
                        ApiResponse<ConveyorStatusResponse>.Success(
                            await handler.StopAsync(ct),
                            "Konveyör normal olarak durduruldu"
                        )
                    )
            );
        group
            .MapPost(
                "/emergency-stop",
                async (ConveyorHandler handler, CancellationToken ct) =>
                    Results.Ok(
                        ApiResponse<ConveyorStatusResponse>.Success(
                            await handler.EmergencyStopAsync(ct),
                            "Acil durdurma latch'i etkinleştirildi"
                        )
                    )
            );
        group
            .MapPost(
                "/emergency-reset",
                async (ConveyorHandler handler, CancellationToken ct) =>
                    Results.Ok(
                        ApiResponse<ConveyorStatusResponse>.Success(
                            await handler.EmergencyResetAsync(ct),
                            "Acil durdurma latch'i sıfırlandı; konveyör durmaya devam ediyor"
                        )
                    )
            );
        return group;
    }
}

public sealed record UpdateSpeedRequest([property: JsonRequired] int Speed);

public sealed record EnvironmentReading(decimal Temperature, decimal Humidity);

public sealed record ConveyorStatusResponse(
    int Speed,
    bool IsRunning,
    decimal Temperature,
    decimal Humidity,
    bool FanActive,
    bool IsEmergencyStopped,
    bool IsFireDetected,
    string FireDeviceEpoch,
    long LastFireGeneration,
    bool IsFireGenerationFault,
    DateTime LastUpdated
);

public sealed record EnvironmentStageResult(ConveyorSetting Setting, bool FanChanged);

public sealed class ConveyorHandler(
    ApplicationDbContext db,
    LogHandler logs,
    AlertHandler alerts,
    IHubContext<DashboardHub> hub,
    IMqttService mqtt,
    ConveyorCommandGate commandGate,
    IOptions<EnvironmentThresholdsOptions> thresholds,
    ILogger<ConveyorHandler> logger
)
{
    private const int ConveyorId = 1;

    public async Task<ConveyorStatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        var setting =
            await db
                .ConveyorSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == ConveyorId, ct)
            ?? throw new NotFoundException("ConveyorSetting", ConveyorId);
        return ToResponse(setting);
    }

    public async Task<ConveyorStatusResponse> UpdateSpeedAsync(
        UpdateSpeedRequest request,
        CancellationToken ct = default
    )
    {
        if (request.Speed is < 0 or > 100)
            throw new ValidationException("Hız 0-100 arasında olmalıdır");

        SystemLog audit;
        ConveyorSetting setting;
        MqttUnavailableException? mqttFailure;
        using (await commandGate.EnterAsync(ct))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = DateTime.UtcNow;
            var affected = await db
                .ConveyorSettings.Where(setting =>
                    setting.Id == ConveyorId
                    && !setting.IsRunning
                    && !setting.IsEmergencyStopped
                    && !setting.IsFireDetected
                    && !setting.IsFireGenerationFault
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(setting => setting.Speed, request.Speed)
                            .SetProperty(setting => setting.LastUpdated, now),
                    ct
                );
            if (affected != 1)
                await ThrowForBlockedControlAsync(ct);

            audit = await logs.StageAsync(
                EventType.SPEED_CHANGED,
                $"Konveyör hızı {request.Speed}'e güncellendi",
                ct: ct
            );
            await db.SaveChangesAsync(ct);
            setting = await LoadNoTrackingAsync(ct);
            await transaction.CommitAsync(ct);

            mqttFailure = await CaptureMqttFailureAsync(() =>
                PublishSpeedAsync(request.Speed, CancellationToken.None)
            );
        }
        await logs.NotifyAsync(audit, CancellationToken.None);
        await NotifyConveyorAsync(setting, CancellationToken.None);
        if (mqttFailure is not null)
            throw mqttFailure;
        return ToResponse(setting);
    }

    public async Task<ConveyorStatusResponse> StartAsync(CancellationToken ct = default)
    {
        SystemLog? audit = null;
        ConveyorSetting setting;
        MqttUnavailableException? mqttFailure;
        using (await commandGate.EnterAsync(ct))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = DateTime.UtcNow;
            var affected = await db
                .ConveyorSettings.Where(setting =>
                    setting.Id == ConveyorId
                    && !setting.IsRunning
                    && !setting.IsEmergencyStopped
                    && !setting.IsFireDetected
                    && !setting.IsFireGenerationFault
                )
                .ExecuteUpdateAsync(
                    setters =>
                        setters
                            .SetProperty(setting => setting.IsRunning, true)
                            .SetProperty(setting => setting.LastUpdated, now),
                    ct
                );

            if (affected == 1)
            {
                audit = await logs.StageAsync(
                    EventType.SYSTEM_STARTED,
                    "Konveyör başlatıldı",
                    ct: ct
                );
                await db.SaveChangesAsync(ct);
            }
            else
            {
                var current = await LoadNoTrackingAsync(ct);
                ThrowIfEmergencyBlocked(current);
            }

            setting = await LoadNoTrackingAsync(ct);
            await transaction.CommitAsync(ct);
            mqttFailure = await PublishSerializedStateAsync(
                true,
                false,
                setting.EmergencyResetGeneration,
                setting.FireDeviceEpoch,
                setting.LastFireGeneration
            );
        }
        if (audit is not null)
            await logs.NotifyAsync(audit, CancellationToken.None);
        await NotifyConveyorAsync(setting, CancellationToken.None);
        if (mqttFailure is not null)
            throw mqttFailure;
        return ToResponse(setting);
    }

    public async Task<ConveyorStatusResponse> StopAsync(CancellationToken ct = default)
    {
        SystemLog? audit = null;
        ConveyorSetting setting;
        MqttUnavailableException? mqttFailure;
        using (await commandGate.EnterAsync(ct))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var current = await LoadNoTrackingAsync(ct);
            ThrowIfEmergencyBlocked(current);

            if (current.IsRunning)
            {
                var now = DateTime.UtcNow;
                var affected = await db
                    .ConveyorSettings.Where(setting =>
                        setting.Id == ConveyorId
                        && setting.IsRunning
                        && !setting.IsEmergencyStopped
                        && !setting.IsFireDetected
                        && !setting.IsFireGenerationFault
                    )
                    .ExecuteUpdateAsync(
                        setters =>
                            setters
                                .SetProperty(setting => setting.IsRunning, false)
                                .SetProperty(setting => setting.LastUpdated, now),
                        ct
                    );
                if (affected != 1)
                    await ThrowForBlockedControlAsync(ct);
                audit = await logs.StageAsync(
                    EventType.SYSTEM_STOPPED,
                    "Konveyör normal olarak durduruldu",
                    ct: ct
                );
                await db.SaveChangesAsync(ct);
            }

            setting = await LoadNoTrackingAsync(ct);
            await transaction.CommitAsync(ct);
            mqttFailure = await PublishSerializedStateAsync(
                false,
                false,
                setting.EmergencyResetGeneration,
                setting.FireDeviceEpoch,
                setting.LastFireGeneration
            );
        }
        if (audit is not null)
            await logs.NotifyAsync(audit, CancellationToken.None);
        await NotifyConveyorAsync(setting, CancellationToken.None);
        if (mqttFailure is not null)
            throw mqttFailure;
        return ToResponse(setting);
    }

    public Task<ConveyorStatusResponse> EmergencyStopAsync(CancellationToken ct = default) =>
        ApplyEmergencyAsync(isFire: false, fireDeviceEpoch: null, fireGeneration: null, ct);

    public async Task<ConveyorStatusResponse> EmergencyResetAsync(CancellationToken ct = default)
    {
        ConveyorSetting setting;
        SystemLog audit;
        MqttUnavailableException? mqttFailure;
        using (await commandGate.EnterAsync(ct))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            setting = await LoadTrackedAsync(ct);
            if (setting.IsFireGenerationFault)
                throw new ConveyorException(
                    "Yangın sensörü sayacı geri sardığı için cihaz yeni bir güvenlik epoch'u oluşturmadan reset yapılamaz",
                    ErrorCodes.FireGenerationMismatch
                );
            if (setting.IsFireDetected)
                throw new ConveyorException(
                    "Yangın sensörü aktifken acil durdurma sıfırlanamaz",
                    ErrorCodes.FireSensorActive
                );

            setting.IsEmergencyStopped = false;
            setting.IsRunning = false;
            setting.EmergencyResetGeneration = checked(setting.EmergencyResetGeneration + 1);
            setting.LastUpdated = DateTime.UtcNow;
            audit = await logs.StageAsync(
                EventType.EMERGENCY_RESET,
                "Acil durdurma latch'i yönetici tarafından sıfırlandı",
                "Warning",
                ct
            );
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            mqttFailure = await PublishSerializedStateAsync(
                false,
                false,
                setting.EmergencyResetGeneration,
                setting.FireDeviceEpoch,
                setting.LastFireGeneration
            );
        }
        await logs.NotifyAsync(audit, CancellationToken.None);
        await NotifyConveyorAsync(setting, CancellationToken.None);
        if (mqttFailure is not null)
            throw mqttFailure;
        return ToResponse(setting);
    }

    public async Task<ConveyorStatusResponse> ApplyFireReadingAsync(
        FireReading reading,
        CancellationToken ct = default
    )
    {
        if (!IsValidFireDeviceEpoch(reading.DeviceEpoch))
            throw new ValidationException(
                "Device epoch tam olarak 32 küçük harf hexadecimal karakterden oluşmalıdır"
            );
        if (reading.FireGeneration is < 0 or > uint.MaxValue)
            throw new ValidationException("Geçersiz fire generation değeri");

        return reading.IsDetected
            ? await ApplyEmergencyAsync(
                isFire: true,
                reading.DeviceEpoch,
                reading.FireGeneration,
                ct
            )
            : await ApplyFireClearAsync(reading.DeviceEpoch, reading.FireGeneration, ct);
    }

    private async Task<ConveyorStatusResponse> ApplyFireClearAsync(
        string fireDeviceEpoch,
        long fireGeneration,
        CancellationToken ct
    )
    {
        Alert? alert = null;
        SystemLog? audit = null;
        ConveyorSetting setting;
        MqttUnavailableException? immediateMqttFailure = null;
        MqttUnavailableException? finalMqttFailure;

        using (await commandGate.EnterAsync(CancellationToken.None))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(
                CancellationToken.None
            );
            setting = await LoadTrackedAsync(CancellationToken.None);
            var epochChanged = !string.Equals(
                fireDeviceEpoch,
                setting.FireDeviceEpoch,
                StringComparison.Ordinal
            );
            var missedFireEvent = !epochChanged && fireGeneration > setting.LastFireGeneration;
            var generationRollback = !epochChanged && fireGeneration < setting.LastFireGeneration;
            var firstFault = generationRollback && !setting.IsFireGenerationFault;
            var safetyTransition = epochChanged || missedFireEvent || firstFault;

            if (epochChanged)
            {
                setting.FireDeviceEpoch = fireDeviceEpoch;
                setting.LastFireGeneration = fireGeneration;
                setting.IsFireGenerationFault = false;
            }
            else if (generationRollback)
            {
                setting.IsFireGenerationFault = true;
            }
            else if (missedFireEvent)
            {
                setting.LastFireGeneration = fireGeneration;
            }

            if (epochChanged || missedFireEvent || generationRollback)
            {
                immediateMqttFailure = await PublishSafetyStateBestEffortAsync(
                    false,
                    true,
                    setting.EmergencyResetGeneration,
                    setting.FireDeviceEpoch,
                    setting.LastFireGeneration
                );
                setting.IsRunning = false;
                setting.IsEmergencyStopped = true;
            }
            setting.IsFireDetected = false;
            setting.LastUpdated = DateTime.UtcNow;

            if (safetyTransition)
            {
                alert = await alerts.StageCreateAsync(AlertType.FIRE, CancellationToken.None);
                audit = await logs.StageAsync(
                    EventType.FIRE_DETECTED,
                    generationRollback
                            ? "Yangın sensörü sayacı aynı epoch içinde geri sardı; sistem güvenli durumda kilitlendi"
                        : epochChanged
                            ? "Yeni yangın sensörü epoch'u algılandı; yönetici onayı bekleniyor"
                        : "Firmware çevrimdışıyken yangın algılandı; emergency latch korundu",
                    "Critical",
                    CancellationToken.None
                );
            }
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            finalMqttFailure = await PublishSerializedStateAsync(
                setting.IsRunning,
                setting.IsEmergencyStopped,
                setting.EmergencyResetGeneration,
                setting.FireDeviceEpoch,
                setting.LastFireGeneration
            );
        }

        if (alert is not null)
            await alerts.NotifyAsync(alert, CancellationToken.None);
        if (audit is not null)
            await logs.NotifyAsync(audit, CancellationToken.None);
        await NotifyConveyorAsync(setting, CancellationToken.None);
        var mqttFailure = finalMqttFailure ?? immediateMqttFailure;
        if (mqttFailure is not null)
            throw mqttFailure;
        return ToResponse(setting);
    }

    public async Task<EnvironmentStageResult> StageEnvironmentAsync(
        EnvironmentReading reading,
        CancellationToken ct = default
    )
    {
        if (reading.Temperature is < -40 or > 80 || reading.Humidity is < 0 or > 100)
            throw new ValidationException("Geçersiz ortam verisi");

        var setting = await LoadTrackedAsync(ct);
        var fanWasActive = setting.FanActive;
        setting.Temperature = reading.Temperature;
        setting.Humidity = reading.Humidity;
        var hysteresis = thresholds.Value.FanHysteresisCelsius;
        if (reading.Temperature > thresholds.Value.HighTempThreshold + hysteresis)
            setting.FanActive = true;
        else if (reading.Temperature < thresholds.Value.HighTempThreshold - hysteresis)
            setting.FanActive = false;
        setting.LastUpdated = DateTime.UtcNow;
        return new EnvironmentStageResult(setting, setting.FanActive != fanWasActive);
    }

    public async Task PublishEnvironmentAfterCommitAsync(
        EnvironmentStageResult result,
        CancellationToken ct = default
    )
    {
        var mqttFailure = result.FanChanged
            ? await CaptureMqttFailureAsync(() => PublishFanAsync(result.Setting.FanActive, ct))
            : null;
        try
        {
            await hub.Clients.All.SendAsync(
                "ReceiveEnvironmentUpdate",
                new
                {
                    temperature = (double)result.Setting.Temperature,
                    humidity = (double)result.Setting.Humidity,
                    fanActive = result.Setting.FanActive,
                },
                ct
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "SignalR environment notification failed.");
        }
        if (mqttFailure is not null)
            throw mqttFailure;
    }

    private async Task<ConveyorStatusResponse> ApplyEmergencyAsync(
        bool isFire,
        string? fireDeviceEpoch,
        long? fireGeneration,
        CancellationToken ct
    )
    {
        using var emergencyLease = commandGate.BeginEmergency();
        var immediateMqttFailure = await PublishSafetyStateBestEffortAsync(
            false,
            true,
            resetGeneration: 0,
            acknowledgedFireEpoch: string.Empty,
            acknowledgedFireGeneration: 0
        );
        if (immediateMqttFailure is not null)
            logger.LogCritical(
                immediateMqttFailure,
                "Emergency MQTT command could not be delivered immediately."
            );

        Alert? alert = null;
        SystemLog? audit = null;
        ConveyorSetting setting;
        MqttUnavailableException? finalMqttFailure;
        var resetGeneration = 0;
        var acknowledgedFireEpoch = string.Empty;
        long acknowledgedFireGeneration = 0;
        using (await commandGate.EnterAsync(CancellationToken.None))
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(
                    CancellationToken.None
                );
                setting = await LoadTrackedAsync(CancellationToken.None);
                resetGeneration = setting.EmergencyResetGeneration;
                acknowledgedFireEpoch = setting.FireDeviceEpoch;
                acknowledgedFireGeneration = setting.LastFireGeneration;
                var epochChanged =
                    isFire
                    && !string.Equals(
                        fireDeviceEpoch,
                        setting.FireDeviceEpoch,
                        StringComparison.Ordinal
                    );
                var generationRollback =
                    isFire && !epochChanged && fireGeneration!.Value < setting.LastFireGeneration;
                var generationAdvanced =
                    isFire && !epochChanged && fireGeneration!.Value > setting.LastFireGeneration;
                var firstTransition = !setting.IsEmergencyStopped;
                if (isFire)
                {
                    firstTransition =
                        epochChanged
                        || generationAdvanced
                        || (generationRollback && !setting.IsFireGenerationFault);
                    if (epochChanged)
                    {
                        setting.FireDeviceEpoch = fireDeviceEpoch!;
                        setting.LastFireGeneration = fireGeneration!.Value;
                        setting.IsFireGenerationFault = false;
                    }
                    else if (generationRollback)
                    {
                        setting.IsFireGenerationFault = true;
                    }
                    else if (generationAdvanced)
                    {
                        setting.LastFireGeneration = fireGeneration!.Value;
                    }
                }

                setting.IsRunning = false;
                setting.IsEmergencyStopped = true;
                setting.IsFireDetected = setting.IsFireDetected || isFire;
                setting.LastUpdated = DateTime.UtcNow;
                if (firstTransition)
                {
                    alert = await alerts.StageCreateAsync(
                        isFire ? AlertType.FIRE : AlertType.EMERGENCY_STOP,
                        CancellationToken.None
                    );
                    audit = await logs.StageAsync(
                        isFire ? EventType.FIRE_DETECTED : EventType.EMERGENCY_STOP,
                        isFire && generationRollback
                                ? "Yangın sensörü sayacı aynı epoch içinde geri sardı; sistem güvenli durumda kilitlendi"
                            : isFire ? "Yangın algılandı; acil durdurma latch'i etkinleştirildi"
                            : "Acil durdurma latch'i operatör tarafından etkinleştirildi",
                        "Critical",
                        CancellationToken.None
                    );
                }
                await db.SaveChangesAsync(CancellationToken.None);
                await transaction.CommitAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                var republishFailure = await PublishSafetyStateBestEffortAsync(
                    false,
                    true,
                    resetGeneration,
                    acknowledgedFireEpoch,
                    acknowledgedFireGeneration
                );
                if (republishFailure is not null)
                    logger.LogCritical(
                        republishFailure,
                        "Emergency safety command could not be re-published after persistence failure."
                    );
                logger.LogCritical(
                    exception,
                    "Emergency command was attempted but persistence/audit failed. Physical safe state must be verified."
                );
                throw;
            }

            finalMqttFailure = await PublishSafetyStateBestEffortAsync(
                setting.IsRunning,
                setting.IsEmergencyStopped,
                setting.EmergencyResetGeneration,
                setting.FireDeviceEpoch,
                setting.LastFireGeneration
            );
        }

        if (alert is not null)
            await alerts.NotifyAsync(alert, CancellationToken.None);
        if (audit is not null)
            await logs.NotifyAsync(audit, CancellationToken.None);
        await NotifyConveyorAsync(setting, CancellationToken.None);
        var mqttFailure = finalMqttFailure ?? immediateMqttFailure;
        if (mqttFailure is not null)
            throw mqttFailure;
        return ToResponse(setting);
    }

    private async Task ThrowForBlockedControlAsync(CancellationToken ct)
    {
        var current = await LoadNoTrackingAsync(ct);
        ThrowIfEmergencyBlocked(current);
        if (current.IsRunning)
            throw new ConveyorException(
                "Konveyör çalışırken hız değiştirilemez",
                ErrorCodes.ConveyorMustBeStopped
            );
        throw new ConveyorException(
            "Konveyör durumu eşzamanlı başka bir işlem tarafından değiştirildi",
            ErrorCodes.DatabaseConflict
        );
    }

    private static void ThrowIfEmergencyBlocked(ConveyorSetting setting)
    {
        if (setting.IsFireGenerationFault)
            throw new ConveyorException(
                "Yangın sensörü sayacı aynı epoch içinde geri sardığı için konveyör kontrol edilemez",
                ErrorCodes.FireGenerationMismatch
            );
        if (setting.IsFireDetected)
            throw new ConveyorException(
                "Yangın sensörü aktifken konveyör kontrol edilemez",
                ErrorCodes.FireSensorActive
            );
        if (setting.IsEmergencyStopped)
            throw new ConveyorException(
                "Acil durdurma latch'i sıfırlanmadan konveyör kontrol edilemez",
                ErrorCodes.ConveyorEmergencyLatched
            );
    }

    private async Task<ConveyorSetting> LoadTrackedAsync(CancellationToken ct) =>
        await db.ConveyorSettings.SingleOrDefaultAsync(setting => setting.Id == ConveyorId, ct)
        ?? throw new NotFoundException("ConveyorSetting", ConveyorId);

    private async Task<ConveyorSetting> LoadNoTrackingAsync(CancellationToken ct) =>
        await db
            .ConveyorSettings.AsNoTracking()
            .SingleOrDefaultAsync(setting => setting.Id == ConveyorId, ct)
        ?? throw new NotFoundException("ConveyorSetting", ConveyorId);

    private async Task NotifyConveyorAsync(ConveyorSetting setting, CancellationToken ct)
    {
        try
        {
            await hub.Clients.All.SendAsync("ReceiveConveyorUpdate", ToResponse(setting), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "SignalR conveyor notification failed.");
        }
    }

    private Task PublishSpeedAsync(int speed, CancellationToken ct) =>
        PublishOrThrowAsync(
            () => mqtt.PublishSpeedChangeAsync(speed, ct),
            "MQTT broker hız komutunu teslim edemedi"
        );

    private Task PublishStateAsync(
        bool isRunning,
        bool emergency,
        int resetGeneration,
        string acknowledgedFireEpoch,
        long acknowledgedFireGeneration,
        CancellationToken ct
    ) =>
        PublishOrThrowAsync(
            () =>
                mqtt.PublishConveyorStateAsync(
                    isRunning,
                    emergency,
                    resetGeneration,
                    acknowledgedFireEpoch,
                    acknowledgedFireGeneration,
                    ct
                ),
            "MQTT broker konveyör komutunu teslim edemedi"
        );

    private async Task<MqttUnavailableException?> PublishSerializedStateAsync(
        bool isRunning,
        bool emergency,
        int resetGeneration,
        string acknowledgedFireEpoch,
        long acknowledgedFireGeneration
    )
    {
        if (commandGate.IsEmergencyPending)
            return null;

        var desiredFailure = await CaptureMqttFailureAsync(() =>
            PublishStateAsync(
                isRunning,
                emergency,
                resetGeneration,
                acknowledgedFireEpoch,
                acknowledgedFireGeneration,
                CancellationToken.None
            )
        );
        if (!commandGate.IsEmergencyPending)
            return desiredFailure;

        var safetyFailure = await CaptureMqttFailureAsync(() =>
            PublishStateAsync(
                false,
                true,
                resetGeneration,
                acknowledgedFireEpoch,
                acknowledgedFireGeneration,
                CancellationToken.None
            )
        );
        return safetyFailure ?? desiredFailure;
    }

    private async Task<MqttUnavailableException?> PublishSafetyStateBestEffortAsync(
        bool isRunning,
        bool emergency,
        int resetGeneration,
        string acknowledgedFireEpoch,
        long acknowledgedFireGeneration
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await PublishStateAsync(
                isRunning,
                emergency,
                resetGeneration,
                acknowledgedFireEpoch,
                acknowledgedFireGeneration,
                timeout.Token
            );
            return null;
        }
        catch (Exception exception)
        {
            return exception as MqttUnavailableException
                ?? new MqttUnavailableException(
                    "MQTT broker safety komutunu süre içinde teslim edemedi",
                    exception
                );
        }
    }

    private Task PublishFanAsync(bool active, CancellationToken ct) =>
        PublishOrThrowAsync(
            () => mqtt.PublishFanAsync(active, ct),
            "MQTT broker fan komutunu teslim edemedi"
        );

    private static async Task PublishOrThrowAsync(Func<Task> publish, string failureMessage)
    {
        try
        {
            await publish();
        }
        catch (MqttUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new MqttUnavailableException(failureMessage, exception);
        }
    }

    private static async Task<MqttUnavailableException?> CaptureMqttFailureAsync(Func<Task> publish)
    {
        try
        {
            await publish();
            return null;
        }
        catch (MqttUnavailableException exception)
        {
            return exception;
        }
    }

    private static bool IsValidFireDeviceEpoch(string? deviceEpoch) =>
        deviceEpoch is { Length: 32 }
        && deviceEpoch.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static ConveyorStatusResponse ToResponse(ConveyorSetting setting) =>
        new(
            setting.Speed,
            setting.IsRunning,
            setting.Temperature,
            setting.Humidity,
            setting.FanActive,
            setting.IsEmergencyStopped,
            setting.IsFireDetected,
            setting.FireDeviceEpoch,
            setting.LastFireGeneration,
            setting.IsFireGenerationFault,
            setting.LastUpdated
        );
}
