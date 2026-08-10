using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SmartLine.API.Shared;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger
)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogDebug("Request was cancelled by the client.");
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted)
                throw;
            await HandleExceptionAsync(context, exception);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, message, code) = Map(exception);
        if (statusCode >= StatusCodes.Status500InternalServerError)
            logger.LogError(
                exception,
                "Server error: {ExceptionType}, StatusCode: {StatusCode}, Code: {Code}",
                exception.GetType().Name,
                statusCode,
                code
            );
        else
            logger.LogWarning(
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
    }

    private static (int StatusCode, string Message, string Code) Map(Exception exception)
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
