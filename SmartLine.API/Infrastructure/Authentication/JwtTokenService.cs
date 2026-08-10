using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SmartLine.API.Domain.Identity;

namespace SmartLine.API.Infrastructure.Authentication;

public sealed record IssuedAccessToken(
    string Token,
    DateTime ExpiresAt,
    IReadOnlyList<string> Roles
);

public sealed record IssuedRefreshToken(string RawToken, string Hash, DateTime ExpiresAt);

public sealed class JwtTokenService(
    UserManager<ApplicationUser> userManager,
    IOptions<JwtOptions> options
)
{
    public const string RefreshCookieName = "smartline.refresh";
    private readonly JwtOptions _options = options.Value;

    public async Task<IssuedAccessToken> CreateAccessTokenAsync(
        ApplicationUser user,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        var roles = await userManager.GetRolesAsync(user);
        var expiresAt = DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Email ?? user.UserName ?? user.Id.ToString()),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256
        );
        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt,
            signingCredentials: credentials
        );
        return new IssuedAccessToken(
            new JwtSecurityTokenHandler().WriteToken(token),
            expiresAt,
            roles.ToArray()
        );
    }

    public IssuedRefreshToken CreateRefreshToken()
    {
        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(64));
        return new IssuedRefreshToken(
            rawToken,
            HashRefreshToken(rawToken),
            DateTime.UtcNow.AddDays(_options.RefreshTokenDays)
        );
    }

    public static string HashRefreshToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public static void SetRefreshCookie(HttpResponse response, IssuedRefreshToken token) =>
        response.Cookies.Append(
            RefreshCookieName,
            token.RawToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Expires = token.ExpiresAt,
                Path = "/api/auth",
                IsEssential = true,
            }
        );

    public static void DeleteRefreshCookie(HttpResponse response) =>
        response.Cookies.Delete(
            RefreshCookieName,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/api/auth",
                IsEssential = true,
            }
        );
}
