using System.Net;
using System.Text.Json;
using ClockManager.Api.Application.History;
using ClockManager.Api.Domain;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using ClockManager.Tests.Punches;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.History;

/// <summary>
/// <c>GET /api/me/shifts</c> through the real host and a real SQL Server (plans/06-history.md). Shifts are seeded with fixed
/// instants from the zurich-time-cases matrix; the fake trusted clock only matters for the forgotten flag.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ShiftHistoryApiTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private (PunchApiFactory Factory, FakeTrustedClock Clock) NewHost(string nowUtc = "2026-11-18T12:00:00Z")
    {
        var clock = new FakeTrustedClock(nowUtc);
        return (new PunchApiFactory(database, clock), clock);
    }

    // ---- Totals and grouping ----

    [SqlServerFact]
    public async Task A_normal_week_lists_days_in_order_with_day_and_week_totals()
    {
        var (factory, _) = NewHost();
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await AddEmployeeAsync();
        await SaveClosedAsync(id, "2026-11-18T12:00:00Z", "2026-11-18T16:00:00Z"); // Wed afternoon (inserted first)
        await SaveClosedAsync(id, "2026-11-18T07:00:00Z", "2026-11-18T11:00:00Z"); // Wed morning
        await SaveClosedAsync(id, "2026-11-17T07:03:11Z", "2026-11-17T16:12:20Z"); // Tue 08:03:11 to 17:12:20 (+01:00)
        await SaveClosedAsync(id, "2026-11-23T07:00:00Z", "2026-11-23T15:00:00Z"); // next week: not listed

        var (status, body) = await GetAsync(client, user, "from=2026-11-16&to=2026-11-22");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["from", "to", "days", "totalSeconds"], Names(body));
        Assert.Equal("2026-11-16", body.GetProperty("from").GetString());
        Assert.Equal("2026-11-22", body.GetProperty("to").GetString());
        var days = body.GetProperty("days").EnumerateArray().ToArray();
        Assert.Equal(["2026-11-17", "2026-11-18"], days.Select(d => d.GetProperty("date").GetString()));
        Assert.Equal(["date", "totalSeconds", "notes", "shifts"], Names(days[0]));
        Assert.Equal(32949, days[0].GetProperty("totalSeconds").GetInt64());
        Assert.Equal(28800, days[1].GetProperty("totalSeconds").GetInt64());
        Assert.Equal(61749, body.GetProperty("totalSeconds").GetInt64());

        var tue = days[0].GetProperty("shifts")[0];
        Assert.Equal(["shiftId", "workDate", "clockIn", "clockOut", "durationSeconds", "endsNextDay", "open", "flags"], Names(tue));
        Assert.Equal("2026-11-17T08:03:11+01:00", tue.GetProperty("clockIn").GetProperty("zurich").GetString());
        Assert.Equal("2026-11-17T17:12:20+01:00", tue.GetProperty("clockOut").GetProperty("zurich").GetString());
        Assert.Equal(32949, tue.GetProperty("durationSeconds").GetInt64());
        Assert.False(tue.GetProperty("endsNextDay").GetBoolean());
        Assert.False(tue.GetProperty("open").GetBoolean());
        Assert.Equal(0, tue.GetProperty("flags").GetArrayLength());
        Assert.Equal(0, days[0].GetProperty("notes").GetArrayLength());

        var wed = days[1].GetProperty("shifts").EnumerateArray().ToArray();
        Assert.Equal(["2026-11-18T08:00:00+01:00", "2026-11-18T13:00:00+01:00"], wed.Select(s => s.GetProperty("clockIn").GetProperty("zurich").GetString()));
    }

    [SqlServerFact]
    public async Task Spring_dst_week_counts_the_real_hours_and_notes_the_change_day()
    {
        var (factory, _) = NewHost("2026-03-30T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await AddEmployeeAsync();
        await SaveClosedAsync(id, "2026-03-28T21:00:00Z", "2026-03-29T04:00:00Z"); // T4: 22:00 to 06:00 on the wall clock, 7 h
        await SaveClosedAsync(id, "2026-03-29T00:30:00Z", "2026-03-29T04:30:00Z"); // 01:30 (+01:00) to 06:30 (+02:00), 4 h

        var (_, body) = await GetAsync(client, user, "from=2026-03-23&to=2026-03-29");

        var days = body.GetProperty("days").EnumerateArray().ToArray();
        Assert.Equal(["2026-03-28", "2026-03-29"], days.Select(d => d.GetProperty("date").GetString()));
        Assert.Equal(25200, days[0].GetProperty("totalSeconds").GetInt64()); // 7 h, not 8 h
        Assert.Equal(0, days[0].GetProperty("notes").GetArrayLength());
        Assert.True(days[0].GetProperty("shifts")[0].GetProperty("endsNextDay").GetBoolean());
        Assert.Equal(["dst_change"], days[1].GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(14400, days[1].GetProperty("totalSeconds").GetInt64());
        Assert.Equal(39600, body.GetProperty("totalSeconds").GetInt64());
    }

    [SqlServerFact]
    public async Task Autumn_dst_week_counts_9_hours_and_notes_the_25_hour_day()
    {
        var (factory, _) = NewHost("2026-10-26T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await AddEmployeeAsync();
        await SaveClosedAsync(id, "2026-10-24T20:00:00Z", "2026-10-25T05:00:00Z"); // T5: 9 h
        await SaveClosedAsync(id, "2026-10-25T00:30:00Z", "2026-10-25T01:15:00Z"); // T6: 02:30 (+02:00) to 02:15 (+01:00), 45 min

        var (_, body) = await GetAsync(client, user, "from=2026-10-19&to=2026-10-25"); // the Monday-Sunday week of Oct 25

        var days = body.GetProperty("days").EnumerateArray().ToArray();
        Assert.Equal(["2026-10-24", "2026-10-25"], days.Select(d => d.GetProperty("date").GetString()));
        Assert.Equal(32400, days[0].GetProperty("totalSeconds").GetInt64());
        Assert.Equal(["dst_change"], days[1].GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(2700, days[1].GetProperty("totalSeconds").GetInt64());
        var repeated = days[1].GetProperty("shifts")[0];
        Assert.Equal("2026-10-25T02:30:00+02:00", repeated.GetProperty("clockIn").GetProperty("zurich").GetString());
        Assert.Equal("2026-10-25T02:15:00+01:00", repeated.GetProperty("clockOut").GetProperty("zurich").GetString());
        Assert.Equal(35100, body.GetProperty("totalSeconds").GetInt64());
    }

    [SqlServerFact]
    public async Task A_year_boundary_week_keeps_the_shift_on_the_day_it_started()
    {
        var (factory, _) = NewHost("2027-01-04T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await AddEmployeeAsync();
        await SaveClosedAsync(id, "2026-12-31T22:00:00Z", "2027-01-01T06:00:00Z"); // T8: 23:00 on Dec 31 to 07:00 on Jan 1
        await SaveClosedAsync(id, "2027-01-01T08:00:00Z", "2027-01-01T12:00:00Z");

        var (_, body) = await GetAsync(client, user, "from=2026-12-28&to=2027-01-03");

        var days = body.GetProperty("days").EnumerateArray().ToArray();
        Assert.Equal(["2026-12-31", "2027-01-01"], days.Select(d => d.GetProperty("date").GetString()));
        var overnight = days[0].GetProperty("shifts")[0];
        Assert.Equal("2026-12-31", overnight.GetProperty("workDate").GetString());
        Assert.Equal("2026-12-31T23:00:00+01:00", overnight.GetProperty("clockIn").GetProperty("zurich").GetString());
        Assert.Equal("2027-01-01T07:00:00+01:00", overnight.GetProperty("clockOut").GetProperty("zurich").GetString());
        Assert.True(overnight.GetProperty("endsNextDay").GetBoolean());
        Assert.Equal(28800, days[0].GetProperty("totalSeconds").GetInt64());
        Assert.Equal(14400, days[1].GetProperty("totalSeconds").GetInt64());
        Assert.Equal(43200, body.GetProperty("totalSeconds").GetInt64());
    }

    [SqlServerFact]
    public async Task A_sunday_night_shift_stays_on_sunday_and_in_that_week()
    {
        var (factory, _) = NewHost("2026-11-24T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await AddEmployeeAsync();
        await SaveClosedAsync(id, "2026-11-22T22:00:00Z", "2026-11-23T06:00:00Z"); // Sun 23:00 to Mon 07:00 (+01:00)

        var (_, thisWeek) = await GetAsync(client, user, "from=2026-11-16&to=2026-11-22");
        var (_, nextWeek) = await GetAsync(client, user, "from=2026-11-23&to=2026-11-29");

        var shift = thisWeek.GetProperty("days")[0].GetProperty("shifts")[0];
        Assert.Equal("2026-11-22", thisWeek.GetProperty("days")[0].GetProperty("date").GetString());
        Assert.True(shift.GetProperty("endsNextDay").GetBoolean());
        Assert.Equal(28800, thisWeek.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(0, nextWeek.GetProperty("days").GetArrayLength());
        Assert.Equal(0, nextWeek.GetProperty("totalSeconds").GetInt64());
    }

    // ---- Open shift and the forgotten flag ----

    [SqlServerFact]
    public async Task An_open_shift_is_listed_with_no_duration_and_is_not_counted()
    {
        var (factory, _) = NewHost("2026-11-18T10:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await AddEmployeeAsync();
        await SaveClosedAsync(id, "2026-11-17T07:00:00Z", "2026-11-17T15:00:00Z");
        await SaveOpenAsync(id, "2026-11-18T07:00:00Z");

        var (_, body) = await GetAsync(client, user, "from=2026-11-16&to=2026-11-22");

        var open = body.GetProperty("days")[1].GetProperty("shifts")[0];
        Assert.True(open.GetProperty("open").GetBoolean());
        Assert.Equal(JsonValueKind.Null, open.GetProperty("durationSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("clockOut").ValueKind);
        Assert.False(open.GetProperty("endsNextDay").GetBoolean());
        Assert.Equal(0, open.GetProperty("flags").GetArrayLength()); // 3 h old: not forgotten
        Assert.Equal(0, body.GetProperty("days")[1].GetProperty("totalSeconds").GetInt64());
        Assert.Equal(28800, body.GetProperty("totalSeconds").GetInt64());
    }

    [SqlServerFact]
    public async Task An_old_open_shift_is_flagged_forgotten_only_when_trusted_time_exists()
    {
        var (withTime, _) = NewHost("2026-01-13T07:00:00Z"); // 24 h after the clock-in
        var (withoutTime, brokenClock) = NewHost();
        brokenClock.FailWith(FakeTrustedClock.Unavailable());
        await using var _f1 = withTime;
        await using var _f2 = withoutTime;
        var (id, user) = await AddEmployeeAsync();
        await SaveOpenAsync(id, "2026-01-12T07:00:00Z");

        using var client1 = withTime.CreateClient();
        var (s1, flagged) = await GetAsync(client1, user, "from=2026-01-12&to=2026-01-12");
        using var client2 = withoutTime.CreateClient();
        var (s2, unflagged) = await GetAsync(client2, user, "from=2026-01-12&to=2026-01-12");

        Assert.Equal(HttpStatusCode.OK, s1);
        Assert.Equal(["forgotten_clock_out"], Flags(flagged));
        Assert.Equal(HttpStatusCode.OK, s2); // a provider problem is not an error here
        Assert.Empty(Flags(unflagged));
    }

    [SqlServerFact]
    public async Task Closed_shifts_get_the_long_and_short_flags()
    {
        var (factory, _) = NewHost();
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await AddEmployeeAsync();
        await SaveClosedAsync(id, "2026-11-16T05:00:00Z", "2026-11-16T22:00:00Z"); // 17 h
        await SaveClosedAsync(id, "2026-11-17T07:00:00Z", "2026-11-17T07:00:30Z"); // 30 s

        var (_, body) = await GetAsync(client, user, "from=2026-11-16&to=2026-11-22");

        Assert.Equal(["long_shift"], body.GetProperty("days")[0].GetProperty("shifts")[0].GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(["short_shift"], body.GetProperty("days")[1].GetProperty("shifts")[0].GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
    }

    // ---- Who ----

    [SqlServerFact]
    public async Task Another_employees_shifts_never_appear()
    {
        var (factory, _) = NewHost();
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (me, user) = await AddEmployeeAsync();
        var (other, _) = await AddEmployeeAsync();
        await SaveClosedAsync(me, "2026-11-17T07:00:00Z", "2026-11-17T15:00:00Z");
        await SaveClosedAsync(other, "2026-11-17T07:00:00Z", "2026-11-17T19:00:00Z");
        await SaveClosedAsync(other, "2026-11-19T07:00:00Z", "2026-11-19T19:00:00Z");

        var (_, ranged) = await GetAsync(client, user, "from=2026-11-16&to=2026-11-22");
        var (_, defaulted) = await GetAsync(client, user, "");

        Assert.Equal(28800, ranged.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(1, ranged.GetProperty("days").GetArrayLength());
        Assert.Equal(28800, defaulted.GetProperty("totalSeconds").GetInt64()); // the default week ignores the other person's later shift
    }

    [SqlServerFact]
    public async Task Without_a_login_the_answer_is_401()
    {
        var (factory, _) = NewHost();
        await using var _f = factory;
        using var client = factory.CreateClient();

        var (status, _) = await GetAsync(client, null, "from=2026-11-16&to=2026-11-22");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    // ---- Default week ----

    [SqlServerFact]
    public async Task Without_dates_the_week_of_the_latest_work_date_is_returned()
    {
        var (factory, _) = NewHost();
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (id, user) = await AddEmployeeAsync();
        await SaveClosedAsync(id, "2026-11-18T07:00:00Z", "2026-11-18T15:00:00Z"); // latest work date, lowest Id
        await SaveClosedAsync(id, "2026-11-10T07:00:00Z", "2026-11-10T15:00:00Z"); // an earlier week, saved later

        var (status, body) = await GetAsync(client, user, "");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("2026-11-16", body.GetProperty("from").GetString());
        Assert.Equal("2026-11-22", body.GetProperty("to").GetString());
        Assert.Equal(["2026-11-18"], body.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("date").GetString()));
    }

    [SqlServerFact]
    public async Task With_no_shifts_at_all_the_answer_is_empty_with_null_dates()
    {
        var (factory, _) = NewHost();
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        var (status, body) = await GetAsync(client, user, "");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("from").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("to").ValueKind);
        Assert.Equal(0, body.GetProperty("days").GetArrayLength());
        Assert.Equal(0, body.GetProperty("totalSeconds").GetInt64());
    }

    [SqlServerFact]
    public async Task A_range_of_exactly_31_days_is_accepted()
    {
        var (factory, _) = NewHost();
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        var (status, _) = await GetAsync(client, user, "from=2026-01-01&to=2026-01-31");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    // ---- Bad input ----

    [SqlServerTheory]
    [InlineData("from=2026-11-16", "to", ShiftHistoryService.BothOrNoneMessage)]
    [InlineData("to=2026-11-22", "from", ShiftHistoryService.BothOrNoneMessage)]
    [InlineData("from=2026-11-22&to=2026-11-16", "to", ShiftHistoryService.BeforeFromMessage)]
    [InlineData("from=2026-01-01&to=2026-02-01", "to", ShiftHistoryService.TooLongMessage)] // 32 days
    [InlineData("from=2026-13-01&to=2026-11-22", "from", ShiftHistoryService.InvalidDateMessage)]
    [InlineData("from=2026-11-16&to=2026-02-30", "to", ShiftHistoryService.InvalidDateMessage)]
    [InlineData("from=2026-1-5&to=2026-11-22", "from", ShiftHistoryService.InvalidDateMessage)]
    [InlineData("from=0001-01-01&to=0001-01-07", "from", ShiftHistoryService.InvalidDateMessage)]
    [InlineData("from=&to=", "from", ShiftHistoryService.InvalidDateMessage)]
    public async Task Bad_dates_are_a_400_with_a_fixed_message(string query, string key, string message)
    {
        var (factory, _) = NewHost();
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        var (status, problem) = await GetAsync(client, user, query);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("request.invalid", problem.GetProperty("code").GetString());
        Assert.Equal(message, problem.GetProperty("errors").GetProperty(key)[0].GetString());
    }

    [SqlServerFact]
    public async Task An_invalid_date_text_is_never_echoed_back()
    {
        var (factory, _) = NewHost();
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        var (status, problem) = await GetAsync(client, user, "from=<script>alert(1)</script>&to=2026-11-22");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.DoesNotContain("script", problem.GetRawText(), StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private async Task<(int Id, string Username)> AddEmployeeAsync()
    {
        await using var db = database.CreateContext();
        var employee = new Employee("e" + Guid.NewGuid().ToString("N")[..12], "Test Employee", EmployeeRole.Employee);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (employee.Id, employee.Username);
    }

    private async Task SaveClosedAsync(int employeeId, string inUtc, string outUtc)
    {
        await using var db = database.CreateContext();
        var shift = new Shift(employeeId, Stamp(inUtc));
        shift.Close(Stamp(outUtc));
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
    }

    private async Task SaveOpenAsync(int employeeId, string inUtc)
    {
        await using var db = database.CreateContext();
        db.Shifts.Add(new Shift(employeeId, Stamp(inUtc)));
        await db.SaveChangesAsync();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string? user, string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, query.Length == 0 ? "/api/me/shifts" : "/api/me/shifts?" + query);
        if (user is not null) request.Headers.TryAddWithoutValidation(PunchApiFactory.TestUserHeader, user);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response.StatusCode, json);
    }

    private static string[] Names(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name).ToArray();

    private static string[] Flags(JsonElement body) =>
        body.GetProperty("days")[0].GetProperty("shifts")[0].GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray();
}
