using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmartLine.API.Infrastructure.Persistence;

namespace SmartLine.API.Features.Inspection;

internal static class InspectionTransaction
{
    public static async Task<IDbContextTransaction> BeginAsync(
        ApplicationDbContext db,
        CancellationToken ct
    )
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Aynı ürün ve aktif yapılandırma üzerindeki yazmaları veritabanında sıraya alır.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(20261024)", ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
