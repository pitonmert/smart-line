using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SmartLine.API.Features.Conveyor;
using SmartLine.API.Features.Production;
using SmartLine.API.Features.Safety;
using SmartLine.API.Infrastructure.Messaging;
using SmartLine.API.Infrastructure.Messaging.Mqtt;

namespace SmartLine.API.Infrastructure.Hosting;

public sealed class SensorDataProcessorService(
    ChannelReader<SensorMessage> reader,
    IServiceScopeFactory scopeFactory,
    IOptions<MqttOptions> options,
    ILogger<SensorDataProcessorService> logger
) : BackgroundService
{
    private readonly MqttTopicOptions _topics = options.Value.Topics;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Sensör Veri İşleyici başlatıldı.");
        await foreach (var message in reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessMessageAsync(message, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Mesaj işlenemedi. Topic: {Topic}", message.Topic);
            }
        }
        logger.LogInformation("Sensör Veri İşleyici durduruldu.");
    }

    private async Task ProcessMessageAsync(SensorMessage message, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        if (message.Topic == _topics.Environment)
        {
            if (
                JsonSerializer.Deserialize<EnvironmentReading>(message.Payload, JsonOptions) is
                { } reading
            )
                await scope
                    .ServiceProvider.GetRequiredService<EnvironmentHandler>()
                    .HandleAsync(reading, ct);
            else
                logger.LogWarning(
                    "Ortam verisi parse edilemedi. Payload: {Payload}",
                    message.Payload
                );
        }
        else if (message.Topic == _topics.Fire)
        {
            if (
                JsonSerializer.Deserialize<FireReading>(message.Payload, JsonOptions) is { } reading
            )
                await scope
                    .ServiceProvider.GetRequiredService<FireHandler>()
                    .HandleAsync(reading, ct);
            else
                logger.LogWarning(
                    "Yangın verisi parse edilemedi. Payload: {Payload}",
                    message.Payload
                );
        }
        else if (message.Topic == _topics.Barcode)
        {
            if (
                JsonSerializer.Deserialize<BarcodeReading>(message.Payload, JsonOptions) is
                { } reading
            )
                await scope
                    .ServiceProvider.GetRequiredService<BarcodeHandler>()
                    .HandleAsync(reading, ct);
            else
                logger.LogWarning(
                    "Barkod verisi parse edilemedi. Payload: {Payload}",
                    message.Payload
                );
        }
        else
            logger.LogDebug("Bilinmeyen topic, yoksayıldı: {Topic}", message.Topic);
    }
}
