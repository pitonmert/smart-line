using System.ComponentModel.DataAnnotations;

namespace SmartLine.API.Infrastructure.Messaging.Mqtt;

public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    [Required]
    public string BrokerAddress { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 8883;

    [Required]
    public string ClientId { get; set; } = "smartline-api";

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    [Required]
    public string CaCertificatePath { get; set; } = string.Empty;

    [Required]
    public string ClientCertificatePath { get; set; } = string.Empty;

    [Required]
    public string ClientKeyPath { get; set; } = string.Empty;

    public MqttTopicOptions Topics { get; set; } = new();
}

public sealed class MqttTopicOptions
{
    [Required]
    public string Environment { get; set; } = "smartline/sensors/environment";

    [Required]
    public string Barcode { get; set; } = "smartline/sensors/barcode";

    [Required]
    public string Fire { get; set; } = "smartline/sensors/fire";

    [Required]
    public string CommandSpeed { get; set; } = "smartline/commands/speed";

    [Required]
    public string CommandStop { get; set; } = "smartline/commands/stop";

    [Required]
    public string CommandFan { get; set; } = "smartline/commands/fan";

    public IEnumerable<string> All =>
        [Environment, Barcode, Fire, CommandSpeed, CommandStop, CommandFan];

    public IEnumerable<string> SensorTopics => [Environment, Barcode, Fire];
}
