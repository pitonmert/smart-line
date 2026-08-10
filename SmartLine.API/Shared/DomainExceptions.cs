namespace SmartLine.API.Shared;

public class ApiException(
    int statusCode,
    string code,
    string message,
    Exception? innerException = null
) : Exception(message, innerException)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed class ConveyorException(
    string message,
    string code = ErrorCodes.ConveyorMustBeStopped,
    int statusCode = StatusCodes.Status409Conflict
) : ApiException(statusCode, code, message);

public sealed class DuplicateResourceException(
    string message,
    string code = ErrorCodes.ProductBarcodeDuplicate
) : ApiException(StatusCodes.Status409Conflict, code, message);

public sealed class MqttUnavailableException(string message, Exception? innerException = null)
    : ApiException(
        StatusCodes.Status503ServiceUnavailable,
        ErrorCodes.MqttUnavailable,
        message,
        innerException
    );

public sealed class NotFoundException : ApiException
{
    public NotFoundException(string message)
        : base(StatusCodes.Status404NotFound, ErrorCodes.ResourceNotFound, message) { }

    public NotFoundException(string entityName, object key)
        : base(
            StatusCodes.Status404NotFound,
            ErrorCodes.ResourceNotFound,
            $"{entityName} bulunamadı. Id: {key}"
        )
    { }
}

public sealed class ValidationException : ApiException
{
    public IDictionary<string, string[]> Errors { get; }

    public ValidationException(string message, string code = ErrorCodes.RequestInvalid)
        : base(StatusCodes.Status400BadRequest, code, message) =>
        Errors = new Dictionary<string, string[]>();

    public ValidationException(IDictionary<string, string[]> errors)
        : base(
            StatusCodes.Status400BadRequest,
            ErrorCodes.RequestInvalid,
            "Doğrulama hatası oluştu"
        ) => Errors = errors;
}
