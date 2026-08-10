using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartLine.API.Domain.Identity;
using SmartLine.API.Infrastructure.Authentication;
using SmartLine.API.Infrastructure.Persistence;
using SmartLine.API.Shared;

namespace SmartLine.API.Features.Auth;

public static class AuthFeature
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/login", LoginAsync).AllowAnonymous().RequireRateLimiting("auth");
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous().RequireRateLimiting("auth");
        auth.MapPost("/logout", LogoutAsync).RequireAuthorization(AuthPolicies.Viewer);
        auth.MapGet("/me", MeAsync).RequireAuthorization(AuthPolicies.Viewer);

        var admin = app
            .MapGroup("/api/admin/users")
            .RequireAuthorization(AuthPolicies.Admin);
        admin.MapPost("", CreateUserAsync);
        admin.MapPut("/{id:guid}/role", UpdateRoleAsync);
        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext context,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        JwtTokenService tokens,
        CancellationToken ct
    )
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            throw new ValidationException("E-posta ve parola zorunludur");

        var user = await userManager.FindByEmailAsync(request.Email.Trim());
        if (user is null || await userManager.IsLockedOutAsync(user))
            return AuthenticationFailed();
        if (!await userManager.CheckPasswordAsync(user, request.Password))
        {
            await userManager.AccessFailedAsync(user);
            return AuthenticationFailed();
        }
        await userManager.ResetAccessFailedCountAsync(user);

        var access = await tokens.CreateAccessTokenAsync(user, ct);
        var refresh = tokens.CreateRefreshToken();
        db.RefreshTokens.Add(
            new RefreshToken
            {
                UserId = user.Id,
                TokenHash = refresh.Hash,
                ExpiresAt = refresh.ExpiresAt,
            }
        );
        await db.SaveChangesAsync(ct);
        JwtTokenService.SetRefreshCookie(context.Response, refresh);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(
            ApiResponse<AuthTokenResponse>.Success(ToTokenResponse(user, access), "Giriş başarılı")
        );
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext context,
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        JwtTokenService tokens,
        CancellationToken ct
    )
    {
        if (!context.Request.Cookies.TryGetValue(JwtTokenService.RefreshCookieName, out var raw))
            return AuthenticationFailed();

        var hash = JwtTokenService.HashRefreshToken(raw);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await db
            .RefreshTokens.AsNoTracking()
            .FirstOrDefaultAsync(
                token =>
                    token.TokenHash == hash
                    && token.RevokedAt == null
                    && token.ExpiresAt > DateTime.UtcNow,
                ct
            );
        if (current is null)
        {
            var reused = await db
                .RefreshTokens.AsNoTracking()
                .FirstOrDefaultAsync(token => token.TokenHash == hash && token.RevokedAt != null, ct);
            if (reused is not null)
            {
                await db
                    .RefreshTokens.Where(token =>
                        token.UserId == reused.UserId && token.RevokedAt == null
                    )
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(x => x.RevokedAt, DateTime.UtcNow),
                        ct
                    );
                await transaction.CommitAsync(ct);
            }

            JwtTokenService.DeleteRefreshCookie(context.Response);
            return AuthenticationFailed();
        }

        var revokedAt = DateTime.UtcNow;
        var affected = await db
            .RefreshTokens.Where(token =>
                token.Id == current.Id && token.RevokedAt == null && token.ExpiresAt > revokedAt
            )
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, revokedAt), ct);
        if (affected != 1)
        {
            JwtTokenService.DeleteRefreshCookie(context.Response);
            return AuthenticationFailed();
        }

        var user = await userManager.FindByIdAsync(current.UserId.ToString());
        if (user is null)
        {
            JwtTokenService.DeleteRefreshCookie(context.Response);
            return AuthenticationFailed();
        }

        var access = await tokens.CreateAccessTokenAsync(user, ct);
        var replacement = tokens.CreateRefreshToken();
        db.RefreshTokens.Add(
            new RefreshToken
            {
                UserId = user.Id,
                TokenHash = replacement.Hash,
                ExpiresAt = replacement.ExpiresAt,
            }
        );
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        JwtTokenService.SetRefreshCookie(context.Response, replacement);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(
            ApiResponse<AuthTokenResponse>.Success(
                ToTokenResponse(user, access),
                "Oturum yenilendi"
            )
        );
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        ApplicationDbContext db,
        CancellationToken ct
    )
    {
        if (context.Request.Cookies.TryGetValue(JwtTokenService.RefreshCookieName, out var raw))
        {
            var hash = JwtTokenService.HashRefreshToken(raw);
            await db
                .RefreshTokens.Where(token => token.TokenHash == hash && token.RevokedAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(x => x.RevokedAt, DateTime.UtcNow),
                    ct
                );
        }
        JwtTokenService.DeleteRefreshCookie(context.Response);
        return Results.Ok(ApiResponse<object?>.Success(null, "Çıkış başarılı"));
    }

    private static async Task<IResult> MeAsync(
        ClaimsPrincipal principal,
        UserManager<ApplicationUser> userManager,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = id is null ? null : await userManager.FindByIdAsync(id);
        if (user is null)
            return AuthenticationFailed();
        var roles = await userManager.GetRolesAsync(user);
        return Results.Ok(ApiResponse<AuthUserResponse>.Success(ToUserResponse(user, roles)));
    }

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest request,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        var role = NormalizeRole(request.Role);
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            throw new ValidationException("E-posta ve parola zorunludur");
        if (await userManager.FindByEmailAsync(request.Email.Trim()) is not null)
            throw new DuplicateResourceException(
                "Bu e-posta adresi zaten kullanılıyor",
                ErrorCodes.DatabaseConflict
            );

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAdminRoleAsync(db, ct);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Email.Trim(),
            Email = request.Email.Trim(),
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        var created = await userManager.CreateAsync(user, request.Password);
        if (!created.Succeeded)
            throw new ValidationException(FormatIdentityErrors(created));
        var assigned = await userManager.AddToRoleAsync(user, role);
        if (!assigned.Succeeded)
            throw new ValidationException(FormatIdentityErrors(assigned));
        await transaction.CommitAsync(ct);

        return Results.Created(
            $"/api/admin/users/{user.Id}",
            ApiResponse<AuthUserResponse>.Success(
                ToUserResponse(user, [role]),
                "Kullanıcı oluşturuldu"
            )
        );
    }

    private static async Task<IResult> UpdateRoleAsync(
        Guid id,
        UpdateRoleRequest request,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        var role = NormalizeRole(request.Role);
        var user = await userManager.FindByIdAsync(id.ToString())
            ?? throw new NotFoundException("User", id);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var adminRole = await LockAdminRoleAsync(db, ct);
        var currentRoles = await userManager.GetRolesAsync(user);
        if (
            currentRoles.Contains(AuthRoles.Admin, StringComparer.Ordinal)
            && role != AuthRoles.Admin
            && await db.UserRoles.CountAsync(item => item.RoleId == adminRole.Id, ct) <= 1
        )
            throw new ApiException(
                StatusCodes.Status409Conflict,
                ErrorCodes.LastAdminRequired,
                "Sistemde en az bir Admin kullanıcısı kalmalıdır"
            );
        if (currentRoles.Count > 0)
        {
            var removed = await userManager.RemoveFromRolesAsync(user, currentRoles);
            if (!removed.Succeeded)
                throw new ValidationException(FormatIdentityErrors(removed));
        }
        var assigned = await userManager.AddToRoleAsync(user, role);
        if (!assigned.Succeeded)
            throw new ValidationException(FormatIdentityErrors(assigned));
        await userManager.UpdateSecurityStampAsync(user);
        await transaction.CommitAsync(ct);
        return Results.Ok(
            ApiResponse<AuthUserResponse>.Success(
                ToUserResponse(user, [role]),
                "Kullanıcı rolü güncellendi"
            )
        );
    }

    private static string NormalizeRole(string role) =>
        AuthRoles.UserRoles.FirstOrDefault(candidate =>
            candidate.Equals(role, StringComparison.OrdinalIgnoreCase)
        ) ?? throw new ValidationException("Rol Viewer, Operator veya Admin olmalıdır");

    private static Task<IdentityRole<Guid>> LockAdminRoleAsync(
        ApplicationDbContext db,
        CancellationToken ct
    ) =>
        db.Roles
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM "AspNetRoles"
                WHERE "NormalizedName" = {AuthRoles.Admin.ToUpperInvariant()}
                FOR UPDATE
                """
            )
            .SingleAsync(ct);

    private static IResult AuthenticationFailed() =>
        Results.Json(
            ApiResponse<object>.Error(
                "Kimlik bilgileri geçersiz veya oturum süresi dolmuş",
                ErrorCodes.AuthenticationRequired
            ),
            statusCode: StatusCodes.Status401Unauthorized
        );

    private static AuthTokenResponse ToTokenResponse(
        ApplicationUser user,
        IssuedAccessToken access
    ) => new(access.Token, access.ExpiresAt, ToUserResponse(user, access.Roles));

    private static AuthUserResponse ToUserResponse(
        ApplicationUser user,
        IEnumerable<string> roles
    ) => new(user.Id, user.Email ?? user.UserName ?? string.Empty, roles.ToArray());

    private static string FormatIdentityErrors(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(error => error.Description));
}

public sealed record LoginRequest(string Email, string Password);
public sealed record CreateUserRequest(string Email, string Password, string Role);
public sealed record UpdateRoleRequest(string Role);
public sealed record AuthUserResponse(Guid Id, string Email, IReadOnlyList<string> Roles);
public sealed record AuthTokenResponse(string AccessToken, DateTime ExpiresAt, AuthUserResponse User);
