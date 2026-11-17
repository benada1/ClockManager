using ClockManager.Api.Application.Time;
using ClockManager.Api.Domain;
using Microsoft.Data.SqlClient;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Tests.Persistence;

/// <summary>Builders and assertions shared by the persistence tests. Instants come from the zurich-time-cases matrix.</summary>
internal static class PersistenceTestData
{
    public static readonly ZurichTime Zurich = ZurichTime.FromTzdb();

    public static Instant Utc(string iso) => InstantPattern.ExtendedIso.Parse(iso).GetValueOrThrow();

    /// <summary>A stamp exactly as Phase 3 will build it: from a trusted reading, through <see cref="PunchStamp.From"/>.</summary>
    public static PunchStamp Stamp(string isoUtc, string provider = "time.now", string? crossCheckedBy = "timeapi.io") =>
        PunchStamp.From(Trusted(Utc(isoUtc), provider, crossCheckedBy), Zurich);

    public static TrustedInstant Trusted(Instant utc, string provider = "time.now", string? crossCheckedBy = "timeapi.io") =>
        new(
            Utc: utc,
            ZurichOffset: Zurich.OffsetAt(utc),
            Provider: provider,
            RoundTripMs: 87,
            RawPayload: $$"""{"utc_datetime":"{{InstantPattern.ExtendedIso.Format(utc)}}"}""",
            CrossCheckedBy: crossCheckedBy,
            CrossCheckDeltaMs: crossCheckedBy is null ? null : 12,
            LowPrecision: false);

    public static Employee NewEmployee(string? username = null) =>
        new(username ?? "e" + Guid.NewGuid().ToString("N")[..12], "Test Employee", EmployeeRole.Employee);

    /// <summary>The <see cref="SqlException"/> behind an EF exception, with its error number and message.</summary>
    public static SqlException FindSqlException(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SqlException sql) return sql;
        }

        throw new Xunit.Sdk.XunitException($"Expected a SqlException, got: {exception}");
    }
}
