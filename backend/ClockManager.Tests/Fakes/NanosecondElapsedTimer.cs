using ClockManager.Api.Infrastructure.Time;

namespace ClockManager.Tests.Fakes;

/// <summary>
/// Like <see cref="FakeElapsedTimer"/>, but its timestamps count <b>nanoseconds</b>, the way <c>Stopwatch</c> does on
/// Linux (<c>Stopwatch.Frequency</c> = 1e9). On Windows <c>Stopwatch.Frequency</c> is usually 1e7, the same unit as
/// <see cref="TimeSpan"/> ticks, so code that does <c>TimeSpan.FromTicks(b - a)</c> on two timestamps looks right on
/// a Windows dev machine and is 100× wrong in a Linux container. Timestamps are opaque: the only correct way to turn
/// them into time is through <see cref="IElapsedTimer.GetElapsedTime"/>.
/// </summary>
public sealed class NanosecondElapsedTimer : IElapsedTimer
{
    private const long NanosecondsPerTick = 100;
    private long _nanoseconds;

    /// <summary>The timestamp this timer would return after <paramref name="sinceZero"/> has elapsed from zero.</summary>
    public static long TimestampAt(TimeSpan sinceZero) => sinceZero.Ticks * NanosecondsPerTick;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _nanoseconds, by.Ticks * NanosecondsPerTick);

    public long GetTimestamp() => Interlocked.Read(ref _nanoseconds);

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        TimeSpan.FromTicks((Interlocked.Read(ref _nanoseconds) - startingTimestamp) / NanosecondsPerTick);
}
