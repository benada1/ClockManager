using ClockManager.Api.Application.Time;
using ClockManager.Api.Domain;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Api.Application.Manager;

/// <summary>A correction request after parsing; every value is valid on its own.</summary>
public sealed record ParsedCorrection(ShiftCorrectionField Field, Instant NewUtc, string Reason, byte[] RowVersion);

/// <summary>
/// Strict, pure parsing of <see cref="CorrectionRequest"/> (no I/O). Messages are fixed and never echo the input (like D-065).
/// The typed offset must be the Zurich offset at that instant: this picks the right repeated October hour and rejects the spring gap.
/// </summary>
public static class CorrectionInput
{
    public const string FieldMessage = "must be \"clockIn\" or \"clockOut\"";
    public const string NewTimeMessage = "must be a date and time with its offset, e.g. 2026-11-17T17:12:00+01:00";
    public const string OffsetMessage = "the offset is not the Europe/Zurich offset at that time (check clock-change days)";
    public const string ReasonMessage = "must be 5 to 500 characters";
    public const string RowVersionMessage = "must be the rowVersion of the shift as last read";

    private const int RowVersionBytes = 8;

    // Seconds required, up to 7 fraction digits (the DB keeps 100 ns), offset always +HH:mm. No "Z": UTC is never Zurich.
    private static readonly OffsetDateTimePattern Pattern =
        OffsetDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm':'ss;FFFFFFFo<+HH:mm>");

    public static ParsedCorrection? TryParse(CorrectionRequest? request, ZurichTime zurich, IDictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(zurich);
        request ??= new CorrectionRequest();

        ShiftCorrectionField? field = request.Field switch
        {
            CorrectionFields.ClockIn => ShiftCorrectionField.ClockIn,
            CorrectionFields.ClockOut => ShiftCorrectionField.ClockOut,
            _ => null,
        };
        if (field is null) errors["field"] = FieldMessage;

        var newUtc = ParseNewTime(request.NewTime, zurich, errors);

        var reason = request.Reason?.Trim();
        if (reason is null || reason.Length < ShiftCorrection.ReasonMinLength || reason.Length > ShiftCorrection.ReasonMaxLength)
            errors["reason"] = ReasonMessage;

        var rowVersion = new byte[RowVersionBytes];
        if (request.RowVersion is null
            || !Convert.TryFromBase64String(request.RowVersion, rowVersion, out var written)
            || written != RowVersionBytes)
            errors["rowVersion"] = RowVersionMessage;

        return errors.Count == 0 ? new ParsedCorrection(field!.Value, newUtc!.Value, reason!, rowVersion) : null;
    }

    private static Instant? ParseNewTime(string? text, ZurichTime zurich, IDictionary<string, string> errors)
    {
        var result = text is null ? null : Pattern.Parse(text);
        if (result is null || !result.Success)
        {
            errors["newTime"] = NewTimeMessage;
            return null;
        }

        var instant = result.Value.ToInstant();
        if (instant < TrustedInstantRules.WindowStart || instant >= TrustedInstantRules.WindowEnd)
        {
            errors["newTime"] = NewTimeMessage;
            return null;
        }
        if (zurich.OffsetAt(instant) != result.Value.Offset)
        {
            errors["newTime"] = OffsetMessage;
            return null;
        }
        return instant;
    }
}
