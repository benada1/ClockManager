namespace ClockManager.Api.Application.Time;

/// <summary>
/// The only way the application learns "what time is it now" for a punch (CLAUDE.md rule 1).
/// Backed by external Europe/Zurich time providers; tests replace it with a fake.
/// </summary>
public interface ITrustedClock
{
    /// Throws TrustedTimeException (→ 503) if no trustworthy time is available. Never returns a guess.
    Task<TrustedInstant> GetNowAsync(CancellationToken ct);
}
