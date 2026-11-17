using ClockManager.Api.Application.Time;
using NodaTime;

namespace ClockManager.Api.Domain;

/// <summary>
/// One punch (Clock In or Clock Out) together with the evidence that its time is trustworthy (plans/02-schema.md §4).
/// Stored as owned columns on <c>Shifts</c> (<c>ClockIn_*</c> / <c>ClockOut_*</c>). It can only be built from a
/// <see cref="TrustedInstant"/>, so a stamp is never assembled from a local clock or from half the data.
/// </summary>
public sealed class PunchStamp
{
    public const int ProviderMaxLength = 32;
    public const int RawPayloadMaxLength = 1024;

    /// <summary>For EF Core materialization only.</summary>
    private PunchStamp()
    {
    }

    /// <summary>The exact moment of the punch. Durations are computed from this, never from local times.</summary>
    public Instant Utc { get; private set; }

    /// <summary>Zurich offset in force at <see cref="Utc"/> (+01:00 or +02:00), as seen at punch time.</summary>
    public Offset ZurichOffset { get; private set; }

    /// <summary>Zurich calendar date of <see cref="Utc"/>. For Clock In this is the shift's work date.</summary>
    public LocalDate ZurichDate { get; private set; }

    public string Provider { get; private set; } = null!;

    public int RoundTripMs { get; private set; }

    /// <summary>The second provider that agreed, or null for a single-source punch (D-009).</summary>
    public string? CrossCheckedBy { get; private set; }

    public int? CrossCheckDeltaMs { get; private set; }

    public bool LowPrecision { get; private set; }

    /// <summary>The provider's sanitized response body (D-006), at most 1024 characters.</summary>
    public string RawPayload { get; private set; } = null!;

    /// <summary>
    /// Builds a stamp from a trusted reading. The Zurich date is derived from the instant with tzdb, and the
    /// reading's offset must match tzdb at that instant (a mismatch means a bug upstream, so it throws).
    /// </summary>
    public static PunchStamp From(TrustedInstant trusted, ZurichTime zurich)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(zurich);

        var expectedOffset = zurich.OffsetAt(trusted.Utc);
        if (trusted.ZurichOffset != expectedOffset)
        {
            throw new ArgumentException(
                $"Offset {trusted.ZurichOffset} does not match tzdb ({expectedOffset}) at {trusted.Utc}.", nameof(trusted));
        }

        if (string.IsNullOrWhiteSpace(trusted.Provider) || trusted.Provider.Length > ProviderMaxLength)
            throw new ArgumentException($"Provider must be 1..{ProviderMaxLength} characters.", nameof(trusted));
        if (trusted.CrossCheckedBy is { } other && (other.Length == 0 || other.Length > ProviderMaxLength))
            throw new ArgumentException($"CrossCheckedBy must be 1..{ProviderMaxLength} characters.", nameof(trusted));
        if ((trusted.CrossCheckedBy is null) != (trusted.CrossCheckDeltaMs is null) || trusted.CrossCheckDeltaMs < 0)
            throw new ArgumentException("CrossCheckedBy and a non-negative CrossCheckDeltaMs come together or not at all.", nameof(trusted));
        if (trusted.RoundTripMs < 0)
            throw new ArgumentException("RoundTripMs must be >= 0.", nameof(trusted));
        if (trusted.RawPayload is null || trusted.RawPayload.Length > RawPayloadMaxLength)
            throw new ArgumentException($"RawPayload must be at most {RawPayloadMaxLength} characters.", nameof(trusted));

        return new PunchStamp
        {
            Utc = trusted.Utc,
            ZurichOffset = expectedOffset,
            ZurichDate = zurich.LocalDate(trusted.Utc),
            Provider = trusted.Provider,
            RoundTripMs = trusted.RoundTripMs,
            CrossCheckedBy = trusted.CrossCheckedBy,
            CrossCheckDeltaMs = trusted.CrossCheckDeltaMs,
            LowPrecision = trusted.LowPrecision,
            RawPayload = trusted.RawPayload,
        };
    }
}
