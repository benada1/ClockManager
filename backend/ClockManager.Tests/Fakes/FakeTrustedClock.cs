using ClockManager.Api.Application.Time;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Tests.Fakes;

/// <summary>
/// A trusted clock that returns whatever instant the test sets. Use it in every test that needs "now";
/// never call a real provider in unit tests. Values come from the zurich-time-cases matrix.
/// <code>
/// var clock = new FakeTrustedClock("2026-10-25T00:30:00Z");
/// clock.Set("2026-10-25T01:15:00Z");                          // move time between punches
/// clock.FailWith(FakeTrustedClock.Unavailable());              // simulate an outage (→ 503)
/// </code>
/// </summary>
public sealed class FakeTrustedClock : ITrustedClock
{
    public const string ProviderName = "fake";

    private static readonly ZurichTime Zurich = ZurichTime.FromTzdb();
    private readonly object _gate = new();
    private Instant _now;
    private Exception? _failure;

    public FakeTrustedClock(Instant now) => _now = now;

    /// <param name="isoUtc">ISO 8601 UTC, e.g. "2026-01-12T07:00:00Z".</param>
    public FakeTrustedClock(string isoUtc) : this(ParseUtc(isoUtc)) { }

    public int Calls { get; private set; }

    public void Set(Instant now)
    {
        lock (_gate) _now = now;
    }

    public void Set(string isoUtc) => Set(ParseUtc(isoUtc));

    public void Advance(Duration by)
    {
        lock (_gate) _now += by;
    }

    /// <summary>Every following call throws <paramref name="failure"/> until <see cref="Recover"/>.</summary>
    public void FailWith(Exception failure)
    {
        lock (_gate) _failure = failure;
    }

    public void Recover()
    {
        lock (_gate) _failure = null;
    }

    public Task<TrustedInstant> GetNowAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Calls++;
            if (_failure is not null) return Task.FromException<TrustedInstant>(_failure);
            return Task.FromResult(new TrustedInstant(
                Utc: _now,
                ZurichOffset: Zurich.OffsetAt(_now),
                Provider: ProviderName,
                RoundTripMs: 0,
                RawPayload: "",
                CrossCheckedBy: null,
                CrossCheckDeltaMs: null,
                LowPrecision: false));
        }
    }

    /// <summary>The exception the real clock throws when no provider answered.</summary>
    public static TrustedTimeException Unavailable() =>
        new(Api.Common.Errors.ErrorCodes.TimeUnavailable, [new ProviderOutcome(ProviderName, "timeout")]);

    private static Instant ParseUtc(string isoUtc) => InstantPattern.ExtendedIso.Parse(isoUtc).GetValueOrThrow();
}
