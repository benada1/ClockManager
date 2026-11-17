using ClockManager.Api.Infrastructure.Time;

namespace ClockManager.Tests.Fakes;

/// <summary>A monotonic timer the test moves by hand, e.g. from inside a stub HTTP handler, so RTTs are exact and no test sleeps.</summary>
public sealed class FakeElapsedTimer : IElapsedTimer
{
    private long _ticks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public long GetTimestamp() => Interlocked.Read(ref _ticks);

    public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.FromTicks(Interlocked.Read(ref _ticks) - startingTimestamp);
}
