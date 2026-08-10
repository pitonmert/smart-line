using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SmartLine.API.Infrastructure.Messaging;
using SmartLine.API.Infrastructure.Messaging.Mqtt;

namespace SmartLine.API.Infrastructure.Hosting;

public sealed class MqttListenerService(
    IMqttService mqttService,
    ChannelWriter<SensorMessage> writer,
    MqttDesiredStatePublisher desiredStatePublisher,
    IOptions<MqttOptions> options,
    ILogger<MqttListenerService> logger
) : BackgroundService
{
    private readonly MqttOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mqttService.OnMessageReceived += (topic, payload) =>
            writer.WriteAsync(new SensorMessage(topic, payload), stoppingToken).AsTask();
        var connectionInitialized = false;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!mqttService.IsConnected)
                {
                    connectionInitialized = false;
                    try
                    {
                        await mqttService.StartAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "MQTT broker {Address}:{Port} is unavailable; retrying in five seconds.",
                            _options.BrokerAddress,
                            _options.Port
                        );
                    }
                }

                if (mqttService.IsConnected && !connectionInitialized)
                {
                    try
                    {
                        foreach (var topic in _options.Topics.SensorTopics)
                            await mqttService.SubscribeAsync(topic, stoppingToken);
                        await desiredStatePublisher.PublishAsync(stoppingToken);
                        connectionInitialized = true;
                        logger.LogInformation(
                            "MQTT subscriptions and desired state were restored."
                        );
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "MQTT connection initialization failed; retrying in five seconds."
                        );
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            try
            {
                await mqttService.StopAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "MQTT shutdown failed.");
            }
        }

    }
}
