using ClockManager.Api.Application.Manager;
using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Manager;

/// <summary>Pure parsing of the correction body and the pure rules (plan 07 §3). Values from the zurich-time-cases matrix.</summary>
public sealed class CorrectionInputTests
{
    private const string Rv = "AAAAAAAAB9E="; // 8 bytes

    private static (ParsedCorrection? Parsed, Dictionary<string, string> Errors) Parse(
        string? newTime, string? field = "clockOut", string? reason = "Forgot to clock out", string? rowVersion = Rv)
    {
        var errors = new Dictionary<string, string>();
        var parsed = CorrectionInput.TryParse(
            new CorrectionRequest { Field = field, NewTime = newTime, Reason = reason, RowVersion = rowVersion }, Zurich, errors);
        return (parsed, errors);
    }

    [Theory] // T13: both 02:30 of 2026-10-25 exist; the offset says which one
    [InlineData("2026-10-25T02:30:00+02:00", "2026-10-25T00:30:00Z")]
    [InlineData("2026-10-25T02:30:00+01:00", "2026-10-25T01:30:00Z")]
    [InlineData("2026-11-17T17:12:00+01:00", "2026-11-17T16:12:00Z")]
    [InlineData("2026-07-06T08:00:00.5+02:00", "2026-07-06T06:00:00.5Z")]
    public void A_time_with_the_matching_Zurich_offset_is_accepted(string newTime, string expectedUtc)
    {
        var (parsed, errors) = Parse(newTime);
        Assert.Empty(errors);
        Assert.Equal(Utc(expectedUtc), parsed!.NewUtc);
    }

    [Theory]
    [InlineData("2026-03-29T02:30:00+01:00")] // T12: the spring gap, either offset
    [InlineData("2026-03-29T02:30:00+02:00")]
    [InlineData("2026-11-17T17:12:00+02:00")] // summer offset in winter
    [InlineData("2026-07-06T08:00:00+01:00")]
    public void A_mismatching_offset_or_a_spring_gap_time_is_rejected(string newTime)
    {
        var (parsed, errors) = Parse(newTime);
        Assert.Null(parsed);
        Assert.Equal(CorrectionInput.OffsetMessage, errors["newTime"]);
    }

    [Theory]
    [InlineData("2026-11-17T16:12:00Z")] // UTC is never a Zurich offset
    [InlineData("2026-11-17T17:12:00")] // no offset: never guessed
    [InlineData("2026-11-17T17:12+01:00")] // seconds required
    [InlineData("2026-11-17 17:12:00+01:00")]
    [InlineData("17.11.2026 17:12")]
    [InlineData("2025-12-31T23:00:00+01:00")] // before the plausible window
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_times_get_the_fixed_message(string? newTime)
    {
        var (parsed, errors) = Parse(newTime);
        Assert.Null(parsed);
        Assert.Equal(CorrectionInput.NewTimeMessage, errors["newTime"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("     ")]
    [InlineData("  abc ")]
    public void A_blank_or_short_reason_is_rejected(string? reason)
    {
        var (_, errors) = Parse("2026-11-17T17:12:00+01:00", reason: reason);
        Assert.Equal(CorrectionInput.ReasonMessage, errors["reason"]);
    }

    [Fact]
    public void The_reason_is_trimmed_and_limited_to_500_characters()
    {
        Assert.Equal("Forgot", Parse("2026-11-17T17:12:00+01:00", reason: "  Forgot  ").Parsed!.Reason);
        Assert.Empty(Parse("2026-11-17T17:12:00+01:00", reason: new string('x', 500)).Errors);
        Assert.Equal(CorrectionInput.ReasonMessage, Parse("2026-11-17T17:12:00+01:00", reason: new string('x', 501)).Errors["reason"]);
    }

    [Theory]
    [InlineData("ClockOut")]
    [InlineData("clock-out")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_the_two_exact_field_names_are_accepted(string? field)
    {
        var (_, errors) = Parse("2026-11-17T17:12:00+01:00", field: field);
        Assert.Equal(CorrectionInput.FieldMessage, errors["field"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64")]
    [InlineData("AAAA")] // 3 bytes
    public void The_row_version_must_be_8_bytes_of_base64(string? rowVersion)
    {
        var (_, errors) = Parse("2026-11-17T17:12:00+01:00", rowVersion: rowVersion);
        Assert.Equal(CorrectionInput.RowVersionMessage, errors["rowVersion"]);
    }

    // ---- CorrectionRules ----

    private static readonly ShiftRulesOptions Rules = new() { MaxOpenShiftHours = 16, ShortShiftSeconds = 60 };

    private static Shift Closed(string inUtc, string outUtc)
    {
        var shift = new Shift(1, Stamp(inUtc));
        shift.Close(Stamp(outUtc));
        return shift;
    }

    private static ParsedCorrection Change(ShiftCorrectionField field, string utc) => new(field, Utc(utc), "reason", new byte[8]);

    private static string CodeOf(Action act) => Assert.IsType<Api.Common.Errors.AppException>(Record.Exception(act), exactMatch: false).Code;

    [Fact]
    public void Rules_refuse_unchanged_future_and_out_before_in_in_that_order()
    {
        var t1 = Closed("2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");
        var now = Utc("2026-01-13T07:00:00Z");

        Assert.Equal(ErrorCodes.CorrectionUnchanged, CodeOf(() => CorrectionRules.Check(t1, Change(ShiftCorrectionField.ClockOut, "2026-01-12T15:30:00Z"), now)));
        Assert.Equal(ErrorCodes.CorrectionInFuture, CodeOf(() => CorrectionRules.Check(t1, Change(ShiftCorrectionField.ClockOut, "2026-01-13T07:00:00.0000001Z"), now)));
        Assert.Equal(ErrorCodes.CorrectionInvalidOrder, CodeOf(() => CorrectionRules.Check(t1, Change(ShiftCorrectionField.ClockOut, "2026-01-12T07:00:00Z"), now))); // T11
        Assert.Equal(ErrorCodes.CorrectionInvalidOrder, CodeOf(() => CorrectionRules.Check(t1, Change(ShiftCorrectionField.ClockIn, "2026-01-12T15:30:00Z"), now)));

        var (newIn, newOut) = CorrectionRules.Check(t1, Change(ShiftCorrectionField.ClockOut, "2026-01-13T07:00:00Z"), now); // exactly now is fine
        Assert.Equal(Utc("2026-01-12T07:00:00Z"), newIn);
        Assert.Equal(Utc("2026-01-13T07:00:00Z"), newOut);
    }

    [Fact]
    public void Closing_an_open_shift_needs_it_to_be_open_for_more_than_MaxOpenShiftHours()
    {
        var open = new Shift(1, Stamp("2026-01-12T07:00:00Z")); // T9
        var close = Change(ShiftCorrectionField.ClockOut, "2026-01-12T16:00:00Z");

        Assert.Equal(ErrorCodes.CorrectionShiftStillRunning,
            CodeOf(() => CorrectionRules.EnsureMayClose(open, close, Utc("2026-01-12T23:00:00Z"), Rules))); // exactly 16 h: not forgotten
        CorrectionRules.EnsureMayClose(open, close, Utc("2026-01-12T23:00:00.0000001Z"), Rules);
        CorrectionRules.EnsureMayClose(open, Change(ShiftCorrectionField.ClockIn, "2026-01-12T06:00:00Z"), Utc("2026-01-12T08:00:00Z"), Rules);
    }
}
