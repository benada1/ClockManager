using ClockManager.Api.Application.Auth;
using ClockManager.Api.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using ClockManager.Api.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClockManager.Api.Controllers;

/// <summary>Sign up, log in, log out and "who am I" (plans/04-auth.md §3). The token travels only in an HttpOnly cookie.</summary>
[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(AuthService auth, AuthCookie cookie) : ControllerBase
{
    /// <summary>Creates an Employee account and signs it in (the cookie is set).</summary>
    [HttpPost("signup")]
    [RequestSizeLimit(16384)] // 16 KB (QA-48)
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [Consumes("application/json")]
    [ProducesResponseType<AuthUserView>(StatusCodes.Status201Created, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<AuthUserView>> Signup([FromBody] SignupRequest request, CancellationToken ct)
    {
        var session = await auth.SignupAsync(request, ct);
        cookie.Append(Response, new IssuedToken(session.Token, session.ExpiresAt));
        return StatusCode(StatusCodes.Status201Created, session.User);
    }

    /// <summary>Checks the password and sets the cookie.</summary>
    [HttpPost("login")]
    [RequestSizeLimit(16384)] // 16 KB (QA-48)
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.AuthPolicy)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")]
    [Consumes("application/json")]
    [ProducesResponseType<AuthUserView>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    public async Task<ActionResult<AuthUserView>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var session = await auth.LoginAsync(request, ct);
        cookie.Append(Response, new IssuedToken(session.Token, session.ExpiresAt));
        return session.User;
    }

    /// <summary>Clears the cookie. Always 204, also when nobody was signed in.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Logout()
    {
        cookie.Delete(Response);
        return NoContent();
    }

    /// <summary>Who the cookie belongs to.</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<AuthUserView>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    public async Task<ActionResult<AuthUserView>> Me(CancellationToken ct) => await auth.GetCurrentAsync(ct);
}
