using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartLine.API.Shared;

namespace SmartLine.API.Infrastructure.Hosting;

public static class HttpPipeline
{
    public static void UseApiPipeline(this WebApplication app)
    {
        app.Use(
            async (context, next) =>
            {
                // Exception handling clears response headers; apply these when the response starts.
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers["X-Frame-Options"] = "DENY";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    context.Response.Headers["Strict-Transport-Security"] =
                        "max-age=31536000; includeSubDomains";
                    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                    return Task.CompletedTask;
                });
                await next();
            }
        );
        app.UseExceptionHandler(
            new ExceptionHandlerOptions
            {
                AllowStatusCode404Response = true,
                // Expected client errors are warnings; the framework logs unexpected server errors.
                SuppressDiagnosticsCallback = context =>
                    MapException(context.Exception).StatusCode < 500,
                ExceptionHandler = async context =>
                {
                    var exception = context.Features.Get<IExceptionHandlerFeature>()!.Error;
                    var (statusCode, message, code) = MapException(exception);
                    if (statusCode < 500)
                        app.Logger.LogWarning(
                            "Request failed: {ExceptionType}, StatusCode: {StatusCode}, Code: {Code}, Message: {Message}",
                            exception.GetType().Name,
                            statusCode,
                            code,
                            exception.Message
                        );

                    context.Response.StatusCode = statusCode;
                    await context.Response.WriteAsJsonAsync(
                        ApiResponse<object>.Error(message, code),
                        context.RequestAborted
                    );
                },
            }
        );
        app.UseStatusCodePages(async statusContext =>
        {
            var response = statusContext.HttpContext.Response;
            if (response.HasStarted || response.ContentLength is > 0)
                return;

            var (message, code) = response.StatusCode switch
            {
                StatusCodes.Status404NotFound => ("Kaynak bulunamadı", ErrorCodes.ResourceNotFound),
                _ => ("İstek işlenemedi", ErrorCodes.RequestInvalid),
            };
            await response.WriteAsJsonAsync(
                ApiResponse<object>.Error(message, code),
                statusContext.HttpContext.RequestAborted
            );
        });
        // Serve Dashboard assets before endpoint routing.
        app.UseStaticFiles();
        app.UseRouting();
    }

    private static (int StatusCode, string Message, string Code) MapException(Exception exception)
    {
        if (exception is ApiException apiException)
            return (apiException.StatusCode, apiException.Message, apiException.Code);

        if (exception is BadHttpRequestException or JsonException)
            return (
                StatusCodes.Status400BadRequest,
                "İstek gövdesi veya parametreleri geçersiz",
                ErrorCodes.RequestInvalid
            );

        if (exception is DbUpdateConcurrencyException)
            return (
                StatusCodes.Status409Conflict,
                "Kayıt başka bir işlem tarafından değiştirildi",
                ErrorCodes.DatabaseConflict
            );

        if (exception is DbUpdateException updateException)
        {
            var postgres = FindPostgresException(updateException);
            if (
                postgres is { SqlState: PostgresErrorCodes.UniqueViolation }
                && string.Equals(
                    postgres.ConstraintName,
                    "IX_Products_Barcode",
                    StringComparison.Ordinal
                )
            )
                return (
                    StatusCodes.Status409Conflict,
                    "Bu barkod daha önce okutulmuş",
                    ErrorCodes.ProductBarcodeDuplicate
                );

            return (
                StatusCodes.Status409Conflict,
                "Veritabanı kısıtlaması ihlal edildi",
                ErrorCodes.DatabaseConflict
            );
        }

        return (
            StatusCodes.Status500InternalServerError,
            "Beklenmeyen bir hata oluştu",
            ErrorCodes.UnexpectedError
        );
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is PostgresException postgres)
                return postgres;
            if (current.InnerException is null)
                break;
        }
        return null;
    }
}
