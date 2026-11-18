using System.Globalization;
using System.Security.Claims;
using System.Text;
using ClockManager.Api.Application.Auth;
using ClockManager.Api.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ClockManager.Api.Infrastructure.Identity;

/// <summary>Where the token's fixed values live, so issuing and validating cannot drift apart.</summary>
internal static class JwtSettings
{
    public const string Issuer = "ClockManager";
    public const string Audience = "ClockManager.Web";
    public const string SubjectClaim = "sub";
    public const string RoleClaim = "role";

    public static SymmetricSecurityKey Key(AuthOptions options) => new(Encoding.UTF8.GetBytes(options.SigningKey!));
}

/// <summary>Issues the HS256 token: <c>sub</c> (employee id), <c>role</c>, <c>exp</c>. Time comes from <see cref="IAuthClock"/>.</summary>
internal sealed class JwtTokenIssuer(IOptions<AuthOptions> options, IAuthClock clock) : ITokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedToken Issue(int employeeId, EmployeeRole role)
    {
        var auth = options.Value;
        var issuedAt = clock.UtcNow;
        var expires = issuedAt.AddHours(auth.TokenLifetimeHours);

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtSettings.SubjectClaim, employeeId.ToString(CultureInfo.InvariantCulture)),
                new Claim(JwtSettings.RoleClaim, role.ToString()),
            ]),
            Issuer = JwtSettings.Issuer,
            Audience = JwtSettings.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(JwtSettings.Key(auth), SecurityAlgorithms.HmacSha256),
        });
        return new IssuedToken(token, expires);
    }
}
