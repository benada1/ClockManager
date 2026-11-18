namespace ClockManager.Api.Infrastructure.Identity;

internal interface IAuthClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// The server clock, for the login token's issue and expiry time ONLY. A token is not a punch record, so rule 1 allows it.
/// This is the only place that does so; never use it for punches (those use <c>ITrustedClock</c>).
/// </summary>
internal sealed class AuthClock : IAuthClock
{
#pragma warning disable RS0030
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; // clock-ok: JWT issue/expiry time, not a punch record
#pragma warning restore RS0030
}
