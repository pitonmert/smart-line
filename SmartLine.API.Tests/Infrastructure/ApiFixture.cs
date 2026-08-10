namespace SmartLine.API.Tests.Infrastructure;

public sealed class ApiFixture : IAsyncLifetime
{
    private const string JwtIssuer = "SmartLine.API.Tests";
    private const string JwtAudience = "SmartLine.API.Tests.Client";
    private static readonly string JwtSigningKey = new('j', 64);
    private static readonly string DeviceKey = new('d', 64);
    private readonly SemaphoreSlim _databaseLock = new(1, 1);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("smartline_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private SmartLineApiFactory? _factory;

    public RecordingMqttService Mqtt =>
        Factory.Services.GetRequiredService<RecordingMqttService>();

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

    public HttpClient CreateAnonymousClient() => CreateClient();

    public HttpClient CreateUserClient(params string[] roles) =>
        CreateClient("integration-user", roles);

    public HttpClient CreateDeviceClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Key", DeviceKey);
        return client;
    }

    public WebSocketClient CreateHubWebSocketClient(params string[] roles)
    {
        var client = Factory.Server.CreateWebSocketClient();
        if (roles.Length == 0)
            return client;

        var token = CreateAccessToken("integration-hub-user", roles);
        client.ConfigureRequest = request =>
            request.Headers.Authorization = $"Bearer {token}";
        return client;
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
            await SeedRolesAsync(scope.ServiceProvider);
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

    public async Task<ApplicationUser> CreateIdentityUserAsync(
        string email,
        string password,
        string role
    )
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        var createResult = await users.CreateAsync(user, password);
        Assert.True(
            createResult.Succeeded,
            string.Join("; ", createResult.Errors.Select(error => error.Description))
        );
        var roleResult = await users.AddToRoleAsync(user, role);
        Assert.True(
            roleResult.Succeeded,
            string.Join("; ", roleResult.Errors.Select(error => error.Description))
        );
        return user;
    }

    private HttpClient CreateClient(string? username = null, IReadOnlyCollection<string>? roles = null)
    {
        var client = Factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost"),
            }
        );
        if (username is null)
            return client;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateAccessToken(username, roles ?? [])
        );
        return client;
    }

    private static string CreateAccessToken(
        string username,
        IReadOnlyCollection<string> roles
    )
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, username),
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey)),
            SecurityAlgorithms.HmacSha256
        );
        var token = new JwtSecurityToken(
            JwtIssuer,
            JwtAudience,
            claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task SeedRolesAsync(IServiceProvider services)
    {
        var roles = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var roleName in new[] { "Viewer", "Operator", "Admin" })
        {
            if (!await roles.RoleExistsAsync(roleName))
            {
                var result = await roles.CreateAsync(new IdentityRole<Guid>(roleName));
                Assert.True(
                    result.Succeeded,
                    string.Join("; ", result.Errors.Select(error => error.Description))
                );
            }
        }
    }

    private sealed class SmartLineApiFactory(string connectionString)
        : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("Jwt:Issuer", JwtIssuer);
            builder.UseSetting("Jwt:Audience", JwtAudience);
            builder.UseSetting("Jwt:SigningKey", JwtSigningKey);
            builder.UseSetting("DeviceAuthentication:Key", DeviceKey);
            builder.UseSetting("Mqtt:Username", "integration-test-api");
            builder.UseSetting("Mqtt:Password", new string('m', 64));
            builder.UseSetting("Mqtt:CaCertificatePath", "/tmp/integration-test-ca.crt");
            builder.UseSetting(
                "Mqtt:ClientCertificatePath",
                "/tmp/integration-test-client.crt"
            );
            builder.UseSetting("Mqtt:ClientKeyPath", "/tmp/integration-test-client.key");
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
                        && descriptor.ImplementationType
                            is { } implementationType
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
