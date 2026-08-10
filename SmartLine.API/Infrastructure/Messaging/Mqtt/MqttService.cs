using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using SmartLine.API.Shared;

namespace SmartLine.API.Infrastructure.Messaging.Mqtt;

public sealed class MqttService : IMqttService, IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttOptions _options;
    private readonly ILogger<MqttService> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly X509Certificate2 _caCertificate;
    private readonly X509Certificate2 _clientCertificate;
    private readonly MqttClientOptions _connectOptions;
    private volatile bool _stopping;

    public bool IsConnected => _client.IsConnected;
    public event Func<string, string, Task>? OnMessageReceived;

    public MqttService(IOptions<MqttOptions> options, ILogger<MqttService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
        _caCertificate = X509CertificateLoader.LoadCertificateFromFile(
            _options.CaCertificatePath
        );
        _clientCertificate = X509Certificate2.CreateFromPemFile(
            _options.ClientCertificatePath,
            _options.ClientKeyPath
        );
        _connectOptions = new MqttClientOptionsBuilder()
            .WithClientId(_options.ClientId)
            .WithTcpServer(_options.BrokerAddress, _options.Port)
            .WithCredentials(_options.Username, _options.Password)
            .WithCleanSession()
            .WithTlsOptions(tls =>
            {
                tls.UseTls();
                tls.WithTrustChain(new X509Certificate2Collection(_caCertificate));
                tls.WithClientCertificates(new X509Certificate2Collection(_clientCertificate));
                tls.WithCertificateValidationHandler(args =>
                    args.SslPolicyErrors == SslPolicyErrors.None
                );
            })
            .Build();
        _client.ApplicationMessageReceivedAsync += HandleMessageAsync;
        _client.ConnectedAsync += HandleConnectedAsync;
        _client.DisconnectedAsync += HandleDisconnectedAsync;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _stopping = false;
        if (!_client.IsConnected)
            await _client.ConnectAsync(_connectOptions, ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _stopping = true;
        await _lifetime.CancelAsync();
        if (_client.IsConnected)
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
    }

    public async Task SubscribeAsync(string topicFilter, CancellationToken ct = default)
    {
        EnsureConnected();
        await _client.SubscribeAsync(
            new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(filter =>
                    filter
                        .WithTopic(topicFilter)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                )
                .Build(),
            ct
        );
    }

    public async Task PublishAsync(
        string topic,
        string payload,
        bool retain = false,
        CancellationToken ct = default
    )
    {
        EnsureConnected();
        try
        {
            await _client.PublishAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(Encoding.UTF8.GetBytes(payload))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(retain)
                    .Build(),
                ct
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new MqttUnavailableException("MQTT broker komutu teslim edemedi", exception);
        }
    }

    public Task PublishSpeedChangeAsync(int speed, CancellationToken ct = default) =>
        PublishAsync(_options.Topics.CommandSpeed, $"{{\"speed\":{speed}}}", ct: ct);

    public Task PublishConveyorStateAsync(
        bool isRunning,
        bool emergency,
        int resetGeneration,
        string acknowledgedFireEpoch,
        long acknowledgedFireGeneration,
        CancellationToken ct = default
    ) =>
        PublishAsync(
            _options.Topics.CommandStop,
            JsonSerializer.Serialize(
                new
                {
                    isRunning,
                    emergency,
                    resetGeneration,
                    acknowledgedFireEpoch,
                    acknowledgedFireGeneration,
                }
            ),
            retain: true,
            ct
        );

    public Task PublishFanAsync(bool active, CancellationToken ct = default) =>
        PublishAsync(
            _options.Topics.CommandFan,
            $"{{\"active\":{active.ToString().ToLowerInvariant()}}}",
            ct: ct
        );

    private void EnsureConnected()
    {
        if (!_client.IsConnected)
            throw new MqttUnavailableException("MQTT broker bağlantısı kullanılamıyor");
    }

    private async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        if (OnMessageReceived is not null)
            await OnMessageReceived.Invoke(
                args.ApplicationMessage.Topic,
                args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty
            );
    }

    private Task HandleConnectedAsync(MqttClientConnectedEventArgs _)
    {
        _logger.LogInformation(
            "MQTT connection established. Broker: {Address}:{Port}",
            _options.BrokerAddress,
            _options.Port
        );
        return Task.CompletedTask;
    }

    private Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (_stopping || _lifetime.IsCancellationRequested)
            return Task.CompletedTask;

        _logger.LogWarning(args.Exception, "MQTT connection lost; the listener will reconnect.");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.ApplicationMessageReceivedAsync -= HandleMessageAsync;
        _client.ConnectedAsync -= HandleConnectedAsync;
        _client.DisconnectedAsync -= HandleDisconnectedAsync;
        await _lifetime.CancelAsync();
        _client.Dispose();
        _clientCertificate.Dispose();
        _caCertificate.Dispose();
        _lifetime.Dispose();
    }
}
