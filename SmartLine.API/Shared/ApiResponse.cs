using System.Text.Json.Serialization;

namespace SmartLine.API.Shared;

public class ApiResponse<T>
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; } = string.Empty;
    public T? Data { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }

    public static ApiResponse<T> Success(T data, string message = "İşlem başarılı") =>
        new()
        {
            IsSuccess = true,
            Message = message,
            Data = data,
        };

    public static ApiResponse<T> Error(string message, string code = ErrorCodes.RequestInvalid) =>
        new()
        {
            IsSuccess = false,
            Message = message,
            Code = code,
        };
}
