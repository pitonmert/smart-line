namespace SmartLine.API.Infrastructure.Messaging.Mqtt;

public interface IMqttService
{
    bool IsConnected { get; }
    event Func<string, string, Task>? OnMessageReceived;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SubscribeAsync(string topicFilter, CancellationToken ct = default);
    Task PublishAsync(
        string topic,
        string payload,
        bool retain = false,
        CancellationToken ct = default
    );
    Task PublishSpeedChangeAsync(int speed, CancellationToken ct = default);
    Task PublishConveyorStateAsync(
        bool isRunning,
        bool emergency,
        int resetGeneration,
        string acknowledgedFireEpoch,
        long acknowledgedFireGeneration,
        CancellationToken ct = default
    );
    Task PublishFanAsync(bool active, CancellationToken ct = default);
}
