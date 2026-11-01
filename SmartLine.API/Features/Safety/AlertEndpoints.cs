using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SmartLine.API.Domain.Enums;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Features.Monitoring;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Infrastructure.Realtime;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Safety;

public static class AlertEndpoints
{
    public static RouteGroupBuilder MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/api/alerts");
        group.MapGet(
            "",
            async (int? pageNumber, int? pageSize, AlertHandler handler, CancellationToken ct) =>
                Results.Ok(
                    ApiResponse<PagedResult<AlertResponse>>.Success(
                        await handler.GetAllAsync(pageNumber ?? 1, pageSize ?? 20, ct)
                    )
                )
        );
        group.MapGet(
            "/{id:int}",
            async (int id, AlertHandler handler, CancellationToken ct) =>
                (await handler.ByIdAsync(id, ct)) is { } alert
                    ? Results.Ok(ApiResponse<AlertResponse>.Success(alert))
                    : Results.NotFound(
                        ApiResponse<AlertResponse>.Error(
                            $"ID {id} olan alarm bulunamadı",
                            ErrorCodes.ResourceNotFound
                        )
                    )
        );
        group.MapGet(
            "/active",
            async (
                int? pageNumber,
                int? pageSize,
                AlertHandler handler,
                CancellationToken ct
            ) =>
                Results.Ok(
                    ApiResponse<PagedResult<AlertResponse>>.Success(
                        await handler.ActiveAsync(pageNumber ?? 1, pageSize ?? 20, ct)
                    )
                )
        );
        group.MapGet(
            "/type/{type}",
            async (
                string type,
                int? pageNumber,
                int? pageSize,
                AlertHandler handler,
                CancellationToken ct
            ) =>
                EnumParser.TryParseDefined<AlertType>(type, out var parsed)
                    ? Results.Ok(
                        ApiResponse<PagedResult<AlertResponse>>.Success(
                            await handler.ByTypeAsync(
                                parsed,
                                pageNumber ?? 1,
                                pageSize ?? 20,
                                ct
                            )
                        )
                    )
                    : Results.BadRequest(
                        ApiResponse<PagedResult<AlertResponse>>.Error(
                            "Geçersiz alarm türü",
                            ErrorCodes.RequestInvalid
                        )
                    )
        );
        group
            .MapPost(
                "/{id:int}/resolve",
                async (int id, AlertHandler handler, CancellationToken ct) =>
                    Results.Ok(
                        ApiResponse<AlertResponse>.Success(
                            await handler.ResolveAsync(id, ct),
                            $"Alarm #{id} çözüldü"
                        )
                    )
            );
        return group;
    }
}

public sealed record AlertResponse(
    int Id,
    string AlertType,
    bool IsActive,
    DateTime TriggeredAt,
    DateTime? ResolvedAt
);

public sealed class AlertHandler(
    ApplicationDbContext db,
    LogHandler logs,
    IHubContext<DashboardHub> hub,
    ILogger<AlertHandler> logger
)
{
    public Task<PagedResult<AlertResponse>> GetAllAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct
    ) => PageAsync(db.Alerts.AsNoTracking(), pageNumber, pageSize, ct);

    public async Task<AlertResponse?> ByIdAsync(int id, CancellationToken ct = default) =>
        (await db.Alerts.AsNoTracking().FirstOrDefaultAsync(alert => alert.Id == id, ct))
            is { } alert
            ? ToResponse(alert)
            : null;

    public Task<PagedResult<AlertResponse>> ActiveAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct
    ) =>
        PageAsync(
            db.Alerts.AsNoTracking().Where(alert => alert.IsActive),
            pageNumber,
            pageSize,
            ct
        );

    public Task<PagedResult<AlertResponse>> ByTypeAsync(
        AlertType type,
        int pageNumber,
        int pageSize,
        CancellationToken ct
    ) =>
        PageAsync(
            db.Alerts.AsNoTracking().Where(alert => alert.AlertType == type),
            pageNumber,
            pageSize,
            ct
        );

    public async Task<Alert> StageCreateAsync(
        AlertType type,
        CancellationToken ct = default
    )
    {
        var alert = new Alert
        {
            AlertType = type,
            IsActive = true,
            TriggeredAt = DateTime.UtcNow,
        };
        await db.Alerts.AddAsync(alert, ct);
        return alert;
    }

    public async Task<AlertResponse> ResolveAsync(int id, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var resolvedAt = DateTime.UtcNow;
        var affected = await db
            .Alerts.Where(alert => alert.Id == id && alert.IsActive)
            .ExecuteUpdateAsync(
                setters =>
                    setters
                        .SetProperty(alert => alert.IsActive, false)
                        .SetProperty(alert => alert.ResolvedAt, resolvedAt)
                        .SetProperty(alert => alert.UpdatedAt, resolvedAt),
                ct
            );

        SystemLog? auditLog = null;
        if (affected == 1)
        {
            auditLog = await logs.StageAsync(
                EventType.ALERT_RESOLVED,
                $"Alarm #{id} çözüldü",
                ct: ct
            );
            await db.SaveChangesAsync(ct);
        }

        var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct)
            ?? throw new NotFoundException("Alert", id);
        await transaction.CommitAsync(ct);

        if (affected == 1)
        {
            await NotifyAsync(alert, ct);
            await logs.NotifyAsync(auditLog!, ct);
        }
        return ToResponse(alert);
    }

    public async Task NotifyAsync(Alert alert, CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.All.SendAsync("ReceiveAlertUpdate", ToResponse(alert), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "SignalR alert notification failed for alert {AlertId}.",
                alert.Id
            );
        }
    }

    internal static AlertResponse ToResponse(Alert alert) =>
        new(
            alert.Id,
            alert.AlertType.ToString(),
            alert.IsActive,
            alert.TriggeredAt,
            alert.ResolvedAt
        );

    private static async Task<PagedResult<AlertResponse>> PageAsync(
        IQueryable<Alert> query,
        int pageNumber,
        int pageSize,
        CancellationToken ct
    )
    {
        (pageNumber, pageSize) = Pagination.Normalize(pageNumber, pageSize);
        var items = await query
            .OrderByDescending(alert => alert.TriggeredAt)
            .ThenByDescending(alert => alert.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync(ct);
        var hasNextPage = items.Count > pageSize;
        if (hasNextPage)
            items.RemoveAt(items.Count - 1);
        return new PagedResult<AlertResponse>(
            items.Select(ToResponse).ToList(),
            hasNextPage,
            pageNumber,
            pageSize
        );
    }
}
