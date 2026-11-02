using Microsoft.EntityFrameworkCore;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Domain.Enums;
using SmartLine.API.Features.Monitoring;
using SmartLine.API.Features.Safety;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Production;

public static class BarcodeEndpoints
{
    public static RouteGroupBuilder MapBarcodeEndpoint(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sensors");
        group.MapPost(
            "/barcode",
            async (BarcodeReading request, BarcodeHandler handler, CancellationToken ct) =>
            {
                await handler.HandleAsync(request, ct);
                return Results.Ok(
                    ApiResponse<string>.Success(
                        string.IsNullOrEmpty(request.Barcode)
                            ? "Barkod okunamadı — ürün kaydedildi (NO_BARCODE)"
                            : $"Ürün kaydedildi: {request.Barcode}"
                    )
                );
            }
        );
        return group;
    }
}

public sealed record BarcodeReading(string Barcode, string? Color);

public sealed class BarcodeHandler(
    ApplicationDbContext db,
    ProductHandler products,
    AlertHandler alerts,
    LogHandler logs,
    StatisticsHandler statistics
)
{
    public async Task HandleAsync(BarcodeReading reading, CancellationToken ct = default)
    {
        if (reading.Barcode?.Length > 50 || reading.Color?.Length > 20)
            throw new ValidationException("Geçersiz barkod veya renk verisi");

        ProductColor? color = null;
        if (!string.IsNullOrWhiteSpace(reading.Color))
        {
            if (!EnumParser.TryParseDefined<ProductColor>(reading.Color, out var parsedColor))
                throw new ValidationException(
                    "Geçersiz ürün rengi",
                    ErrorCodes.ProductColorInvalid
                );
            color = parsedColor;
        }

        var barcode = reading.Barcode?.Trim() ?? string.Empty;
        var status = ProductStatus.OK;
        if (string.IsNullOrEmpty(barcode))
        {
            barcode = $"UNKNOWN_{Guid.NewGuid():N}";
            status = ProductStatus.NO_BARCODE;
        }
        else if (await db.Products.AnyAsync(product => product.Barcode == barcode, ct))
        {
            throw new DuplicateResourceException($"Bu barkod daha önce okutulmuş: {barcode}");
        }

        Alert? alert = null;
        var stagedLogs = new List<SystemLog>();
        Product product;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (status == ProductStatus.NO_BARCODE)
        {
            alert = await alerts.StageCreateAsync(AlertType.NO_BARCODE_DETECTED, ct);
            stagedLogs.Add(
                await logs.StageAsync(
                    EventType.NO_BARCODE_DETECTED,
                    "Barkod okunamadı!",
                    "Warning",
                    ct
                )
            );
        }

        product = await products.StageCreateAsync(
            new CreateProductRequest(barcode, color?.ToString(), status.ToString()),
            ct
        );
        if (status == ProductStatus.OK)
            stagedLogs.Add(
                await logs.StageAsync(
                    EventType.PRODUCT_PASSED,
                    $"Ürün geçti: {barcode}, Renk: {color}",
                    ct: ct
                )
            );

        await db.SaveChangesAsync(ct);
        await statistics.IncrementAsync(color, status, ct);
        await transaction.CommitAsync(ct);

        if (alert is not null)
            await alerts.NotifyAsync(alert, ct);
        await products.NotifyAsync(product, ct);
        foreach (var log in stagedLogs)
            await logs.NotifyAsync(log, ct);
    }
}
