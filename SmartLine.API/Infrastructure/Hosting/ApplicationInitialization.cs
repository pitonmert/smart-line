using Microsoft.EntityFrameworkCore;
using SmartLine.API.Features.Inspection;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Shared;

namespace SmartLine.API.Infrastructure.Hosting;

public static class ApplicationInitialization
{
    public static async Task InitializeApplicationAsync(this WebApplication app)
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.MigrateAsync();
            await ContractSeeder.EnsureAsync(db);
            try
            {
                await scope
                    .ServiceProvider.GetRequiredService<DatasetStore>()
                    .RecoverAsync(CancellationToken.None);
            }
            catch (ApiException error) when (error.StatusCode == 503)
            {
                // Dataset depolaması kapalı olsa da diğer API işlevleri açılabilir.
                app.Logger.LogError(
                    error,
                    "Dataset recovery is unavailable; other API services remain enabled."
                );
            }
        }
    }
}
