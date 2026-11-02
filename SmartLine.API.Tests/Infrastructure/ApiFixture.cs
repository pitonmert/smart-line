using SmartLine.API.Features.Inspection;

namespace SmartLine.API.Tests.Infrastructure;

public sealed class ApiFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _databaseLock = new(1, 1);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("smartline_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private SmartLineApiFactory? _factory;

    public RecordingMqttService Mqtt => Factory.Services.GetRequiredService<RecordingMqttService>();

    private SmartLineApiFactory Factory =>
        _factory ?? throw new InvalidOperationException("API fixture has not been initialized.");

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new SmartLineApiFactory(_postgres.GetConnectionString());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
            await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
        _databaseLock.Dispose();
    }

    public HttpClient CreateClient() =>
        Factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            }
        );

    public WebSocketClient CreateHubWebSocketClient() => Factory.Server.CreateWebSocketClient();

    public IsolatedApiClient CreateDashboardClient(string webRoot)
    {
        var factory = new SmartLineApiFactory(_postgres.GetConnectionString(), webRoot);
        return new IsolatedApiClient(factory, factory.CreateClient());
    }

    public IsolatedApiClient CreateIsolatedAnonymousClient()
    {
        var factory = new SmartLineApiFactory(_postgres.GetConnectionString());
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            }
        );
        return new IsolatedApiClient(factory, client);
    }

    public T GetRequiredService<T>()
        where T : notnull => Factory.Services.GetRequiredService<T>();

    public async Task ResetAsync()
    {
        await _databaseLock.WaitAsync();
        try
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            scope.ServiceProvider.GetService<ScriptedVisionFrames>()?.Reset();
            Mqtt.Reset();
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    public async Task<T> WithDbContextAsync<T>(Func<ApplicationDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await action(db);
    }

    public async Task WithDbContextAsync(Func<ApplicationDbContext, Task> action)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await action(db);
    }

    private sealed class SmartLineApiFactory(string connectionString, string? webRoot = null)
        : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            if (webRoot is not null)
                builder.UseWebRoot(webRoot);
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("Mqtt:Username", "integration-test-api");
            builder.UseSetting("Mqtt:Password", new string('m', 64));
            builder.UseSetting("Mqtt:CaCertificatePath", "/tmp/integration-test-ca.crt");
            builder.UseSetting("Mqtt:ClientCertificatePath", "/tmp/integration-test-client.crt");
            builder.UseSetting("Mqtt:ClientKeyPath", "/tmp/integration-test-client.key");
            builder.UseSetting("Simulation:Enabled", "true");
            builder.UseSetting("Sorting:HardwareActuationEnabled", "false");
            builder.UseSetting("Vision:HealthUrl", "");
            builder.UseSetting(
                "Dataset:Root",
                Path.Combine(Path.GetTempPath(), "smartline-api-tests", "datasets")
            );
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ApplicationDbContext>();
                services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseNpgsql(connectionString)
                );

                var mqttHostedServices = services
                    .Where(descriptor =>
                        descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType is { } implementationType
                        && (
                            implementationType == typeof(MqttListenerService)
                            || implementationType == typeof(SensorDataProcessorService)
                        )
                    )
                    .ToList();
                foreach (var descriptor in mqttHostedServices)
                    services.Remove(descriptor);

                services.RemoveAll<IMqttService>();
                services.AddSingleton<RecordingMqttService>();
                services.AddSingleton<IMqttService>(provider =>
                    provider.GetRequiredService<RecordingMqttService>()
                );
                services.RemoveAll<IVisionFrames>();
                services.AddSingleton<ScriptedVisionFrames>();
                services.AddSingleton<IVisionFrames>(provider =>
                    provider.GetRequiredService<ScriptedVisionFrames>()
                );
            });
        }
    }

    public sealed class IsolatedApiClient(
        WebApplicationFactory<global::Program> factory,
        HttpClient client
    ) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
        }
    }
}
