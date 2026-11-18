using System.Security.Claims;
using ClockManager.Api.Infrastructure.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ClockManager.Tests.Infrastructure;

/// <summary>Hand-made login tokens with fixed dates, for the expired / wrong-key cases. Dates are literals, never the clock.</summary>
internal static class TestTokens
{
    public static readonly DateTime Past = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime Future = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static string Create(AuthOptions options, int employeeId, string role, DateTime notBefore, DateTime expires, string? signingKey = null)
    {
        var key = signingKey is null ? JwtSettings.Key(options) : new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(signingKey));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", employeeId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new Claim("role", role),
            ]),
            Issuer = JwtSettings.Issuer,
            Audience = JwtSettings.Audience,
            NotBefore = notBefore,
            Expires = expires,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }
}
