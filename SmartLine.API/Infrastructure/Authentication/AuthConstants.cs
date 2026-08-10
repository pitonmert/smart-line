namespace SmartLine.API.Infrastructure.Authentication;

public static class AuthRoles
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Admin = "Admin";
    public const string Device = "Device";

    public static readonly string[] UserRoles = [Viewer, Operator, Admin];
}

public static class AuthPolicies
{
    public const string Viewer = "Viewer";
    public const string Operator = "Operator";
    public const string Admin = "Admin";
    public const string Device = "Device";
}

public static class DeviceKeyDefaults
{
    public const string Scheme = "DeviceKey";
    public const string HeaderName = "X-Device-Key";
}
