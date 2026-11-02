using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Infrastructure.Messaging.Mqtt;

namespace SmartLine.API.Infrastructure.Hosting;

public static class ConfigurationRegistration
{
    public static void AddValidatedConfiguration(this WebApplicationBuilder builder)
    {
        builder
            .Services.AddOptions<EnvironmentThresholdsOptions>()
            .Bind(builder.Configuration.GetSection(EnvironmentThresholdsOptions.SectionName))
            .Validate(
                options =>
                    options.HighTempThreshold is >= -40 and <= 80
                    && options.AlarmTempThreshold is >= -40 and <= 80
                    && options.AlarmTempThreshold > options.HighTempThreshold
                    && options.FanHysteresisCelsius is > 0 and <= 20,
                "Environment thresholds are outside their safe range or relationship."
            )
            .ValidateOnStart();
        builder
            .Services.AddOptions<TimeZoneOptions>()
            .Bind(builder.Configuration.GetSection(TimeZoneOptions.SectionName))
            .Validate(
                options =>
                {
                    try
                    {
                        _ = TimeZoneInfo.FindSystemTimeZoneById(options.Id);
                        return true;
                    }
                    catch (TimeZoneNotFoundException)
                    {
                        return false;
                    }
                    catch (InvalidTimeZoneException)
                    {
                        return false;
                    }
                },
                "TimeZone:Id must identify an installed IANA or Windows time zone."
            )
            .ValidateOnStart();
        builder
            .Services.AddOptions<MqttOptions>()
            .Bind(builder.Configuration.GetSection(MqttOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options =>
                    options.Port == 8883
                    && options.Password.Length >= 32
                    && !options.Password.StartsWith("replace-", StringComparison.OrdinalIgnoreCase)
                    && options.Topics.All.All(topic =>
                        !string.IsNullOrWhiteSpace(topic)
                        && topic.StartsWith("smartline/", StringComparison.Ordinal)
                    ),
                "MQTT must use TLS port 8883 and configured smartline topics."
            )
            .Validate(
                options =>
                    builder.Environment.IsEnvironment("Testing")
                    || (
                        File.Exists(options.CaCertificatePath)
                        && File.Exists(options.ClientCertificatePath)
                        && File.Exists(options.ClientKeyPath)
                    ),
                "MQTT CA, client certificate, and client private key files must exist."
            )
            .ValidateOnStart();
        builder.Services.AddSingleton(serviceProvider =>
            TimeZoneInfo.FindSystemTimeZoneById(
                serviceProvider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<TimeZoneOptions>>()
                    .Value.Id
            )
        );
    }
}
