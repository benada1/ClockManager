using System.Diagnostics;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// Measures elapsed time with a monotonic counter (never the wall clock), e.g. a provider's round trip.
/// Its values say nothing about what time it is, so they can never become a punch time.
/// Exists so tests can control "how long the request took" without sleeping.
/// </summary>
public interface IElapsedTimer
{
    /// <summary>An opaque monotonic timestamp; only meaningful when passed back to <see cref="GetElapsedTime"/>.</summary>
    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp);
}

/// <summary>Production timer backed by <see cref="Stopwatch"/> (high-resolution, monotonic).</summary>
public sealed class StopwatchElapsedTimer : IElapsedTimer
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) => Stopwatch.GetElapsedTime(startingTimestamp);
}
