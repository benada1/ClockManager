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
/// clock.Hold(); ...; await clock.WaitForCallsAsync(10); clock.Release();   // park callers inside the "provider call"
/// </code>
/// </summary>
public sealed class FakeTrustedClock : ITrustedClock
{
    public const string ProviderName = "fake";

    private static readonly ZurichTime Zurich = ZurichTime.FromTzdb();
    private readonly object _gate = new();
    private Instant _now;
    private Exception? _failure;
    private TaskCompletionSource? _hold;
    private int _calls;

    public FakeTrustedClock(Instant now) => _now = now;

    /// <param name="isoUtc">ISO 8601 UTC, e.g. "2026-01-12T07:00:00Z".</param>
    public FakeTrustedClock(string isoUtc) : this(ParseUtc(isoUtc)) { }

    /// <summary>How many times <see cref="GetNowAsync"/> was entered (held callers included).</summary>
    public int Calls => Volatile.Read(ref _calls);

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

    /// <summary>
    /// Following calls wait (like a slow provider) until <see cref="Release"/> or until their token is cancelled.
    /// Lets a test put N requests past the state check before any of them saves (a real race), or disconnect a
    /// client while its punch waits for the provider.
    /// </summary>
    public void Hold()
    {
        lock (_gate) _hold ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Lets every held call continue; they then read the current instant / failure.</summary>
    public void Release()
    {
        TaskCompletionSource? hold;
        lock (_gate)
        {
            hold = _hold;
            _hold = null;
        }
        hold?.TrySetResult();
    }

    /// <summary>Waits until at least <paramref name="count"/> calls have entered the clock.</summary>
    public async Task WaitForCallsAsync(int count, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        while (Calls < count)
        {
            await Task.Delay(10, cts.Token);
        }
    }

    public async Task<TrustedInstant> GetNowAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);

        Task? hold;
        lock (_gate) hold = _hold?.Task;
        if (hold is not null) await hold.WaitAsync(ct);

        lock (_gate)
        {
            if (_failure is not null) throw _failure;
            return new TrustedInstant(
                Utc: _now,
                ZurichOffset: Zurich.OffsetAt(_now),
                Provider: ProviderName,
                RoundTripMs: 0,
                RawPayload: "",
                CrossCheckedBy: null,
                CrossCheckDeltaMs: null,
                LowPrecision: false);
        }
    }

    /// <summary>The exception the real clock throws when no provider answered.</summary>
    public static TrustedTimeException Unavailable() =>
        new(Api.Common.Errors.ErrorCodes.TimeUnavailable, [new ProviderOutcome(ProviderName, "timeout")]);

    private static Instant ParseUtc(string isoUtc) => InstantPattern.ExtendedIso.Parse(isoUtc).GetValueOrThrow();
}
