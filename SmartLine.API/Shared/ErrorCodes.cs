namespace SmartLine.API.Shared;

public static class ErrorCodes
{
    public const string RequestInvalid = "REQUEST_INVALID";
    public const string AuthenticationRequired = "AUTHENTICATION_REQUIRED";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string ProductBarcodeDuplicate = "PRODUCT_BARCODE_DUPLICATE";
    public const string ProductColorInvalid = "PRODUCT_COLOR_INVALID";
    public const string ProductStatusInvalid = "PRODUCT_STATUS_INVALID";
    public const string ConveyorMustBeStopped = "CONVEYOR_MUST_BE_STOPPED";
    public const string ConveyorEmergencyLatched = "CONVEYOR_EMERGENCY_LATCHED";
    public const string FireSensorActive = "FIRE_SENSOR_ACTIVE";
    public const string FireGenerationMismatch = "FIRE_GENERATION_MISMATCH";
    public const string LastAdminRequired = "LAST_ADMIN_REQUIRED";
    public const string MqttUnavailable = "MQTT_UNAVAILABLE";
    public const string DatabaseConflict = "DATABASE_CONFLICT";
    public const string UnexpectedError = "UNEXPECTED_ERROR";
}
