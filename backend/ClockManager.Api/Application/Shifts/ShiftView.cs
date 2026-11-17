using ClockManager.Api.Domain;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Api.Application.Shifts;

/// <summary>One punch as the API returns it (plans/03-punch-api.md §4).</summary>
/// <param name="Utc">The instant, ISO 8601 UTC, e.g. <c>2026-11-17T07:00:00.123Z</c>.</param>
/// <param name="Zurich">The same instant at the Zurich offset seen at punch time, e.g. <c>2026-11-17T08:00:00.123+01:00</c>.
/// The offset keeps the two "02:30" of the repeated October hour apart.</param>
public sealed record PunchView(string Utc, string Zurich, string Provider, string? CrossCheckedBy, bool LowPrecision);

/// <summary>A shift as the API returns it (plans/03-punch-api.md §4).</summary>
/// <param name="WorkDate">The Zurich date of Clock In (<c>yyyy-MM-dd</c>): an overnight shift belongs to the day it started.</param>
/// <param name="DurationSeconds">Whole seconds between the two UTC instants (truncated), null while open. Never from wall-clock times.</param>
/// <param name="Flags">Computed on every read, never stored (D-031): <see cref="ShiftFlags"/>.</param>
public sealed record ShiftView(
    long ShiftId,
    string WorkDate,
    PunchView ClockIn,
    PunchView? ClockOut,
    long? DurationSeconds,
    IReadOnlyList<string> Flags);

/// <summary>Flag names returned in <see cref="ShiftView.Flags"/>. Stable: clients rely on them.</summary>
public static class ShiftFlags
{
    /// <summary>A closed shift longer than <see cref="ShiftRulesOptions.MaxOpenShiftHours"/> (strictly).</summary>
    public const string LongShift = "long_shift";

    /// <summary>A closed shift shorter than <see cref="ShiftRulesOptions.ShortShiftSeconds"/> (strictly): maybe a double punch.</summary>
    public const string ShortShift = "short_shift";

    /// <summary>
    /// An OPEN shift older than <see cref="ShiftRulesOptions.MaxOpenShiftHours"/> (strictly), measured against the trusted
    /// server time. Reported only by the status endpoint (top-level <c>flags</c>), and only when that time is available (D-036).
    /// </summary>
    public const string ForgottenClockOut = "forgotten_clock_out";

    /// <summary>True when the open shift started more than <c>MaxOpenShiftHours</c> before <paramref name="now"/> (strict, like <see cref="LongShift"/>).</summary>
    public static bool IsForgotten(Shift openShift, Instant now, ShiftRulesOptions rules)
    {
        ArgumentNullException.ThrowIfNull(openShift);
        ArgumentNullException.ThrowIfNull(rules);
        return openShift.ClockOut is null && now - openShift.ClockIn.Utc > Duration.FromHours(rules.MaxOpenShiftHours);
    }

    /// <summary>
    /// Flags of a closed shift, from the exact UTC duration. An open shift gets none here: whether it is a forgotten
    /// clock-out depends on "now", which only the status endpoint has (<see cref="IsForgotten"/>).
    /// </summary>
    public static IReadOnlyList<string> For(Shift shift, ShiftRulesOptions rules)
    {
        ArgumentNullException.ThrowIfNull(shift);
        ArgumentNullException.ThrowIfNull(rules);
        if (shift.ClockOut is null) return [];

        var duration = shift.ClockOut.Utc - shift.ClockIn.Utc;
        var flags = new List<string>(1);
        if (duration > Duration.FromHours(rules.MaxOpenShiftHours)) flags.Add(LongShift);
        if (duration < Duration.FromSeconds(rules.ShortShiftSeconds)) flags.Add(ShortShift);
        return flags;
    }
}

public static class ShiftViewMapper
{
    /// <summary>
    /// ISO 8601 with the offset always as <c>+HH:mm</c>. NodaTime's <c>OffsetDateTimePattern.ExtendedIso</c> writes
    /// <c>+01</c> when the minutes are zero, which many clients (and RFC 3339) don't accept.
    /// </summary>
    private static readonly OffsetDateTimePattern ZurichPattern =
        OffsetDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm':'ss;FFFFFFFFFo<+HH:mm>");

    /// <summary>An instant as Zurich local time with its offset as <c>+HH:mm</c>, e.g. <c>2026-11-17T08:00:00.123+01:00</c>.</summary>
    public static string FormatZurich(Instant utc, Offset zurichOffset) => ZurichPattern.Format(utc.WithOffset(zurichOffset));

    /// <summary>An instant as ISO 8601 UTC, e.g. <c>2026-11-17T07:00:00.123Z</c>.</summary>
    public static string FormatUtc(Instant utc) => InstantPattern.ExtendedIso.Format(utc);

    public static ShiftView ToView(Shift shift, ShiftRulesOptions rules)
    {
        ArgumentNullException.ThrowIfNull(shift);
        long? durationSeconds = shift.ClockOut is null
            ? null
            : (long)(shift.ClockOut.Utc - shift.ClockIn.Utc).TotalSeconds; // positive (DB CHECK), so the cast truncates
        return new ShiftView(
            shift.Id,
            LocalDatePattern.Iso.Format(shift.ClockIn.ZurichDate),
            ToView(shift.ClockIn),
            shift.ClockOut is null ? null : ToView(shift.ClockOut),
            durationSeconds,
            ShiftFlags.For(shift, rules));
    }

    private static PunchView ToView(PunchStamp stamp) => new(
        InstantPattern.ExtendedIso.Format(stamp.Utc),
        ZurichPattern.Format(stamp.Utc.WithOffset(stamp.ZurichOffset)),
        stamp.Provider,
        stamp.CrossCheckedBy,
        stamp.LowPrecision);
}
