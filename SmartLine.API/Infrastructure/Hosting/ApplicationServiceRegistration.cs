using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SmartLine.API.Features.Conveyor;
using SmartLine.API.Features.Inspection;
using SmartLine.API.Features.Monitoring;
using SmartLine.API.Features.Production;
using SmartLine.API.Features.Safety;
using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Infrastructure.Messaging;
using SmartLine.API.Infrastructure.Messaging.Mqtt;
using SmartLine.API.Infrastructure.Persistence;

namespace SmartLine.API.Infrastructure.Hosting;

public static class ApplicationServiceRegistration
{
    public static void AddDatabase(this WebApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "Connection string 'DefaultConnection' is missing or empty."
            );

        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString)
        );
    }

    public static void AddApplicationServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks();
        builder.Services.AddSignalR();

        builder.Services.AddScoped<LogHandler>();
        builder.Services.AddSingleton<ConveyorCommandGate>();
        builder.Services.AddScoped<ConveyorHandler>();
        builder.Services.AddScoped<ProductHandler>();
        builder.Services.AddScoped<BarcodeHandler>();
        builder.Services.AddScoped<AlertHandler>();
        builder.Services.AddScoped<EnvironmentHandler>();
        builder.Services.AddScoped<FireHandler>();
        builder.Services.AddScoped<StatisticsHandler>();
        builder.Services.AddScoped<DashboardHandler>();
        builder.Services.AddScoped<InspectionWorkflow>();
        builder.Services.AddScoped<DatasetStore>();
        builder.Services.AddSingleton<SimulationLatch>();
        builder.Services.AddHttpClient(
            "vision-health",
            client => client.Timeout = TimeSpan.FromMilliseconds(800)
        );
        builder.Services.AddHttpClient(
            "vision-capture",
            client =>
            {
                var timeoutSeconds =
                    builder.Configuration.GetValue<int?>("Vision:CaptureTimeoutSeconds") ?? 35;
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120));
                client.MaxResponseContentBufferSize = 1_000_000;
            }
        );
        builder.Services.AddSingleton<IVisionFrames, HttpVisionFrames>();
        builder.Services.AddSingleton<IVisionProbe, HttpVisionProbe>();
        builder
            .Services.AddOptions<SortingOptions>()
            .Bind(builder.Configuration.GetSection(SortingOptions.SectionName));
        builder
            .Services.AddOptions<SimulationOptions>()
            .Bind(builder.Configuration.GetSection(SimulationOptions.SectionName));
        builder
            .Services.AddOptions<VisionEndpointOptions>()
            .Bind(builder.Configuration.GetSection(VisionEndpointOptions.SectionName));
        builder
            .Services.AddOptions<DatasetOptions>()
            .Bind(builder.Configuration.GetSection(DatasetOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Root),
                "Dataset:Root must be configured."
            )
            .ValidateOnStart();

        builder.Services.AddSingleton<IMqttService, MqttService>();
        builder.Services.AddSingleton<MqttDesiredStatePublisher>();

        var sensorChannel = Channel.CreateBounded<SensorMessage>(
            new BoundedChannelOptions(1_000)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );
        builder.Services.AddSingleton(sensorChannel);
        builder.Services.AddSingleton(sensorChannel.Writer);
        builder.Services.AddSingleton(sensorChannel.Reader);
        builder.Services.AddHostedService<MqttListenerService>();
        builder.Services.AddHostedService<SensorDataProcessorService>();
    }
}
