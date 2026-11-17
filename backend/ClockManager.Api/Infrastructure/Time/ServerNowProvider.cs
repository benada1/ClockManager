using ClockManager.Api.Application.Time;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// A short cache of the trusted time for the status endpoint's display and flags (D-035). Singleton.
/// <para>
/// <b>This must never be used for a punch</b> (CLAUDE.md rule 1, master plan D1). That is enforced by shape, not just by
/// this comment: it does not implement <see cref="ITrustedClock"/>, it returns a <see cref="ServerNowReading"/> (not a
/// <see cref="TrustedInstant"/>, which is what <c>PunchStamp.From</c> requires), and <c>PunchService</c> never receives
/// it. Punches call <see cref="ITrustedClock"/> afresh every time.
/// </para>
/// <list type="bullet">
/// <item>A success is served for <c>ttl</c> (default 10 s). Its age comes from the monotonic <see cref="IElapsedTimer"/>
/// (never a wall clock) and the fetched instant is returned unchanged: the client extrapolates with its own elapsed counter.</item>
/// <item>A failure is cached for 1 s only (<see cref="FailureTtl"/>): long enough that a polling UI doesn't hit two
/// free providers on every request while they are down, short enough that recovery shows almost at once.</item>
/// <item>At most one fetch is in flight: concurrent callers share it (no stampede on the free providers).</item>
/// <item>The fetch runs on the app's stopping token, not on one caller's token, so a disconnecting caller can't cancel
/// the answer the others wait for. Each provider call has its own timeout.</item>
/// <item>A provider problem returns null; it never throws and is never turned into a 5xx.</item>
/// </list>
/// </summary>
internal sealed class ServerNowProvider : IServerNowProvider
{
    /// <summary>How long "no trusted time" is remembered. A monotonic duration, not a time of day.</summary>
    internal static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(1);

    private readonly Func<CancellationToken, Task<TrustedInstant>> _fetch;
    private readonly IElapsedTimer _timer;
    private readonly TimeSpan _ttl;
    private readonly CancellationToken _stopping;
    private readonly ILogger<ServerNowProvider> _logger;
    private readonly object _gate = new();

    private Task<Fetched>? _inflight;
    private Fetched? _cached;

    /// <param name="fetch">One fresh trusted-clock reading (<see cref="ITrustedClock.GetNowAsync"/> in its own scope).</param>
    /// <param name="timer">Monotonic timer for the cache TTL and the age.</param>
    /// <param name="ttl">How long a success is served.</param>
    /// <param name="stopping">Cancels a fetch when the app shuts down.</param>
    public ServerNowProvider(
        Func<CancellationToken, Task<TrustedInstant>> fetch,
        IElapsedTimer timer,
        TimeSpan ttl,
        CancellationToken stopping,
        ILogger<ServerNowProvider> logger)
    {
        _fetch = fetch;
        _timer = timer;
        _ttl = ttl;
        _stopping = stopping;
        _logger = logger;
    }

    /// <param name="Value">The fetched reading; null = the attempt failed.</param>
    /// <param name="At">Timer timestamp of when the answer was known.</param>
    private sealed record Fetched(TrustedInstant? Value, long At);

    public async Task<ServerNowReading?> GetAsync(CancellationToken ct)
    {
        TaskCompletionSource<Fetched>? refresh = null;
        Task<Fetched> pending;
        lock (_gate)
        {
            if (_cached is not null && _timer.GetElapsedTime(_cached.At) < (_cached.Value is null ? FailureTtl : _ttl))
            {
                pending = Task.FromResult(_cached);
            }
            else
            {
                if (_inflight is null)
                {
                    refresh = new TaskCompletionSource<Fetched>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _inflight = refresh.Task;
                }
                pending = _inflight;
            }
        }

        // Started outside the lock: a fetch that completes synchronously must not run while we hold it.
        if (refresh is not null) _ = RefreshAsync(refresh);

        // The caller's token only stops this caller waiting; the shared fetch carries on.
        var fetched = await pending.WaitAsync(ct);
        return fetched.Value is null
            ? null
            : new ServerNowReading(fetched.Value.Utc, fetched.Value.ZurichOffset, _timer.GetElapsedTime(fetched.At));
    }

    private async Task RefreshAsync(TaskCompletionSource<Fetched> refresh)
    {
        Fetched result;
        try
        {
            var trusted = await _fetch(_stopping);
            result = new Fetched(trusted, _timer.GetTimestamp()); // age counts from when the answer was known
        }
        catch (OperationCanceledException oce) when (_stopping.IsCancellationRequested)
        {
            lock (_gate) _inflight = null; // shutting down: nothing to cache
            refresh.SetCanceled(oce.CancellationToken);
            return;
        }
        catch (Exception ex)
        {
            // No trusted time: the status is still answered, without a server time (P20). Only codes are logged.
            if (ex is TrustedTimeException tte)
            {
                _logger.LogWarning(
                    "Server time unavailable for the status endpoint: {Code} ({Providers}).",
                    tte.Code, string.Join(", ", tte.Providers.Select(p => $"{p.Name}={p.Outcome}")));
            }
            else
            {
                _logger.LogError(ex, "Unexpected error while fetching the server time for the status endpoint.");
            }
            result = new Fetched(null, _timer.GetTimestamp());
        }

        lock (_gate)
        {
            _cached = result;
            _inflight = null;
        }
        refresh.SetResult(result);
    }
}
