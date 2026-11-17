using ClockManager.Api.Application.Time;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// <see cref="ITimeHealth"/> with a 10 s cache (plans/01-trusted-clock.md §4.6, D-012). Singleton.
/// <list type="bullet">
/// <item>Freshness is measured on the monotonic <see cref="IElapsedTimer"/>, not on a wall clock, so no local
/// time-of-day is read at all and the cached value can never be mistaken for a time.</item>
/// <item>At most one refresh is in flight: concurrent callers during a refresh share it (no stampede).</item>
/// <item>The refresh runs on the app's stopping token, not on one caller's token, so a caller that disconnects
/// can't cancel the answer the others are waiting for. Each provider call is still bounded by its own timeout.</item>
/// <item>Only the report is cached. <see cref="ITrustedClock.GetNowAsync"/> (the punch path) never comes here.</item>
/// </list>
/// </summary>
internal sealed class TimeHealthCheck : ITimeHealth
{
    /// <summary>How long a report is served before the providers are asked again.</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private readonly Func<CancellationToken, Task<TrustedClockRun>> _run;
    private readonly IElapsedTimer _timer;
    private readonly CancellationToken _stopping;
    private readonly object _gate = new();

    private Task<TimeHealthReport>? _inflight;
    private TimeHealthReport? _cached;
    private long _cachedAt; // IElapsedTimer timestamp of when _cached was produced

    /// <param name="run">One full trusted-clock attempt (<see cref="TrustedClock.RunAsync"/>).</param>
    /// <param name="timer">Monotonic timer for the cache TTL.</param>
    /// <param name="stopping">Cancels a refresh when the app shuts down.</param>
    public TimeHealthCheck(Func<CancellationToken, Task<TrustedClockRun>> run, IElapsedTimer timer, CancellationToken stopping)
    {
        _run = run;
        _timer = timer;
        _stopping = stopping;
    }

    public Task<TimeHealthReport> GetAsync(CancellationToken ct)
    {
        TaskCompletionSource<TimeHealthReport>? refresh = null;
        Task<TimeHealthReport> pending;
        lock (_gate)
        {
            if (_cached is not null && _timer.GetElapsedTime(_cachedAt) < CacheTtl)
            {
                return Task.FromResult(_cached);
            }

            if (_inflight is null)
            {
                refresh = new TaskCompletionSource<TimeHealthReport>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inflight = refresh.Task;
            }
            pending = _inflight;
        }

        // Started outside the lock: a source that completes synchronously must not run while we hold it.
        if (refresh is not null)
        {
            _ = RefreshAsync(refresh);
        }

        // The caller's token only stops this caller waiting; the shared refresh carries on.
        return pending.WaitAsync(ct);
    }

    private async Task RefreshAsync(TaskCompletionSource<TimeHealthReport> refresh)
    {
        try
        {
            var report = ToReport(await _run(_stopping));
            lock (_gate)
            {
                _cached = report;
                _cachedAt = _timer.GetTimestamp(); // TTL counts from when the answer was known
                _inflight = null;
            }
            refresh.SetResult(report);
        }
        catch (Exception ex)
        {
            // Not cached: the next caller tries again. Provider problems never land here (RunAsync reports them in
            // the run); this is shutdown or a wiring bug.
            lock (_gate)
            {
                _inflight = null;
            }
            if (ex is OperationCanceledException oce) refresh.SetCanceled(oce.CancellationToken);
            else refresh.SetException(ex);
        }
    }

    /// <summary>Maps a run to the public report: outcome codes, round trips and the decision code only.</summary>
    internal static TimeHealthReport ToReport(TrustedClockRun run)
    {
        var providers = new[] { run.Primary, run.Secondary }
            .Select(p => new ProviderHealth(p.Source, p.OutcomeCode, p.RoundTripMs))
            .ToList();

        var crossCheck = run.CrossCheck switch
        {
            null => TimeHealthReport.NotChecked,
            { Agrees: true } => TimeHealthReport.Agree,
            _ => TimeHealthReport.Disagree,
        };

        return new TimeHealthReport(
            CanPunch: run.Accepted is not null,
            providers,
            crossCheck,
            run.CrossCheck?.DeltaMs,
            run.Failure?.Code);
    }
}
