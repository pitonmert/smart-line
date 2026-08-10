using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SmartLine.API.Infrastructure.Authentication;

public sealed class DeviceKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<DeviceKeyOptions> deviceOptions
) : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(DeviceKeyDefaults.HeaderName, out var suppliedValues))
            return Task.FromResult(AuthenticateResult.NoResult());

        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedValues.ToString()));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(deviceOptions.Value.Key));
        if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
            return Task.FromResult(AuthenticateResult.Fail("Device key is invalid."));

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "smartline-device"),
                new Claim(ClaimTypes.Name, "smartline-device"),
                new Claim(ClaimTypes.Role, AuthRoles.Device),
            ],
            DeviceKeyDefaults.Scheme
        );
        return Task.FromResult(
            AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), DeviceKeyDefaults.Scheme)
            )
        );
    }
}
