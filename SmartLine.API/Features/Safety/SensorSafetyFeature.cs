using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Domain.Enums;
using SmartLine.API.Features.Conveyor;
using SmartLine.API.Features.Monitoring;
using SmartLine.API.Infrastructure.Authentication;
using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Safety;

public static class SensorSafetyFeature
{
    public static RouteGroupBuilder MapSensorSafetyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sensors");
        group
            .MapPost(
                "/environment",
                async (
                    EnvironmentReading request,
                    EnvironmentHandler handler,
                    CancellationToken ct
                ) =>
                {
                    await handler.HandleAsync(request, ct);
                    return Results.Ok(
                        ApiResponse<string>.Success(
                            $"Ortam verisi kaydedildi: {request.Temperature}°C, %{request.Humidity}"
                        )
                    );
                }
            )
            .RequireAuthorization(AuthPolicies.Device)
            .RequireRateLimiting("sensor");
        group
            .MapPost(
                "/fire",
                async (FireReading request, FireHandler handler, CancellationToken ct) =>
                {
                    await handler.HandleAsync(request, ct);
                    return Results.Ok(
                        ApiResponse<string>.Success(
                            request.IsDetected
                                ? "YANGIN ALARMI! Acil durdurma latch'i etkin."
                                : "Yangın sensörü temiz; latch yönetici resetini bekliyor"
                        )
                    );
                }
            )
            .RequireAuthorization(AuthPolicies.Device)
            .RequireRateLimiting("fire");
        group
            .MapGet(
                "/status",
                async (ConveyorHandler conveyor, CancellationToken ct) =>
                    Results.Ok(
                        ApiResponse<ConveyorStatusResponse>.Success(
                            await conveyor.GetStatusAsync(ct)
                        )
                    )
            )
            .RequireAuthorization(AuthPolicies.Viewer);
        return group;
    }
}

public sealed record FireReading(
    [property: JsonRequired] bool IsDetected,
    [property: JsonRequired] string DeviceEpoch,
    [property: JsonRequired] long FireGeneration
);

public sealed class EnvironmentHandler(
    ApplicationDbContext db,
    ConveyorHandler conveyor,
    AlertHandler alerts,
    LogHandler logs,
    IOptions<EnvironmentThresholdsOptions> thresholds
)
{
    public async Task HandleAsync(EnvironmentReading reading, CancellationToken ct = default)
    {
        Alert? alert = null;
        SystemLog? audit = null;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var environment = await conveyor.StageEnvironmentAsync(reading, ct);
        if (reading.Temperature > thresholds.Value.AlarmTempThreshold)
        {
            alert = await alerts.StageCreateAsync(AlertType.HIGH_TEMPERATURE, ct);
            audit = await logs.StageAsync(
                EventType.HIGH_TEMPERATURE_DETECTED,
                $"Yüksek sıcaklık algılandı: {reading.Temperature}°C",
                "Warning",
                ct
            );
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        MqttUnavailableException? mqttFailure = null;
        try
        {
            await conveyor.PublishEnvironmentAfterCommitAsync(environment, ct);
        }
        catch (MqttUnavailableException exception)
        {
            mqttFailure = exception;
        }
        if (alert is not null)
            await alerts.NotifyAsync(alert, ct);
        if (audit is not null)
            await logs.NotifyAsync(audit, ct);
        if (mqttFailure is not null)
            throw mqttFailure;
    }
}

public sealed class FireHandler(ConveyorHandler conveyor)
{
    public async Task HandleAsync(FireReading reading, CancellationToken ct = default) =>
        _ = await conveyor.ApplyFireReadingAsync(reading, ct);
}
