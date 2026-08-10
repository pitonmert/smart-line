using Microsoft.EntityFrameworkCore;
using SmartLine.API.Domain.Enums;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Infrastructure.Authentication;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Monitoring;

public static class StatisticsFeature
{
    public static RouteGroupBuilder MapStatisticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/api/statistics")
            .RequireAuthorization(AuthPolicies.Viewer);
        group.MapGet(
            "/daily",
            async (
                DateOnly? startDate,
                DateOnly? endDate,
                StatisticsHandler handler,
                CancellationToken ct
            ) =>
            {
                var today = handler.Today;
                var start = startDate ?? today.AddDays(-7);
                var end = endDate ?? today;
                if (start > end)
                    return Results.BadRequest(
                        ApiResponse<IEnumerable<StatisticsResponse>>.Error(
                            "Başlangıç tarihi bitiş tarihinden büyük olamaz",
                            ErrorCodes.RequestInvalid
                        )
                    );
                if (end.DayNumber - start.DayNumber > 365)
                    return Results.BadRequest(
                        ApiResponse<IEnumerable<StatisticsResponse>>.Error(
                            "İstatistik tarih aralığı en fazla 366 gün olabilir",
                            ErrorCodes.RequestInvalid
                        )
                    );
                return Results.Ok(
                    ApiResponse<IEnumerable<StatisticsResponse>>.Success(
                        await handler.DailyAsync(start, end, ct)
                    )
                );
            }
        );
        group.MapGet(
            "/hourly",
            async (DateOnly? date, StatisticsHandler handler, CancellationToken ct) =>
                Results.Ok(
                    ApiResponse<IEnumerable<StatisticsResponse>>.Success(
                        await handler.HourlyAsync(date ?? handler.Today, ct)
                    )
                )
        );
        return group;
    }
}

public sealed record StatisticsResponse(
    int Id,
    DateOnly Date,
    int? Hour,
    int TotalProducts,
    int DefectiveProducts,
    int RedProducts,
    int BlueProducts,
    int GreenProducts,
    int YellowProducts
)
{
    public double DefectiveRate =>
        TotalProducts == 0 ? 0 : Math.Round((double)DefectiveProducts / TotalProducts * 100, 2);
}

public sealed class StatisticsHandler(ApplicationDbContext db, TimeZoneInfo timeZone)
{
    public DateOnly Today => LocalNow().DateOnly;

    public async Task<IEnumerable<StatisticsResponse>> DailyAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken ct = default
    ) =>
        (
            await db
                .Statistics.AsNoTracking()
                .Where(statistic =>
                    statistic.Date >= start && statistic.Date <= end && statistic.Hour == null
                )
                .OrderBy(statistic => statistic.Date)
                .ThenBy(statistic => statistic.Id)
                .ToListAsync(ct)
        )
            .Select(ToResponse)
            .ToList();

    public async Task<IEnumerable<StatisticsResponse>> HourlyAsync(
        DateOnly date,
        CancellationToken ct = default
    ) =>
        (
            await db
                .Statistics.AsNoTracking()
                .Where(statistic => statistic.Date == date && statistic.Hour != null)
                .OrderBy(statistic => statistic.Hour)
                .ThenBy(statistic => statistic.Id)
                .Take(24)
                .ToListAsync(ct)
        )
            .Select(ToResponse)
            .ToList();

    public async Task<StatisticsResponse?> TodayAsync(CancellationToken ct = default) =>
        (
            await db
                .Statistics.AsNoTracking()
                .FirstOrDefaultAsync(
                    statistic => statistic.Date == Today && statistic.Hour == null,
                    ct
                )
        ) is { } statistic
            ? ToResponse(statistic)
            : null;

    internal static StatisticsResponse ToResponse(Statistic statistic) =>
        new(
            statistic.Id,
            statistic.Date,
            statistic.Hour,
            statistic.TotalProducts,
            statistic.DefectiveProducts,
            statistic.RedProducts,
            statistic.BlueProducts,
            statistic.GreenProducts,
            statistic.YellowProducts
        );

    private (DateOnly DateOnly, int Hour) LocalNow()
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
        return (DateOnly.FromDateTime(local), local.Hour);
    }

    public async Task IncrementAsync(
        ProductColor? color,
        ProductStatus status,
        CancellationToken ct = default
    )
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);
        var date = DateOnly.FromDateTime(local);
        await UpsertAsync(date, null, color, status, ct);
        await UpsertAsync(date, local.Hour, color, status, ct);
    }

    private Task<int> UpsertAsync(
        DateOnly date,
        int? hour,
        ProductColor? color,
        ProductStatus status,
        CancellationToken ct
    )
    {
        var defective = status != ProductStatus.OK ? 1 : 0;
        var red = color == ProductColor.RED ? 1 : 0;
        var blue = color == ProductColor.BLUE ? 1 : 0;
        var green = color == ProductColor.GREEN ? 1 : 0;
        var yellow = color == ProductColor.YELLOW ? 1 : 0;
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "Statistics"
                ("Date", "Hour", "TotalProducts", "DefectiveProducts", "RedProducts", "BlueProducts", "GreenProducts", "YellowProducts")
            VALUES
                ({date}, {hour}, 1, {defective}, {red}, {blue}, {green}, {yellow})
            ON CONFLICT ("Date", "Hour") DO UPDATE SET
                "TotalProducts" = "Statistics"."TotalProducts" + 1,
                "DefectiveProducts" = "Statistics"."DefectiveProducts" + EXCLUDED."DefectiveProducts",
                "RedProducts" = "Statistics"."RedProducts" + EXCLUDED."RedProducts",
                "BlueProducts" = "Statistics"."BlueProducts" + EXCLUDED."BlueProducts",
                "GreenProducts" = "Statistics"."GreenProducts" + EXCLUDED."GreenProducts",
                "YellowProducts" = "Statistics"."YellowProducts" + EXCLUDED."YellowProducts";
            """,
            ct
        );
    }
}
