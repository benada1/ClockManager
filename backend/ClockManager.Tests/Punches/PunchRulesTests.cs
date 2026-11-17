using ClockManager.Api.Application.Punches;
using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using ClockManager.Tests.Fakes;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Tests.Punches;

/// <summary>
/// Plan 03 §6, the unit part (P2–P4, P11, P13, P14): what a punch returns, built exactly as <c>PunchService</c> builds
/// it (fake trusted clock → <c>PunchStamp.From</c> → <c>Shift</c> → <c>ShiftViewMapper</c>), with no database.
/// Instants are the zurich-time-cases matrix values (T1–T11).
/// </summary>
public sealed class PunchRulesTests
{
    private static readonly ZurichTime Zurich = ZurichTime.FromTzdb();

    // ---- Durations and display across DST (P2, P3) ----

    [Fact]
    public async Task P2_shift_across_DST_start_lasts_7_hours_although_the_wall_clock_shows_8_T4()
    {
        var view = await ClosedShiftAsync("2026-03-28T21:00:00Z", "2026-03-29T04:00:00Z");

        Assert.Equal(7 * 3600, view.DurationSeconds);
        Assert.Equal("2026-03-28T22:00:00+01:00", view.ClockIn.Zurich);
        Assert.Equal("2026-03-29T06:00:00+02:00", view.ClockOut!.Zurich);
        Assert.Equal("2026-03-28", view.WorkDate);
    }

    [Fact]
    public async Task P3_shift_across_DST_end_lasts_9_hours_and_each_punch_keeps_its_own_offset_T5()
    {
        var view = await ClosedShiftAsync("2026-10-24T20:00:00Z", "2026-10-25T05:00:00Z");

        Assert.Equal(9 * 3600, view.DurationSeconds);
        Assert.Equal("2026-10-24T22:00:00+02:00", view.ClockIn.Zurich);
        Assert.Equal("2026-10-25T06:00:00+01:00", view.ClockOut!.Zurich);
    }

    [Fact]
    public async Task P3_shift_inside_the_repeated_hour_is_45_minutes_although_the_wall_clock_goes_backwards_T6()
    {
        var view = await ClosedShiftAsync("2026-10-25T00:30:00Z", "2026-10-25T01:15:00Z");

        Assert.Equal(45 * 60, view.DurationSeconds);
        Assert.Equal("2026-10-25T02:30:00+02:00", view.ClockIn.Zurich);
        Assert.Equal("2026-10-25T02:15:00+01:00", view.ClockOut!.Zurich); // earlier on the wall, later in reality
        Assert.Equal("2026-10-25T00:30:00Z", view.ClockIn.Utc);
        Assert.Equal("2026-10-25T01:15:00Z", view.ClockOut.Utc);
        Assert.Empty(view.Flags);
    }

    [Fact]
    public async Task Clock_in_exactly_at_DST_start_shows_0300_never_0200_T7()
    {
        var clock = new FakeTrustedClock("2026-03-29T01:00:00Z");
        var shift = new Shift(1, PunchStamp.From(await clock.GetNowAsync(default), Zurich));

        var view = ShiftViewMapper.ToView(shift, new ShiftRulesOptions());

        Assert.Equal("2026-03-29T03:00:00+02:00", view.ClockIn.Zurich);
        Assert.Null(view.ClockOut);
        Assert.Null(view.DurationSeconds);
        Assert.Empty(view.Flags);
    }

    [Theory]
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z", 30600, "2026-01-12T08:00:00+01:00", "2026-01-12T16:30:00+01:00")] // T1
    [InlineData("2026-07-06T06:00:00Z", "2026-07-06T14:00:00Z", 28800, "2026-07-06T08:00:00+02:00", "2026-07-06T16:00:00+02:00")] // T2
    public async Task Normal_day_shift_in_winter_and_summer_T1_T2(string inUtc, string outUtc, long seconds, string inZurich, string outZurich)
    {
        var view = await ClosedShiftAsync(inUtc, outUtc);

        Assert.Equal(seconds, view.DurationSeconds);
        Assert.Equal(inZurich, view.ClockIn.Zurich);
        Assert.Equal(outZurich, view.ClockOut!.Zurich);
    }

    [Fact]
    public async Task Sub_second_instants_keep_their_fraction_in_both_strings()
    {
        var view = await ClosedShiftAsync("2026-01-12T07:00:00.123Z", "2026-01-12T15:30:00.122Z");

        Assert.Equal("2026-01-12T07:00:00.123Z", view.ClockIn.Utc);
        Assert.Equal("2026-01-12T08:00:00.123+01:00", view.ClockIn.Zurich);
        Assert.Equal(30599, view.DurationSeconds); // 8h29m59.999s, truncated, not rounded up
    }

    // ---- Work date (P4) ----

    [Theory]
    [InlineData("2026-01-12T21:00:00Z", "2026-01-13T05:00:00Z", "2026-01-12")] // T3 overnight: 22:00 → 06:00 local
    [InlineData("2026-12-31T22:00:00Z", "2027-01-01T06:00:00Z", "2026-12-31")] // T8 New Year's Eve: 23:00 → 07:00 local
    public async Task P4_overnight_shift_belongs_to_the_Zurich_date_of_clock_in(string inUtc, string outUtc, string workDate)
    {
        var view = await ClosedShiftAsync(inUtc, outUtc);

        Assert.Equal(workDate, view.WorkDate);
        Assert.Equal(8 * 3600, view.DurationSeconds);
    }

    [Fact]
    public async Task P4_clock_in_late_evening_UTC_is_already_the_next_day_in_Zurich()
    {
        // 2026-01-12T23:30Z is 00:30 on the 13th in Zurich: the UTC date would be wrong.
        var view = await ClosedShiftAsync("2026-01-12T23:30:00Z", "2026-01-13T07:30:00Z");

        Assert.Equal("2026-01-13", view.WorkDate);
    }

    // ---- Monotonic guard (P11) ----

    [Fact]
    public void P11_time_equal_to_the_last_punch_is_refused_with_503_went_backwards_T11()
    {
        var last = Utc("2026-01-12T07:00:00Z");

        var ex = Assert.Throws<AppException>(() => PunchRules.EnsureLaterThanLastPunch(last, last));

        Assert.Equal(ErrorCodes.TimeWentBackwards, ex.Code);
        Assert.Equal(503, ex.StatusCode);
    }

    [Fact]
    public void P11_time_earlier_than_the_last_punch_is_refused_T10()
    {
        var ex = Assert.Throws<AppException>(() =>
            PunchRules.EnsureLaterThanLastPunch(Utc("2026-01-12T06:59:58Z"), Utc("2026-01-12T07:00:00Z")));

        Assert.Equal(ErrorCodes.TimeWentBackwards, ex.Code);
    }

    [Fact]
    public void P11_time_one_nanosecond_later_is_accepted_and_the_first_punch_ever_has_nothing_to_compare()
    {
        var last = Utc("2026-01-12T07:00:00Z");

        PunchRules.EnsureLaterThanLastPunch(last + Duration.Epsilon, last);
        PunchRules.EnsureLaterThanLastPunch(last, null);
    }

    [Fact]
    public void P11_repeated_hour_clock_out_is_not_mistaken_for_going_backwards_T6()
    {
        // 02:15 (+01) after 02:30 (+02): the wall clock went back, the instant did not.
        PunchRules.EnsureLaterThanLastPunch(Utc("2026-10-25T01:15:00Z"), Utc("2026-10-25T00:30:00Z"));
    }

    [Theory]
    [InlineData("2026-01-12T07:00:00Z", null, null, PunchRules.GuardResult.Ok)]                                      // first punch ever
    [InlineData("2026-01-12T15:30:00Z", "2026-01-12T07:00:00Z", "2026-01-12T07:00:00Z", PunchRules.GuardResult.Ok)]  // normal
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T07:00:00Z", "2026-01-12T07:00:00Z", PunchRules.GuardResult.WentBackwards)]
    [InlineData("2026-01-12T07:00:00Z", null, "2026-01-12T07:00:00Z", PunchRules.GuardResult.PunchedMeanwhile)]      // other tab's clock-in, same instant
    [InlineData("2026-01-12T07:00:00Z", null, "2026-01-12T07:00:00.001Z", PunchRules.GuardResult.PunchedMeanwhile)]
    [InlineData("2026-01-12T15:30:00Z", "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z", PunchRules.GuardResult.PunchedMeanwhile)] // other tab's clock-out
    [InlineData("2026-01-12T15:31:00Z", "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z", PunchRules.GuardResult.Ok)]  // later than it: the DB decides
    public void Guard_tells_a_backwards_clock_from_a_punch_saved_during_the_provider_call(
        string trusted, string? before, string? now, PunchRules.GuardResult expected)
    {
        Assert.Equal(expected, PunchRules.Check(Utc(trusted), before is null ? null : Utc(before), now is null ? null : Utc(now)));
    }

    // ---- Flags (P13, P14) ----

    [Fact]
    public async Task P13_clock_out_after_17_hours_is_flagged_long_shift_T9()
    {
        var view = await ClosedShiftAsync("2026-01-12T07:00:00Z", "2026-01-13T00:00:00Z");

        Assert.Equal(17 * 3600, view.DurationSeconds);
        Assert.Equal([ShiftFlags.LongShift], view.Flags);
    }

    [Fact]
    public async Task P13_exactly_the_maximum_is_not_long_one_millisecond_more_is()
    {
        var exactly16h = await ClosedShiftAsync("2026-01-12T07:00:00Z", "2026-01-12T23:00:00Z");
        var justOver = await ClosedShiftAsync("2026-01-12T07:00:00Z", "2026-01-12T23:00:00.001Z");

        Assert.Empty(exactly16h.Flags);
        Assert.Equal([ShiftFlags.LongShift], justOver.Flags);
    }

    [Fact]
    public async Task P13_long_shift_threshold_comes_from_ShiftRulesOptions()
    {
        var view = await ClosedShiftAsync("2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z", new ShiftRulesOptions { MaxOpenShiftHours = 8 });

        Assert.Equal([ShiftFlags.LongShift], view.Flags);
    }

    [Fact]
    public async Task P14_clock_out_20_seconds_after_clock_in_is_flagged_short_shift()
    {
        var view = await ClosedShiftAsync("2026-01-12T07:00:00Z", "2026-01-12T07:00:20Z");

        Assert.Equal(20, view.DurationSeconds);
        Assert.Equal([ShiftFlags.ShortShift], view.Flags);
    }

    [Fact]
    public async Task P14_exactly_ShortShiftSeconds_is_not_short_just_under_is()
    {
        var exactly60s = await ClosedShiftAsync("2026-01-12T07:00:00Z", "2026-01-12T07:01:00Z");
        var justUnder = await ClosedShiftAsync("2026-01-12T07:00:00Z", "2026-01-12T07:00:59.999Z");

        Assert.Empty(exactly60s.Flags);
        Assert.Equal([ShiftFlags.ShortShift], justUnder.Flags);
        Assert.Equal(59, justUnder.DurationSeconds);
    }

    [Fact]
    public async Task Flags_are_computed_from_the_UTC_duration_not_the_wall_clock_across_DST_end()
    {
        // T5 shows 22:00 → 06:00 (8 h on the wall) but lasts 9 h: with an 8 h limit it IS long.
        var view = await ClosedShiftAsync("2026-10-24T20:00:00Z", "2026-10-25T05:00:00Z", new ShiftRulesOptions { MaxOpenShiftHours = 8 });

        Assert.Equal([ShiftFlags.LongShift], view.Flags);
    }

    // ---------- Helpers ----------

    private static async Task<ShiftView> ClosedShiftAsync(string inUtc, string outUtc, ShiftRulesOptions? rules = null)
    {
        var clock = new FakeTrustedClock(inUtc);
        var shift = new Shift(1, PunchStamp.From(await clock.GetNowAsync(default), Zurich));
        clock.Set(outUtc);
        var trustedOut = await clock.GetNowAsync(default);
        PunchRules.EnsureLaterThanLastPunch(trustedOut.Utc, shift.ClockIn.Utc);
        shift.Close(PunchStamp.From(trustedOut, Zurich));
        return ShiftViewMapper.ToView(shift, rules ?? new ShiftRulesOptions());
    }

    private static Instant Utc(string iso) => InstantPattern.ExtendedIso.Parse(iso).GetValueOrThrow();
}
