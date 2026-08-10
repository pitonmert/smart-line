using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SmartLine.API.Domain.Enums;
using SmartLine.API.Domain.Entities;
using SmartLine.API.Infrastructure.Authentication;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Infrastructure.Realtime;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Production;

public static class ProductFeature
{
    public static RouteGroupBuilder MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app
            .MapGroup("/api/products")
            .RequireAuthorization(AuthPolicies.Viewer);
        group.MapGet(
            "",
            async (int? pageNumber, int? pageSize, ProductHandler handler, CancellationToken ct) =>
                Results.Ok(
                    ApiResponse<PagedResult<ProductResponse>>.Success(
                        await handler.GetAllAsync(pageNumber ?? 1, pageSize ?? 10, ct)
                    )
                )
        );
        group.MapGet(
            "/{id:int}",
            async (int id, ProductHandler handler, CancellationToken ct) =>
            {
                var product = await handler.ByIdAsync(id, ct);
                return product is null
                    ? Results.NotFound(
                        ApiResponse<ProductResponse>.Error(
                            $"ID {id} olan ürün bulunamadı",
                            ErrorCodes.ResourceNotFound
                        )
                    )
                    : Results.Ok(ApiResponse<ProductResponse>.Success(product));
            }
        );
        group
            .MapPost(
                "",
                async (CreateProductRequest request, ProductHandler handler, CancellationToken ct) =>
                {
                    var product = await handler.CreateAsync(request, ct);
                    return Results.Created(
                        $"/api/products/{product.Id}",
                        ApiResponse<ProductResponse>.Success(
                            product,
                            "Ürün başarıyla oluşturuldu"
                        )
                    );
                }
            )
            .RequireAuthorization(AuthPolicies.Operator);
        group.MapGet(
            "/defective",
            async (
                int? pageNumber,
                int? pageSize,
                ProductHandler handler,
                CancellationToken ct
            ) =>
                Results.Ok(
                    ApiResponse<PagedResult<ProductResponse>>.Success(
                        await handler.DefectiveAsync(pageNumber ?? 1, pageSize ?? 10, ct)
                    )
                )
        );
        group.MapGet(
            "/barcode/{barcode}",
            async (string barcode, ProductHandler handler, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(barcode))
                    return Results.BadRequest(
                        ApiResponse<ProductResponse>.Error(
                            "Barkod boş olamaz",
                            ErrorCodes.RequestInvalid
                        )
                    );
                var product = await handler.ByBarcodeAsync(barcode, ct);
                return product is null
                    ? Results.NotFound(
                        ApiResponse<ProductResponse>.Error(
                            $"Barkod '{barcode}' olan ürün bulunamadı",
                            ErrorCodes.ResourceNotFound
                        )
                    )
                    : Results.Ok(ApiResponse<ProductResponse>.Success(product));
            }
        );
        group.MapGet(
            "/status/{status}",
            async (
                string status,
                int? pageNumber,
                int? pageSize,
                ProductHandler handler,
                CancellationToken ct
            ) =>
                EnumParser.TryParseDefined<ProductStatus>(status, out var parsed)
                    ? Results.Ok(
                        ApiResponse<PagedResult<ProductResponse>>.Success(
                            await handler.ByStatusAsync(
                                parsed,
                                pageNumber ?? 1,
                                pageSize ?? 10,
                                ct
                            )
                        )
                    )
                    : Results.BadRequest(
                        ApiResponse<PagedResult<ProductResponse>>.Error(
                            "Geçersiz ürün durumu",
                            ErrorCodes.ProductStatusInvalid
                        )
                    )
        );
        group.MapGet(
            "/color/{color}",
            async (
                string color,
                int? pageNumber,
                int? pageSize,
                ProductHandler handler,
                CancellationToken ct
            ) =>
                EnumParser.TryParseDefined<ProductColor>(color, out var parsed)
                    ? Results.Ok(
                        ApiResponse<PagedResult<ProductResponse>>.Success(
                            await handler.ByColorAsync(
                                parsed,
                                pageNumber ?? 1,
                                pageSize ?? 10,
                                ct
                            )
                        )
                    )
                    : Results.BadRequest(
                        ApiResponse<PagedResult<ProductResponse>>.Error(
                            "Geçersiz ürün rengi",
                            ErrorCodes.ProductColorInvalid
                        )
                    )
        );
        return group;
    }
}

public sealed record CreateProductRequest(string Barcode, string? Color, string Status = "OK");

public sealed record ProductResponse(
    int Id,
    string Barcode,
    string? Color,
    string Status,
    DateTime PassedAt
);

public sealed class ProductHandler(
    ApplicationDbContext db,
    IHubContext<DashboardHub> hub,
    ILogger<ProductHandler> logger
)
{
    public Task<PagedResult<ProductResponse>> GetAllAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct
    ) => PageAsync(db.Products.AsNoTracking(), pageNumber, pageSize, ct);

    public async Task<ProductResponse?> ByIdAsync(int id, CancellationToken ct = default) =>
        (await db.Products.AsNoTracking().FirstOrDefaultAsync(product => product.Id == id, ct))
            is { } product
            ? ToResponse(product)
            : null;

    public async Task<ProductResponse?> ByBarcodeAsync(
        string barcode,
        CancellationToken ct = default
    ) =>
        (
            await db
                .Products.AsNoTracking()
                .FirstOrDefaultAsync(product => product.Barcode == barcode, ct)
        ) is { } product
            ? ToResponse(product)
            : null;

    public Task<PagedResult<ProductResponse>> DefectiveAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct
    ) =>
        PageAsync(
            db.Products.AsNoTracking().Where(product => product.Status != ProductStatus.OK),
            pageNumber,
            pageSize,
            ct
        );

    public Task<PagedResult<ProductResponse>> ByStatusAsync(
        ProductStatus status,
        int pageNumber,
        int pageSize,
        CancellationToken ct
    ) =>
        PageAsync(
            db.Products.AsNoTracking().Where(product => product.Status == status),
            pageNumber,
            pageSize,
            ct
        );

    public Task<PagedResult<ProductResponse>> ByColorAsync(
        ProductColor color,
        int pageNumber,
        int pageSize,
        CancellationToken ct
    ) =>
        PageAsync(
            db.Products.AsNoTracking().Where(product => product.Color == color),
            pageNumber,
            pageSize,
            ct
        );

    public async Task<Product> StageCreateAsync(
        CreateProductRequest request,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(request.Barcode) || request.Barcode.Length > 50)
            throw new ValidationException("Barkod boş olamaz ve en fazla 50 karakter olabilir");
        if (!EnumParser.TryParseDefined<ProductStatus>(request.Status, out var status))
            throw new ValidationException(
                "Geçersiz ürün durumu",
                ErrorCodes.ProductStatusInvalid
            );

        ProductColor? color = null;
        if (!string.IsNullOrWhiteSpace(request.Color))
        {
            if (!EnumParser.TryParseDefined<ProductColor>(request.Color, out var parsedColor))
                throw new ValidationException(
                    "Geçersiz ürün rengi",
                    ErrorCodes.ProductColorInvalid
                );
            color = parsedColor;
        }

        var product = new Product
        {
            Barcode = request.Barcode.Trim(),
            Color = color,
            Status = status,
            PassedAt = DateTime.UtcNow,
        };
        await db.Products.AddAsync(product, ct);
        return product;
    }

    public async Task<ProductResponse> CreateAsync(
        CreateProductRequest request,
        CancellationToken ct = default
    )
    {
        if (await db.Products.AnyAsync(product => product.Barcode == request.Barcode.Trim(), ct))
            throw new DuplicateResourceException(
                $"Bu barkod daha önce okutulmuş: {request.Barcode.Trim()}"
            );
        var product = await StageCreateAsync(request, ct);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(product, ct);
        return ToResponse(product);
    }

    public async Task NotifyAsync(Product product, CancellationToken ct = default)
    {
        try
        {
            await hub.Clients.All.SendAsync("ReceiveProductUpdate", ToResponse(product), ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "SignalR product notification failed for product {ProductId}.",
                product.Id
            );
        }
    }

    internal static ProductResponse ToResponse(Product product) =>
        new(
            product.Id,
            product.Barcode,
            product.Color?.ToString(),
            product.Status.ToString(),
            product.PassedAt
        );

    private static async Task<PagedResult<ProductResponse>> PageAsync(
        IQueryable<Product> query,
        int pageNumber,
        int pageSize,
        CancellationToken ct
    )
    {
        (pageNumber, pageSize) = Pagination.Normalize(pageNumber, pageSize);
        var items = await query
            .OrderByDescending(product => product.PassedAt)
            .ThenByDescending(product => product.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync(ct);
        var hasNextPage = items.Count > pageSize;
        if (hasNextPage)
            items.RemoveAt(items.Count - 1);
        return new PagedResult<ProductResponse>(
            items.Select(ToResponse).ToList(),
            hasNextPage,
            pageNumber,
            pageSize
        );
    }
}
