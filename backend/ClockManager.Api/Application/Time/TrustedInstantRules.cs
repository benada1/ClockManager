using ClockManager.Api.Common.Errors;
using NodaTime;

namespace ClockManager.Api.Application.Time;

/// <summary>
/// The one place that makes a trusted reading safe to store (D-042): checks the plausible window, then floors the
/// instant to the 100 ns tick the database keeps (<c>datetimeoffset(7)</c>). The punch service calls it right after the
/// clock, so the guard, <c>Shift.Close</c> and the stored value all see the SAME instant.
/// </summary>
public static class TrustedInstantRules
{
    /// <summary>Plausible UTC-year range for a punch, fixed in code: rule 1 forbids comparing with the server clock.
    /// The time sources use the same window, so there is one definition.</summary>
    public const int MinYear = 2026;
    public const int MaxYearExclusive = 2100;

    public static readonly Instant WindowStart = Instant.FromUtc(MinYear, 1, 1, 0, 0);
    public static readonly Instant WindowEnd = Instant.FromUtc(MaxYearExclusive, 1, 1, 0, 0);

    /// <summary>
    /// Returns <paramref name="trusted"/> with <c>Utc</c> floored to whole ticks. An instant outside
    /// [<see cref="WindowStart"/>, <see cref="WindowEnd"/>) is refused as 503 <c>time.invalid_response</c> (fixed message,
    /// nothing from the instant in it). The offset is NOT changed: transitions fall on whole seconds, so flooring
    /// can never cross one, and <c>PunchStamp.From</c> still checks the offset against tzdb at the floored instant.
    /// </summary>
    public static TrustedInstant Normalize(TrustedInstant trusted)
    {
        ArgumentNullException.ThrowIfNull(trusted);

        if (trusted.Utc < WindowStart || trusted.Utc >= WindowEnd)
        {
            throw new TrustedTimeException(
                ErrorCodes.TimeInvalidResponse,
                [new ProviderOutcome(string.IsNullOrWhiteSpace(trusted.Provider) ? "unknown" : trusted.Provider, "out_of_range")]);
        }

        return trusted with { Utc = Instant.FromUnixTimeTicks(trusted.Utc.ToUnixTimeTicks()) };
    }
}
