using SmartLine.API.Shared;

namespace SmartLine.API.Infrastructure.Hosting;

public static class DashboardHosting
{
    public static void MapDashboardFallback(this WebApplication app)
    {
        app.MapFallback(
            "/{**path}",
            (HttpContext context) =>
            {
                var path = context.Request.Path;
                // Unknown service routes and missing assets must never return the SPA shell.
                if (
                    (
                        HttpMethods.IsGet(context.Request.Method)
                        || HttpMethods.IsHead(context.Request.Method)
                    )
                    && !path.StartsWithSegments("/api")
                    && !path.StartsWithSegments("/hubs")
                    && !path.StartsWithSegments("/health")
                    && !Path.HasExtension(path.Value)
                )
                {
                    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
                    if (index.Exists && index.PhysicalPath is not null)
                        return (IResult)Results.File(index.PhysicalPath, "text/html");
                }

                return Results.NotFound(
                    ApiResponse<object>.Error("Kaynak bulunamadı", ErrorCodes.ResourceNotFound)
                );
            }
        );
    }
}
