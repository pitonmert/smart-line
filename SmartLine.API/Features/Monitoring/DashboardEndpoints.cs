using SmartLine.API.Features.Conveyor;
using SmartLine.API.Features.Production;
using SmartLine.API.Features.Safety;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Monitoring;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/dashboard",
                async (DashboardHandler handler, CancellationToken ct) =>
                    Results.Ok(ApiResponse<DashboardResponse>.Success(await handler.GetAsync(ct)))
            );
        return app;
    }
}

public sealed record DashboardResponse(
    ConveyorStatusResponse ConveyorStatus,
    List<AlertResponse> ActiveAlerts,
    StatisticsResponse? TodayStatistics,
    List<ProductResponse> RecentProducts
);

public sealed class DashboardHandler(
    ConveyorHandler conveyor,
    AlertHandler alerts,
    StatisticsHandler statistics,
    ProductHandler products
)
{
    public async Task<DashboardResponse> GetAsync(CancellationToken ct)
    {
        var conveyorStatus = await conveyor.GetStatusAsync(ct);
        var activeAlerts = (await alerts.ActiveAsync(1, 100, ct)).Items;
        var today = await statistics.TodayAsync(ct);
        var recentProducts = (await products.GetAllAsync(1, 5, ct)).Items;
        return new DashboardResponse(conveyorStatus, activeAlerts, today, recentProducts);
    }
}
