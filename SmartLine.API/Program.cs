using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using SmartLine.API.Domain.Identity;
using SmartLine.API.Features.Auth;
using SmartLine.API.Features.Conveyor;
using SmartLine.API.Features.Inspection;
using SmartLine.API.Features.Monitoring;
using SmartLine.API.Features.Production;
using SmartLine.API.Features.Safety;
using SmartLine.API.Infrastructure.Authentication;
using SmartLine.API.Infrastructure.Configuration;
using SmartLine.API.Infrastructure.Hosting;
using SmartLine.API.Infrastructure.Messaging;
using SmartLine.API.Infrastructure.Messaging.Mqtt;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Infrastructure.Realtime;
using SmartLine.API.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration));
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "Connection string 'DefaultConnection' is missing or empty."
    );

var configuredProxy = builder.Configuration["ReverseProxy:KnownProxy"];
IPAddress? trustedProxy = null;
if (!string.IsNullOrWhiteSpace(configuredProxy))
{
    if (!IPAddress.TryParse(configuredProxy, out trustedProxy))
        throw new InvalidOperationException("ReverseProxy:KnownProxy must be a valid IP address.");
}
else if (builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "ReverseProxy:KnownProxy is required in Production so client IP rate limits trust only Caddy."
    );
}

builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

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
builder
    .Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options =>
            options.SigningKey.Length >= 32
            && !options.SigningKey.StartsWith("replace-", StringComparison.OrdinalIgnoreCase),
        "JWT signing key must be at least 32 random characters and cannot be a placeholder."
    )
    .ValidateOnStart();
builder
    .Services.AddOptions<DeviceKeyOptions>()
    .Bind(builder.Configuration.GetSection(DeviceKeyOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options =>
            options.Key.Length >= 32
            && !options.Key.StartsWith("replace-", StringComparison.OrdinalIgnoreCase),
        "Device key must be at least 32 random characters and cannot be a placeholder."
    )
    .ValidateOnStart();
builder
    .Services.AddOptions<BootstrapAdminOptions>()
    .Bind(builder.Configuration.GetSection(BootstrapAdminOptions.SectionName))
    .Validate(
        options =>
        {
            var bothEmpty = string.IsNullOrWhiteSpace(options.Email)
                && string.IsNullOrWhiteSpace(options.Password);
            var bothSecure = !string.IsNullOrWhiteSpace(options.Email)
                && options.Email.Contains('@')
                && options.Password.Length >= 12
                && !options.Password.StartsWith(
                    "replace-",
                    StringComparison.OrdinalIgnoreCase
                );
            return bothEmpty || bothSecure;
        },
        "Bootstrap admin credentials must both be empty or contain a valid email and non-placeholder password."
    )
    .ValidateOnStart();
builder.Services.AddSingleton(serviceProvider =>
    TimeZoneInfo.FindSystemTimeZoneById(
        serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TimeZoneOptions>>()
            .Value.Id
    )
);

builder
    .Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Jwt configuration section is missing.");

builder
    .Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            NameClaimType = System.Security.Claims.ClaimTypes.Name,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (
                    !string.IsNullOrEmpty(accessToken)
                    && context.HttpContext.Request.Path.StartsWithSegments("/hubs/dashboard")
                )
                    context.Token = accessToken;
                return Task.CompletedTask;
            },
        };
    })
    .AddScheme<AuthenticationSchemeOptions, DeviceKeyAuthenticationHandler>(
        DeviceKeyDefaults.Scheme,
        _ => { }
    );

builder
    .Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(
        AuthPolicies.Viewer,
        policy => policy.RequireRole(AuthRoles.Viewer, AuthRoles.Operator, AuthRoles.Admin)
    )
    .AddPolicy(
        AuthPolicies.Operator,
        policy => policy.RequireRole(AuthRoles.Operator, AuthRoles.Admin)
    )
    .AddPolicy(AuthPolicies.Admin, policy => policy.RequireRole(AuthRoles.Admin))
    .AddPolicy(
        AuthPolicies.Device,
        policy =>
            policy
                .AddAuthenticationSchemes(DeviceKeyDefaults.Scheme)
                .RequireAuthenticatedUser()
                .RequireRole(AuthRoles.Device)
    )
    .AddPolicy(
        AuthPolicies.OperatorOrDevice,
        policy =>
            policy
                .AddAuthenticationSchemes(
                    JwtBearerDefaults.AuthenticationScheme,
                    DeviceKeyDefaults.Scheme
                )
                .RequireAuthenticatedUser()
                .RequireRole(AuthRoles.Operator, AuthRoles.Admin, AuthRoles.Device)
    );

builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, EnvelopeAuthorizationMiddlewareResultHandler>();
builder.Services.AddScoped<JwtTokenService>();
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
    client => { client.Timeout = TimeSpan.FromSeconds(Math.Clamp(builder.Configuration.GetValue<int?>("Vision:CaptureTimeoutSeconds") ?? 35, 5, 120)); client.MaxResponseContentBufferSize = 1_000_000; }
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

static string RateLimitPartitionKey(HttpContext context, bool preferAuthenticatedUser = false)
{
    var subject = preferAuthenticatedUser
        ? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        : null;
    var client = subject ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return $"{client}:{context.Request.Path.Value?.ToLowerInvariant()}";
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        await context.HttpContext.Response.WriteAsJsonAsync(
            ApiResponse<object>.Error(
                "İstek limiti aşıldı. Lütfen daha sonra tekrar deneyin.",
                ErrorCodes.RateLimitExceeded
            ),
            ct
        );
    };

    void AddFixedWindowPolicy(
        string name,
        TimeSpan window,
        int permitLimit,
        bool preferAuthenticatedUser = false
    ) =>
        options.AddPolicy(
            name,
            context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    RateLimitPartitionKey(context, preferAuthenticatedUser),
                    _ =>
                        new FixedWindowRateLimiterOptions
                        {
                            AutoReplenishment = true,
                            Window = window,
                            PermitLimit = permitLimit,
                            QueueLimit = 0,
                            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        }
                )
        );

    AddFixedWindowPolicy("auth", TimeSpan.FromMinutes(1), 20);
    AddFixedWindowPolicy("sensor", TimeSpan.FromSeconds(1), 100);
    AddFixedWindowPolicy("fire", TimeSpan.FromSeconds(1), 1_000);
    AddFixedWindowPolicy("control", TimeSpan.FromSeconds(10), 200, preferAuthenticatedUser: true);
});

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

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
    await ContractSeeder.EnsureAsync(db);
    try { await scope.ServiceProvider.GetRequiredService<DatasetStore>().RecoverAsync(CancellationToken.None); }
    catch (ApiException error) when (error.StatusCode == 503)
    { app.Logger.LogError(error, "Dataset recovery is unavailable; other API services remain enabled."); }
}
await IdentityBootstrapper.InitializeAsync(app.Services, app.Environment);

var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
};
if (trustedProxy is not null)
    forwardedHeaders.KnownProxies.Add(trustedProxy);
app.UseForwardedHeaders(forwardedHeaders);
app.Use(
    async (context, next) =>
    {
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Strict-Transport-Security"] =
            "max-age=31536000; includeSubDomains";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        await next();
    }
);
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    if (response.HasStarted || response.ContentLength is > 0)
        return;

    var (message, code) = response.StatusCode switch
    {
        StatusCodes.Status404NotFound => ("Kaynak bulunamadı", ErrorCodes.ResourceNotFound),
        StatusCodes.Status401Unauthorized => (
            "Kimlik doğrulaması gerekli",
            ErrorCodes.AuthenticationRequired
        ),
        StatusCodes.Status403Forbidden => ("Bu işlem için yetkiniz yok", ErrorCodes.AccessDenied),
        StatusCodes.Status429TooManyRequests => (
            "İstek limiti aşıldı",
            ErrorCodes.RateLimitExceeded
        ),
        _ => ("İstek işlenemedi", ErrorCodes.RequestInvalid),
    };
    await response.WriteAsJsonAsync(
        ApiResponse<object>.Error(message, code),
        statusContext.HttpContext.RequestAborted
    );
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapConveyorEndpoints();
app.MapProductEndpoints();
app.MapAlertEndpoints();
app.MapLogEndpoints();
app.MapStatisticsEndpoints();
app.MapBarcodeEndpoint();
app.MapSensorSafetyEndpoints();
app.MapDashboardEndpoint();
app.MapInspectionPlatform();
app.MapVisionInspection();
app.MapHub<DashboardHub>("/hubs/dashboard").RequireAuthorization(AuthPolicies.Viewer);
app.MapHealthChecks("/health").AllowAnonymous();
app.MapFallback(() =>
        Results.NotFound(
            ApiResponse<object>.Error("Kaynak bulunamadı", ErrorCodes.ResourceNotFound)
        )
    )
    .AllowAnonymous();

app.Run();

public partial class Program;
