using System.ComponentModel.DataAnnotations;

namespace SmartLine.API.Infrastructure.Authentication;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    [Required]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 60)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 30)]
    public int RefreshTokenDays { get; set; } = 7;
}

public sealed class DeviceKeyOptions
{
    public const string SectionName = "DeviceAuthentication";

    [Required]
    public string Key { get; set; } = string.Empty;
}

public sealed class BootstrapAdminOptions
{
    public const string SectionName = "BootstrapAdmin";
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
