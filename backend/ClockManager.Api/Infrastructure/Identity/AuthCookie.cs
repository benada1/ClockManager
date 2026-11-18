using ClockManager.Api.Application.Auth;
using Microsoft.Extensions.Options;

namespace ClockManager.Api.Infrastructure.Identity;

/// <summary>The login cookie: HttpOnly, SameSite=Lax, path /, Secure when the request is HTTPS.</summary>
public sealed class AuthCookie(IOptions<AuthOptions> options)
{
    public void Append(HttpResponse response, IssuedToken token) =>
        response.Cookies.Append(options.Value.CookieName, token.Value, Build(response.HttpContext.Request, token.ExpiresAt));

    public void Delete(HttpResponse response) =>
        response.Cookies.Delete(options.Value.CookieName, Build(response.HttpContext.Request, null));

    private static CookieOptions Build(HttpRequest request, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Secure = request.IsHttps, // plain http://localhost in development must still work
        IsEssential = true,
        Expires = expires,
    };
}
