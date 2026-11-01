using SmartLine.API.Features.Conveyor;
using SmartLine.API.Features.Inspection;
using SmartLine.API.Features.Monitoring;
using SmartLine.API.Features.Production;
using SmartLine.API.Features.Safety;
using SmartLine.API.Infrastructure.Hosting;
using SmartLine.API.Infrastructure.Realtime;

var builder = WebApplication.CreateBuilder(args);

builder.AddDatabase();

builder.AddValidatedConfiguration();

builder.AddApplicationServices();

var app = builder.Build();

// Complete migrations and state recovery before accepting requests.
await app.InitializeApplicationAsync();

app.MapHub<DashboardHub>("/hubs/dashboard");

app.UseApiPipeline();

// Unknown API routes must return JSON errors, not the Dashboard HTML.
app.MapDashboardFallback();

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

app.MapHealthChecks("/health");

app.Run();

// Required by WebApplicationFactory integration tests.
public partial class Program;
