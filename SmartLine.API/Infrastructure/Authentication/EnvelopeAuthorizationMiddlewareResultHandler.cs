using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using SmartLine.API.Shared;

namespace SmartLine.API.Infrastructure.Authentication;

public sealed class EnvelopeAuthorizationMiddlewareResultHandler
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult
    )
    {
        if (authorizeResult.Challenged)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error(
                    "Kimlik doğrulaması gerekli",
                    ErrorCodes.AuthenticationRequired
                ),
                context.RequestAborted
            );
            return;
        }

        if (authorizeResult.Forbidden)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Bu işlem için yetkiniz yok", ErrorCodes.AccessDenied),
                context.RequestAborted
            );
            return;
        }

        await _fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
