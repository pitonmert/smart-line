using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Domain.Enums;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Infrastructure.Realtime;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Monitoring;

public static class LogEndpoints
{
    public static RouteGroupBuilder MapLogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/logs");
        group.MapGet(
            "",
            async (int? pageNumber, int? pageSize, LogHandler handler, CancellationToken ct) =>
                Results.Ok(
                    ApiResponse<PagedResult<LogResponse>>.Success(
                        await handler.GetAllAsync(pageNumber ?? 1, pageSize ?? 20, ct)
                    )
                )
        );
        group.MapGet(
            "/severity/{level}",
            async (
                string level,
                int? pageNumber,
                int? pageSize,
                LogHandler handler,
                CancellationToken ct
            ) =>
            {
                var valid = LogHandler.ValidSeverities.FirstOrDefault(candidate =>
                    candidate.Equals(level, StringComparison.OrdinalIgnoreCase)
                );
                return valid is null
                    ? Results.BadRequest(
                        ApiResponse<PagedResult<LogResponse>>.Error(
                            "Geçersiz severity seviyesi",
                            ErrorCodes.RequestInvalid
                        )
                    )
                    : Results.Ok(
                        ApiResponse<PagedResult<LogResponse>>.Success(
                            await handler.BySeverityAsync(
                                valid,
                                pageNumber ?? 1,
                                pageSize ?? 20,
                                ct
                            )
                        )
                    );
            }
        );
        group.MapGet(
            "/event/{eventType}",
            async (
                string eventType,
                int? pageNumber,
                int? pageSize,
                LogHandler handler,
                CancellationToken ct
            ) =>
                EnumParser.TryParseDefined<EventType>(eventType, out var parsed)
                    ? Results.Ok(
                        ApiResponse<PagedResult<LogResponse>>.Success(
                            await handler.ByEventTypeAsync(
                                parsed,
                                pageNumber ?? 1,
                                pageSize ?? 20,
                                ct
                            )
                        )
                    )
                    : Results.BadRequest(
                        ApiResponse<PagedResult<LogResponse>>.Error(
                            "Geçersiz event türü",
                            ErrorCodes.RequestInvalid
                        )
                    )
        );
        return group;
    }
}

public sealed record LogResponse(
    int Id,
    string EventType,
    string? Message,
    string Severity,
    DateTime CreatedAt
);

public sealed class LogHandler(
    ApplicationDbContext db,
    IHubContext<DashboardHub> hub,
    ILogger<LogHandler> logger
)
{
    public static readonly string[] ValidSeverities = ["Info", "Warning", "Error", "Critical"];

    public Task<PagedResult<LogResponse>> GetAllAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct
    ) => PageAsync(db.SystemLogs.AsNoTracking(), pageNumber, pageSize, ct);

    public Task<PagedResult<LogResponse>> BySeverityAsync(
        string severity,
        int pageNumber,
        int pageSize,
        CancellationToken ct
    ) =>
        PageAsync(
            db.SystemLogs.AsNoTracking().Where(log => log.Severity == severity),
            pageNumber,
            pageSize,
            ct
        );

    public Task<PagedResult<LogResponse>> ByEventTypeAsync(
        EventType type,
        int pageNumber,
        int pageSize,
        CancellationToken ct
    ) =>
        PageAsync(
            db.SystemLogs.AsNoTracking().Where(log => log.EventType == type),
            pageNumber,
            pageSize,
            ct
        );

    public async Task<SystemLog> StageAsync(
        EventType eventType,
        string? message = null,
        string severity = "Info",
        CancellationToken ct = default
    )
    {
        var log = new SystemLog
        {
            EventType = eventType,
            Message = message,
            Severity = severity,
            CreatedAt = DateTime.UtcNow,
        };
        await db.SystemLogs.AddAsync(log, ct);
        return log;
    }

    public async Task NotifyAsync(SystemLog log, CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.All.SendAsync("ReceiveLogUpdate", ToResponse(log), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "SignalR log notification failed for log {LogId}.",
                log.Id
            );
        }
    }

    internal static LogResponse ToResponse(SystemLog log) =>
        new(log.Id, log.EventType.ToString(), log.Message, log.Severity, log.CreatedAt);

    private static async Task<PagedResult<LogResponse>> PageAsync(
        IQueryable<SystemLog> query,
        int pageNumber,
        int pageSize,
        CancellationToken ct
    )
    {
        (pageNumber, pageSize) = Pagination.Normalize(pageNumber, pageSize);
        var items = await query
            .OrderByDescending(log => log.CreatedAt)
            .ThenByDescending(log => log.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync(ct);
        var hasNextPage = items.Count > pageSize;
        if (hasNextPage)
            items.RemoveAt(items.Count - 1);
        return new PagedResult<LogResponse>(
            items.Select(ToResponse).ToList(),
            hasNextPage,
            pageNumber,
            pageSize
        );
    }
}
