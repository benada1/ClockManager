using ClockManager.Api.Application.Shifts;

namespace ClockManager.Api.Application.Status;

/// <summary>Who is asking, as the status endpoint shows it.</summary>
public sealed record StatusEmployee(string Username, string DisplayName);

/// <summary>
/// The server's trusted time for DISPLAY only, never for a record (D-035). <see cref="Utc"/> is the instant as it was
/// fetched, not moved forward: add <see cref="AgeMs"/> (or the client's own elapsed counter since the response arrived) to
/// estimate "now".
/// </summary>
/// <param name="Utc">ISO 8601 UTC, e.g. <c>2026-11-17T07:00:00.123Z</c>.</param>
/// <param name="Zurich">The same instant at the Zurich offset, <c>+HH:mm</c> like the punch view.</param>
/// <param name="Source">Always <see cref="StatusService.TrustedClockSource"/>: the trusted clock, possibly cached.</param>
/// <param name="AgeMs">Milliseconds since <see cref="Utc"/> was fetched from the providers (0 for a fresh fetch, up to the cache TTL).</param>
public sealed record ServerNowView(string Utc, string Zurich, string Source, long AgeMs);

/// <summary>Response of <c>GET /api/me/status</c> (plans/03-punch-api.md §4).</summary>
/// <param name="OpenShift">The employee's open shift (same shape as a punch response), or null.</param>
/// <param name="LastClosedShift">The employee's closed shift with the latest clock-out, or null.</param>
/// <param name="ServerNow">The cached trusted time, or null when no trusted time is available.</param>
/// <param name="Flags">Top-level flags of the current state; <c>forgotten_clock_out</c> only. Empty when no trusted time is available (never guessed).</param>
public sealed record StatusView(
    StatusEmployee Employee,
    ShiftView? OpenShift,
    ShiftView? LastClosedShift,
    ServerNowView? ServerNow,
    IReadOnlyList<string> Flags);
