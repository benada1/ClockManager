using ClockManager.Api.Application.Time;
using ClockManager.Api.Infrastructure.Time;
using NodaTime;

namespace ClockManager.Tests.Fakes;

/// <summary>
/// An <see cref="ITimeSource"/> whose answer the test decides: a reading, a failure, an exception, or "wait for a gate /
/// for cancellation". It records how often and with which token it was called, and <see cref="Called"/> completes the
/// moment <see cref="FetchAsync"/> is entered, so a test can prove two sources were started before either finished.
/// <code>
/// var primary = FakeTimeSource.Returning(FakeTimeSource.Reading("time.now", Utc("2026-01-12T07:00:00Z")));
/// var secondary = FakeTimeSource.Returning(FakeTimeSource.Failure("timeapi.io", TimeSourceFailureReason.Timeout));
/// var gated = FakeTimeSource.Gated(reading, gate.Task);         // answers only when the test releases the gate
/// var hanging = FakeTimeSource.UntilCancelled("timeapi.io");    // ends only via the caller's token (E17)
/// </code>
/// </summary>
internal sealed class FakeTimeSource : ITimeSource
{
    private static readonly ZurichTime Zurich = ZurichTime.FromTzdb();

    private readonly Func<int, CancellationToken, Task<TimeSourceResult>> _behaviour;
    private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public FakeTimeSource(string name, Func<int, CancellationToken, Task<TimeSourceResult>> behaviour)
    {
        Name = name;
        _behaviour = behaviour;
    }

    public string Name { get; }

    /// <summary>How many times <see cref="FetchAsync"/> was called.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Completes as soon as <see cref="FetchAsync"/> is entered the first time (before it answers).</summary>
    public Task Called => _called.Task;

    /// <summary>The token passed by the caller on the last call.</summary>
    public CancellationToken ReceivedToken { get; private set; }

    public Task<TimeSourceResult> FetchAsync(CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        ReceivedToken = ct;
        _called.TrySetResult();
        return _behaviour(call, ct);
    }

    // ---------- Behaviours ----------

    public static FakeTimeSource Returning(TimeSourceResult result) =>
        new(result.Source, (_, _) => Task.FromResult(result));

    /// <summary>Answers <paramref name="results"/> in order, one per call (the last one repeats).</summary>
    public static FakeTimeSource Sequence(params TimeSourceResult[] results) =>
        new(results[0].Source, (call, _) => Task.FromResult(results[Math.Min(call, results.Length) - 1]));

    /// <summary>Answers only once <paramref name="gate"/> completes (or throws if the token is cancelled first).</summary>
    public static FakeTimeSource Gated(TimeSourceResult result, Task gate) =>
        new(result.Source, async (_, ct) =>
        {
            await gate.WaitAsync(ct);
            return result;
        });

    /// <summary>Never answers; ends with <see cref="OperationCanceledException"/> when the caller's token is cancelled.</summary>
    public static FakeTimeSource UntilCancelled(string name) =>
        new(name, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

    // ---------- Result builders ----------

    /// <param name="startedTimestamp">An <see cref="IElapsedTimer"/> timestamp (with <see cref="FakeElapsedTimer"/>: TimeSpan ticks).</param>
    /// <param name="offset">Defaults to the tzdb offset of Zurich at <paramref name="instant"/>, as a real source reports it.</param>
    public static TimeSourceReading Reading(
        string source,
        Instant instant,
        int roundTripMs = 300,
        long startedTimestamp = 0,
        Offset? offset = null,
        string? rawPayload = null) =>
        new(source, instant, offset ?? Zurich.OffsetAt(instant), roundTripMs, rawPayload ?? $"{{\"from\":\"{source}\"}}", startedTimestamp);

    public static TimeSourceFailure Failure(string source, TimeSourceFailureReason reason, int roundTripMs = 300)
    {
        int? status = reason switch
        {
            TimeSourceFailureReason.HttpError => 500,
            TimeSourceFailureReason.RateLimited => 429,
            _ => null,
        };
        return new TimeSourceFailure(source, reason, roundTripMs, $"fake {reason}", status);
    }

    public static TimeSourceFailure HttpFailure(string source, int status, int roundTripMs = 300) =>
        new(source, status == 429 ? TimeSourceFailureReason.RateLimited : TimeSourceFailureReason.HttpError,
            roundTripMs, $"HTTP {status}", status);
}
