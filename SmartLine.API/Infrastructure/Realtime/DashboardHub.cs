using Microsoft.AspNetCore.SignalR;

namespace SmartLine.API.Infrastructure.Realtime;

public sealed class DashboardHub(ILogger<DashboardHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        logger.LogInformation("Dashboard istemcisi bağlandı: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogInformation("Dashboard istemcisi ayrıldı: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
