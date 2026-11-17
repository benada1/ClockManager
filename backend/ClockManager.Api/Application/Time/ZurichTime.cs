using NodaTime;

namespace ClockManager.Api.Application.Time;

/// <summary>
/// The Europe/Zurich zone from NodaTime's bundled tzdb, resolved once and registered as a singleton (D-007).
/// Use it to turn a UTC <see cref="Instant"/> into Zurich wall-clock time for display and day grouping.
/// Durations are never computed here: subtract instants instead.
/// </summary>
public sealed class ZurichTime
{
    public const string ZoneId = "Europe/Zurich";

    public ZurichTime(IDateTimeZoneProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Zone = provider[ZoneId]; // throws DateTimeZoneNotFoundException at startup if tzdb lacks it
    }

    /// <summary>Uses NodaTime's embedded tzdb, so Windows and Linux give identical results.</summary>
    public static ZurichTime FromTzdb() => new(DateTimeZoneProviders.Tzdb);

    public DateTimeZone Zone { get; }

    /// <summary>The Zurich UTC offset in force at <paramref name="instant"/> (+01:00 or +02:00).</summary>
    public Offset OffsetAt(Instant instant) => Zone.GetUtcOffset(instant);

    /// <summary>The instant as Zurich wall-clock time, including the offset (so the repeated October hour is unambiguous).</summary>
    public ZonedDateTime ToLocal(Instant instant) => instant.InZone(Zone);

    /// <summary>The Zurich calendar date of the instant (the day a shift belongs to, by its Clock In).</summary>
    public LocalDate LocalDate(Instant instant) => ToLocal(instant).Date;
}
