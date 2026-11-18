using System.Net;
using System.Text.Json;
using ClockManager.Api.Domain;
using ClockManager.Tests.Manager;
using ClockManager.Tests.Persistence;
using NodaTime;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.History;

/// <summary>
/// Plans/08-qa.md §2.D items 2 and 3: week and range edges, day totals against week totals, clock-change days and
/// shifts that are long, nested in the repeated hour or span days. Instants are computed independently from tzdb in the test.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HistoryQaRangeTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private readonly ManagerTestKit _kit = new(database);

    private static string Z(Instant i) => HistoryQa.Iso(i);

    private static string D(LocalDate d) => NodaTime.Text.LocalDatePattern.Iso.Format(d);

    private static Instant Midnight(int y, int m, int d) => Zurich.Zone.AtStartOfDay(new LocalDate(y, m, d)).ToInstant();

    private static string[] DayDates(JsonElement body) =>
        body.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("date").GetString()!).ToArray();

    // ---- Monday and Sunday boundaries (item 2) ----

    [SqlServerTheory]
    [InlineData(2026, 11, 23)] // winter
    [InlineData(2026, 7, 13)] // summer
    [InlineData(2026, 3, 30)] // the Monday after the 23-hour Sunday
    [InlineData(2026, 10, 26)] // the Monday after the 25-hour Sunday
    [InlineData(2027, 1, 4)] // across New Year (Sunday 2027-01-03)
    public async Task A_clock_in_one_tick_before_Monday_midnight_belongs_to_the_old_week_and_at_midnight_to_the_new_one(int y, int m, int d)
    {
        var (factory, _) = _kit.NewHost("2027-02-01T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (aId, a) = await _kit.AddEmployeeAsync();
        var (bId, b) = await _kit.AddEmployeeAsync();
        var monday = new LocalDate(y, m, d);
        var sunday = monday.PlusDays(-1);
        var midnight = Midnight(y, m, d);
        var lastTick = midnight - Duration.FromTicks(1); // 23:59:59.9999999 on Sunday
        await _kit.SaveClosedAsync(aId, Z(lastTick), Z(lastTick + Duration.FromHours(2)));
        await _kit.SaveClosedAsync(bId, Z(midnight), Z(midnight + Duration.FromHours(2)));
        var oldWeek = $"from={D(sunday.PlusDays(-6))}&to={D(sunday)}";
        var newWeek = $"from={D(monday)}&to={D(monday.PlusDays(6))}";

        var (_, aOld) = await HistoryQa.GetAsync(client, "/api/me/shifts?" + oldWeek, a);
        var (_, aNew) = await HistoryQa.GetAsync(client, "/api/me/shifts?" + newWeek, a);
        var (_, bOld) = await HistoryQa.GetAsync(client, "/api/me/shifts?" + oldWeek, b);
        var (_, bNew) = await HistoryQa.GetAsync(client, "/api/me/shifts?" + newWeek, b);
        var (_, aDefault) = await HistoryQa.GetAsync(client, "/api/me/shifts", a);
        var (_, bDefault) = await HistoryQa.GetAsync(client, "/api/me/shifts", b);

        Assert.Equal([$"{D(sunday)}"], DayDates(aOld));
        Assert.Empty(DayDates(aNew));
        Assert.Empty(DayDates(bOld));
        Assert.Equal([$"{D(monday)}"], DayDates(bNew));
        Assert.Equal(7200, aOld.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(7200, bNew.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(0, aNew.GetProperty("totalSeconds").GetInt64());
        // The default week follows the work date, so a's default is the OLD week and b's the NEW week.
        Assert.Equal($"{D(sunday.PlusDays(-6))}", aDefault.GetProperty("from").GetString());
        Assert.Equal($"{D(sunday)}", aDefault.GetProperty("to").GetString());
        Assert.Equal($"{D(monday)}", bDefault.GetProperty("from").GetString());
        // The shift that starts at 23:59:59.9999999 ends on Monday: endsNextDay, and the clock-in text keeps the offset.
        var seen = HistoryQa.FindInHistory(aOld, (await FirstIdAsync(aId)));
        Assert.True(seen.GetProperty("endsNextDay").GetBoolean());
        Assert.Contains("T23:59:59.9999999", seen.GetProperty("clockIn").GetProperty("zurich").GetString(), StringComparison.Ordinal);
        Assert.Equal(7200, seen.GetProperty("durationSeconds").GetInt64());
    }

    private async Task<long> FirstIdAsync(int employeeId)
    {
        await using var db = database.CreateContext();
        return db.Shifts.Where(s => s.EmployeeId == employeeId).Select(s => s.Id).First();
    }

    [SqlServerTheory]
    [InlineData("2026-11-22")] // the latest shift is on a Sunday
    [InlineData("2026-11-23")] // on a Monday
    public async Task The_default_week_of_a_latest_shift_on_Sunday_or_Monday_is_that_shifts_own_Monday_to_Sunday_week(string latest)
    {
        var (factory, _) = _kit.NewHost("2026-12-01T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await _kit.AddEmployeeAsync();
        var date = NodaTime.Text.LocalDatePattern.Iso.Parse(latest).Value;
        var start = Midnight(date.Year, date.Month, date.Day) + Duration.FromHours(10);
        // Two shifts with the SAME work date (the tie), the second one saved first; and an older one in the previous week.
        await _kit.SaveClosedAsync(id, Z(start + Duration.FromHours(5)), Z(start + Duration.FromHours(6)));
        await _kit.SaveClosedAsync(id, Z(start), Z(start + Duration.FromHours(1)));
        await _kit.SaveClosedAsync(id, Z(start - Duration.FromDays(7)), Z(start - Duration.FromDays(7) + Duration.FromHours(1)));

        var (_, body) = await HistoryQa.GetAsync(client, "/api/me/shifts", user);

        var monday = date.With(DateAdjusters.PreviousOrSame(IsoDayOfWeek.Monday));
        Assert.Equal($"{D(monday)}", body.GetProperty("from").GetString());
        Assert.Equal($"{D(monday.PlusDays(6))}", body.GetProperty("to").GetString());
        Assert.Equal([latest], DayDates(body));
        Assert.Equal(7200, body.GetProperty("totalSeconds").GetInt64());
        var shifts = body.GetProperty("days")[0].GetProperty("shifts").EnumerateArray().ToArray();
        Assert.Equal(2, shifts.Length);
        Assert.True(shifts[0].GetProperty("clockIn").GetProperty("zurich").GetString()!.CompareTo(shifts[1].GetProperty("clockIn").GetProperty("zurich").GetString()) < 0);
    }

    [SqlServerFact]
    public async Task The_default_week_follows_an_open_shift_with_a_later_work_date_than_all_closed_ones()
    {
        var (factory, _) = _kit.NewHost("2026-11-19T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await _kit.AddEmployeeAsync();
        await _kit.SaveClosedAsync(id, "2026-11-10T07:00:00Z", "2026-11-10T15:00:00Z");
        await _kit.SaveOpenAsync(id, "2026-11-18T07:00:00Z");

        var (_, body) = await HistoryQa.GetAsync(client, "/api/me/shifts", user);

        Assert.Equal("2026-11-16", body.GetProperty("from").GetString());
        Assert.Equal(["2026-11-18"], DayDates(body));
        Assert.Equal(0, body.GetProperty("totalSeconds").GetInt64());
    }

    // ---- Range edges ----

    [SqlServerTheory]
    [InlineData("from=2026-11-18&to=2026-11-18", 1)] // from == to
    [InlineData("from=2026-01-01&to=2026-01-02", 0)] // entirely before all shifts
    [InlineData("from=2027-05-01&to=2027-05-31", 0)] // entirely after
    [InlineData("from=2028-02-29&to=2028-02-29", 0)] // a leap day
    [InlineData("from=2028-02-01&to=2028-03-02", 0)] // 31 days inclusive across the leap day
    [InlineData("from=2026-12-25&to=2027-01-24", 0)] // 31 days across New Year
    [InlineData("from=2026-03-01&to=2026-03-31", 0)] // 31 days with the spring change inside
    [InlineData("from=2026-10-01&to=2026-10-31", 0)] // and the autumn one
    [InlineData("from=1900-01-01&to=1900-01-31", 0)] // the lowest allowed
    [InlineData("from=2200-12-01&to=2200-12-31", 0)] // the highest allowed
    public async Task These_ranges_are_accepted_and_echo_the_dates_asked_for(string query, int dayCount)
    {
        var (factory, _) = _kit.NewHost("2026-11-18T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await _kit.AddEmployeeAsync();
        await _kit.SaveClosedAsync(id, "2026-11-18T07:00:00Z", "2026-11-18T15:00:00Z");

        var (status, body) = await HistoryQa.GetAsync(client, "/api/me/shifts?" + query, user);

        Assert.Equal(HttpStatusCode.OK, status);
        var parts = query.Split('&');
        Assert.Equal(parts[0]["from=".Length..], body.GetProperty("from").GetString());
        Assert.Equal(parts[1]["to=".Length..], body.GetProperty("to").GetString());
        Assert.Equal(dayCount, body.GetProperty("days").GetArrayLength());
        Assert.Equal(dayCount * 28800, body.GetProperty("totalSeconds").GetInt64());
    }

    [SqlServerTheory]
    [InlineData("from=2028-02-01&to=2028-03-03")] // 32 days inclusive, leap year
    [InlineData("from=2026-12-25&to=2027-01-25")] // 32 across New Year
    [InlineData("from=2026-03-01&to=2026-04-01")] // 32 across the spring change
    [InlineData("from=2027-02-29&to=2027-03-02")] // 2027 is no leap year
    [InlineData("from=2026-02-29&to=2026-03-01")]
    [InlineData("from=1899-12-31&to=1900-01-02")] // below the lowest allowed date
    [InlineData("from=2200-12-31&to=2201-01-01")] // above the highest
    [InlineData("from=2026-11-16&to=2026-11-22&from=2026-11-01")] // from twice: not a crash
    public async Task These_ranges_are_a_clean_400(string query)
    {
        var (factory, _) = _kit.NewHost("2026-11-18T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await _kit.AddEmployeeAsync();

        var (status, body) = await HistoryQa.GetAsync(client, "/api/me/shifts?" + query, user);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("request.invalid", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("stackTrace", out _));
    }

    // ---- Two years of data around year end and the leap day ----

    [SqlServerFact]
    public async Task Two_years_of_shifts_around_new_year_and_a_leap_day_land_in_the_right_weeks_and_default_week()
    {
        var (factory, _) = _kit.NewHost("2028-03-10T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await _kit.AddEmployeeAsync();
        await _kit.SaveClosedAsync(id, "2026-12-31T22:00:00Z", "2027-01-01T06:00:00Z"); // T8: 23:00 to 07:00, 8 h, work date Dec 31
        await _kit.SaveClosedAsync(id, "2027-01-01T08:00:00Z", "2027-01-01T12:00:00Z"); // 4 h on Jan 1
        await _kit.SaveClosedAsync(id, "2028-02-29T07:00:00Z", "2028-02-29T15:00:00Z"); // leap day, Tuesday
        await _kit.SaveClosedAsync(id, "2028-03-01T07:00:00Z", "2028-03-01T10:00:00Z"); // 3 h

        var (_, ny) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-12-28&to=2027-01-03", user);
        var (_, leap) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2028-02-28&to=2028-03-05", user);
        var (_, def) = await HistoryQa.GetAsync(client, "/api/me/shifts", user);

        Assert.Equal(["2026-12-31", "2027-01-01"], DayDates(ny));
        Assert.Equal([8 * 3600, 4 * 3600], ny.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("totalSeconds").GetInt64()));
        Assert.Equal(12 * 3600, ny.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(["2028-02-29", "2028-03-01"], DayDates(leap));
        Assert.Equal(11 * 3600, leap.GetProperty("totalSeconds").GetInt64());
        Assert.Equal("2028-02-28", def.GetProperty("from").GetString()); // the week of the latest work date, 2028-03-01
        Assert.Equal("2028-03-05", def.GetProperty("to").GetString());
        Assert.Equal(leap.GetProperty("totalSeconds").GetInt64(), def.GetProperty("totalSeconds").GetInt64());
        // A day without shifts is not listed.
        Assert.DoesNotContain("2028-03-02", DayDates(def));
    }

    // ---- Clock-change weeks with a shift on EVERY day ----

    [SqlServerTheory]
    [InlineData(2026, 3, 23, "day")] // Monday of the spring change week (change on the Sunday)
    [InlineData(2026, 3, 23, "night")]
    [InlineData(2026, 10, 19, "day")]
    [InlineData(2026, 10, 19, "night")]
    public async Task A_week_with_a_shift_every_day_sums_the_UTC_durations_exactly(int y, int m, int d, string pattern)
    {
        var (factory, _) = _kit.NewHost("2026-12-01T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await _kit.AddEmployeeAsync();
        var monday = new LocalDate(y, m, d);
        var expectedPerDay = new List<long>();
        for (var i = 0; i < 7; i++)
        {
            var date = monday.PlusDays(i);
            // day: 09:00 to 17:00 local on the same date. night: 22:00 local to 06:00 local the next date.
            var start = pattern == "day" ? Zurich.Zone.AtStrictly(date.At(new LocalTime(9, 0))).ToInstant() : Zurich.Zone.AtStrictly(date.At(new LocalTime(22, 0))).ToInstant();
            var end = pattern == "day" ? Zurich.Zone.AtStrictly(date.At(new LocalTime(17, 0))).ToInstant() : Zurich.Zone.AtStrictly(date.PlusDays(1).At(new LocalTime(6, 0))).ToInstant();
            await _kit.SaveClosedAsync(id, Z(start), Z(end));
            expectedPerDay.Add(HistoryQa.Seconds(start, end));
        }
        var sunday = monday.PlusDays(6);

        var (_, body) = await HistoryQa.GetAsync(client, $"/api/me/shifts?from={D(monday)}&to={D(sunday)}", user);

        Assert.Equal(7, body.GetProperty("days").GetArrayLength());
        var days = body.GetProperty("days").EnumerateArray().ToArray();
        Assert.Equal(expectedPerDay, days.Select(x => x.GetProperty("totalSeconds").GetInt64()));
        Assert.Equal(expectedPerDay.Sum(), body.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(days.Sum(x => x.GetProperty("totalSeconds").GetInt64()), body.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(days.Sum(x => x.GetProperty("shifts").EnumerateArray().Sum(s => s.GetProperty("durationSeconds").GetInt64())),
            body.GetProperty("totalSeconds").GetInt64());
        var noted = days.Where(x => x.GetProperty("notes").GetArrayLength() > 0).Select(x => x.GetProperty("date").GetString()).ToArray();
        Assert.Equal([$"{D(sunday)}"], noted.Select(n => n!).ToArray()); // only the change Sunday
        // The expected total of the week, from first principles: 7 x 8 h, +1 h on the autumn night chain, -1 h on the spring one,
        // and 8 h on the wall clock everywhere else.
        var expectedTotal = (pattern, m) switch
        {
            ("day", 3) => 7 * 8 * 3600L, // 09:00-17:00 is never across a change
            ("day", 10) => 7 * 8 * 3600L,
            ("night", 3) => (7 * 8 - 1) * 3600L, // Saturday 22:00 to Sunday 06:00 loses the skipped hour
            ("night", 10) => (7 * 8 + 1) * 3600L, // and gains the repeated one
            _ => throw new InvalidOperationException(),
        };
        Assert.Equal(expectedTotal, body.GetProperty("totalSeconds").GetInt64());
    }

    [SqlServerFact]
    public async Task A_shift_covering_the_whole_25_hour_and_the_whole_23_hour_day_is_exactly_that_long()
    {
        var (factory, _) = _kit.NewHost("2026-12-01T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (autumn, a) = await _kit.AddEmployeeAsync();
        var (spring, s) = await _kit.AddEmployeeAsync();
        await _kit.SaveClosedAsync(autumn, Z(Midnight(2026, 10, 25)), Z(Midnight(2026, 10, 26)));
        await _kit.SaveClosedAsync(spring, Z(Midnight(2026, 3, 29)), Z(Midnight(2026, 3, 30)));

        var (_, aw) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-10-19&to=2026-10-25", a);
        var (_, sw) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-03-23&to=2026-03-29", s);

        Assert.Equal(25 * 3600, aw.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(23 * 3600, sw.GetProperty("totalSeconds").GetInt64());
        var autumnShift = aw.GetProperty("days")[0].GetProperty("shifts")[0];
        Assert.Equal("2026-10-25T00:00:00+02:00", autumnShift.GetProperty("clockIn").GetProperty("zurich").GetString());
        Assert.Equal("2026-10-26T00:00:00+01:00", autumnShift.GetProperty("clockOut").GetProperty("zurich").GetString());
        Assert.True(autumnShift.GetProperty("endsNextDay").GetBoolean());
        Assert.Equal(["long_shift"], autumnShift.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        var springShift = sw.GetProperty("days")[0].GetProperty("shifts")[0];
        Assert.Equal("2026-03-29T00:00:00+01:00", springShift.GetProperty("clockIn").GetProperty("zurich").GetString());
        Assert.Equal("2026-03-30T00:00:00+02:00", springShift.GetProperty("clockOut").GetProperty("zurich").GetString());
        Assert.Equal(["long_shift"], springShift.GetProperty("flags").EnumerateArray().Select(f => f.GetString())); // 23 h > 16 h
        // The 25-hour day is a 25-hour day, the next day is not listed.
        Assert.Equal(["2026-10-25"], DayDates(aw));
        Assert.Equal(["dst_change"], aw.GetProperty("days")[0].GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
    }

    [SqlServerFact]
    public async Task A_shift_ending_in_the_second_pass_of_the_repeated_hour_and_one_across_the_spring_gap_show_offsets_and_true_length()
    {
        var (factory, _) = _kit.NewHost("2026-12-01T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await _kit.AddEmployeeAsync();
        var (id2, user2) = await _kit.AddEmployeeAsync();
        await _kit.SaveClosedAsync(id, "2026-10-25T00:10:00Z", "2026-10-25T01:50:00Z"); // 02:10 (+02:00) to 02:50 (+01:00)
        await _kit.SaveClosedAsync(id2, "2026-03-29T00:30:00Z", "2026-03-29T01:30:00Z"); // 01:30 (+01:00) to 03:30 (+02:00)

        var (_, autumn) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-10-25&to=2026-10-25", user);
        var (_, spring) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-03-29&to=2026-03-29", user2);

        var a = HistoryQa.FromShift(autumn.GetProperty("days")[0].GetProperty("shifts")[0]);
        Assert.Equal("2026-10-25T02:10:00+02:00", a.In);
        Assert.Equal("2026-10-25T02:50:00+01:00", a.Out);
        Assert.Equal(6000, a.Duration); // 100 minutes though the wall clock says 40
        Assert.Equal("", a.Flags);
        var s = HistoryQa.FromShift(spring.GetProperty("days")[0].GetProperty("shifts")[0]);
        Assert.Equal("2026-03-29T01:30:00+01:00", s.In);
        Assert.Equal("2026-03-29T03:30:00+02:00", s.Out);
        Assert.Equal(3600, s.Duration); // an hour though the wall clock says two
        Assert.False(spring.GetProperty("days")[0].GetProperty("shifts")[0].GetProperty("endsNextDay").GetBoolean());
    }

    [SqlServerFact]
    public async Task A_shift_over_two_midnights_belongs_to_the_week_it_started_in_even_when_that_week_ends_on_the_day_it_started()
    {
        var (factory, _) = _kit.NewHost("2026-02-01T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await _kit.AddEmployeeAsync();
        // Sunday 2026-01-11 22:00 to Tuesday 2026-01-13 08:00 Zurich: 34 h.
        var start = Zurich.Zone.AtStrictly(new LocalDate(2026, 1, 11).At(new LocalTime(22, 0))).ToInstant();
        var end = Zurich.Zone.AtStrictly(new LocalDate(2026, 1, 13).At(new LocalTime(8, 0))).ToInstant();
        var shiftId = await _kit.SaveClosedAsync(id, Z(start), Z(end));

        var (_, startWeek) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-01-05&to=2026-01-11", user);
        var (_, endWeek) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-01-12&to=2026-01-18", user);
        var (_, def) = await HistoryQa.GetAsync(client, "/api/me/shifts", user);

        var s = HistoryQa.FindInHistory(startWeek, shiftId);
        Assert.Equal(34 * 3600, s.GetProperty("durationSeconds").GetInt64());
        Assert.True(s.GetProperty("endsNextDay").GetBoolean());
        Assert.Equal("2026-01-11", s.GetProperty("workDate").GetString());
        Assert.Equal(["long_shift"], s.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(34 * 3600, startWeek.GetProperty("totalSeconds").GetInt64());
        Assert.Empty(DayDates(endWeek));
        Assert.Equal(0, endWeek.GetProperty("totalSeconds").GetInt64());
        Assert.Equal("2026-01-05", def.GetProperty("from").GetString());
    }

    // ---- Sub-second shifts and rounding ----

    [SqlServerFact]
    public async Task A_sub_second_shift_is_listed_as_0_seconds_and_short_on_every_endpoint()
    {
        var (factory, _) = _kit.NewHost("2026-02-01T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (id, user) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveClosedAsync(id, "2026-01-12T07:00:00Z", "2026-01-12T07:00:00.5000000Z");

        var (_, mine) = await HistoryQa.GetAsync(client, "/api/me/shifts", user);
        var (_, anomalies) = await HistoryQa.GetAsync(client, "/api/manager/anomalies", manager);

        var s = HistoryQa.FromShift(HistoryQa.FindInHistory(mine, shiftId));
        Assert.Equal(0, s.Duration);
        Assert.Equal("short_shift", s.Flags);
        Assert.Equal("2026-01-12T08:00:00.5+01:00", s.Out); // fractions: shortest form, no trailing zeros
        var row = anomalies.EnumerateArray().Single(a => a.GetProperty("shiftId").GetInt64() == shiftId);
        Assert.Equal(0, row.GetProperty("durationSeconds").GetInt64());
        Assert.Equal("short_shift", row.GetProperty("kind").GetString());
    }

    /// <summary>
    /// Decision (known limit, not a bug): every duration is truncated to whole seconds PER SHIFT, and day and week totals add
    /// the truncated numbers. Real punches carry milliseconds, so a total can be up to 1 s per shift below the exact UTC sum.
    /// The numbers still agree with each other everywhere (day total = sum of its shifts, week total = sum of day totals),
    /// which is what a reader of the page can check; the exact sum is not shown anywhere.
    /// </summary>
    [SqlServerFact]
    public async Task Totals_add_the_truncated_per_shift_seconds_and_agree_with_the_day_and_week_lines()
    {
        var (factory, _) = _kit.NewHost("2026-02-01T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await _kit.AddEmployeeAsync();
        // Four shifts of 1.5 s (exact sum 6 s), then two of 3600.999 s (exact 7201.998).
        for (var i = 0; i < 4; i++)
        {
            var start = Utc("2026-01-12T07:00:00Z") + Duration.FromMinutes(10 * i);
            await _kit.SaveClosedAsync(id, Z(start), Z(start + Duration.FromMilliseconds(1500)));
        }
        for (var i = 0; i < 2; i++)
        {
            var start = Utc("2026-01-12T09:00:00Z") + Duration.FromHours(2 * i);
            await _kit.SaveClosedAsync(id, Z(start), Z(start + Duration.FromMilliseconds(3_600_999)));
        }

        var (_, body) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-01-12&to=2026-01-18", user);

        var day = body.GetProperty("days")[0];
        var shifts = day.GetProperty("shifts").EnumerateArray().Select(s => s.GetProperty("durationSeconds").GetInt64()).ToArray();
        Assert.Equal([1L, 1, 1, 1, 3600, 3600], shifts);
        Assert.Equal(7204, day.GetProperty("totalSeconds").GetInt64()); // exact: 7207.998 s
        Assert.Equal(7204, body.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(shifts.Sum(), day.GetProperty("totalSeconds").GetInt64());
    }
}
