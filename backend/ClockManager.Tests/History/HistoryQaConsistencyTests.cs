using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClockManager.Api.Domain;
using ClockManager.Tests.Manager;
using ClockManager.Tests.Persistence;
using NodaTime;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.History;

/// <summary>Helpers shared by the Phase 8 wave-2 area D tests (history and the numbers).</summary>
internal static class HistoryQa
{
    /// <summary>What every endpoint must agree on for one shift.</summary>
    public sealed record Seen(string WorkDate, string In, string? Out, long? Duration, string Flags);

    public static readonly Regex ZurichText = new(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d{1,7})?[+-]\d\d:\d\d$", RegexOptions.CultureInvariant);

    public static Instant I(string iso) => Utc(iso);

    public static string FlagText(JsonElement flags) =>
        string.Join(",", flags.EnumerateArray().Select(f => f.GetString()!).OrderBy(f => f, StringComparer.Ordinal));

    /// <summary>A shift object in the punch / status / history / detail shape.</summary>
    public static Seen FromShift(JsonElement s) => new(
        s.GetProperty("workDate").GetString()!,
        s.GetProperty("clockIn").GetProperty("zurich").GetString()!,
        s.GetProperty("clockOut").ValueKind == JsonValueKind.Null ? null : s.GetProperty("clockOut").GetProperty("zurich").GetString(),
        s.GetProperty("durationSeconds").ValueKind == JsonValueKind.Null ? null : s.GetProperty("durationSeconds").GetInt64(),
        FlagText(s.GetProperty("flags")));

    public static Seen FromAnomaly(JsonElement a) => new(
        a.GetProperty("workDate").GetString()!,
        a.GetProperty("clockIn").GetProperty("zurich").GetString()!,
        a.GetProperty("clockOut").ValueKind == JsonValueKind.Null ? null : a.GetProperty("clockOut").GetProperty("zurich").GetString(),
        a.GetProperty("durationSeconds").ValueKind == JsonValueKind.Null ? null : a.GetProperty("durationSeconds").GetInt64(),
        a.GetProperty("kind").GetString()!);

    public static JsonElement FindInHistory(JsonElement history, long shiftId)
    {
        foreach (var day in history.GetProperty("days").EnumerateArray())
        foreach (var shift in day.GetProperty("shifts").EnumerateArray())
        {
            if (shift.GetProperty("shiftId").GetInt64() == shiftId) return shift;
        }
        throw new Xunit.Sdk.XunitException($"Shift {shiftId} is not in the history body.");
    }

    public static Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string url, string? user) =>
        ManagerTestKit.SendAsync(client, HttpMethod.Get, url, user);

    public static Task<(HttpStatusCode Status, JsonElement Body)> PunchAsync(HttpClient client, string intent, string user) =>
        ManagerTestKit.SendAsync(client, HttpMethod.Post, "/api/punches", user, "{\"intent\":\"" + intent + "\"}", idempotencyKey: Guid.NewGuid().ToString());

    /// <summary>Whole seconds between two instants, truncated (an independent calculation from raw ticks).</summary>
    public static long Seconds(Instant from, Instant to) => (to.ToUnixTimeTicks() - from.ToUnixTimeTicks()) / NodaConstants.TicksPerSecond;

    public static Instant Local(int y, int mo, int d, int h, int mi, int s = 0) =>
        Zurich.Zone.AtStrictly(new LocalDateTime(y, mo, d, h, mi, s)).ToInstant();

    public static string Iso(Instant i) => NodaTime.Text.InstantPattern.ExtendedIso.Format(i);
}

/// <summary>
/// Plans/08-qa.md §2.D item 1 and 5: the SAME shift must read the same on every endpoint that shows it
/// (punch response, status, my history, manager history, manager shift detail, problems list).
/// </summary>
[Trait("Category", "Integration")]
public sealed class HistoryQaConsistencyTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private readonly ManagerTestKit _kit = new(database);

    [SqlServerTheory]
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z")] // T1
    [InlineData("2026-07-06T06:00:00Z", "2026-07-06T14:00:00Z")] // T2
    [InlineData("2026-03-28T21:00:00Z", "2026-03-29T04:00:00Z")] // T4, 7 h over the spring change
    [InlineData("2026-10-24T20:00:00Z", "2026-10-25T05:00:00Z")] // T5, 9 h over the autumn change
    [InlineData("2026-10-25T00:30:00Z", "2026-10-25T01:15:00Z")] // T6, 45 min inside the repeated hour
    [InlineData("2026-12-31T22:00:00Z", "2027-01-01T06:00:00Z")] // T8
    [InlineData("2026-01-12T07:00:00.9000000Z", "2026-01-12T07:00:30.2000000Z")] // sub-second ends: 29.3 s, short
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T07:00:59.9999999Z")] // one tick under 60 s: short
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T07:01:00Z")] // exactly 60 s: not short
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T23:00:00Z")] // exactly 16 h: not long
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T23:00:00.0000001Z")] // one tick over 16 h: long, shown as 57600 s
    [InlineData("2026-01-12T21:00:00Z", "2026-01-14T07:00:00Z")] // two midnights, 34 h: long
    [InlineData("2026-10-24T20:00:00Z", "2026-10-25T12:00:00Z")] // exactly 16 h of UTC across the autumn change (15 h on the wall clock)
    [InlineData("2026-10-24T20:00:00Z", "2026-10-25T12:00:00.0000001Z")] // and one tick more
    public async Task A_closed_shift_reads_the_same_on_every_endpoint(string inUtc, string outUtc)
    {
        var (factory, clock) = _kit.NewHost(inUtc);
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, user) = await _kit.AddEmployeeAsync(displayName: "Consistency " + Guid.NewGuid().ToString("N")[..6]);

        var (inStatus, _) = await HistoryQa.PunchAsync(client, "clock-in", user);
        clock.Set(outUtc);
        var (outStatus, punch) = await HistoryQa.PunchAsync(client, "clock-out", user);
        Assert.Equal(HttpStatusCode.Created, inStatus);
        Assert.Equal(HttpStatusCode.Created, outStatus);
        var shiftId = punch.GetProperty("shiftId").GetInt64();

        var inI = HistoryQa.I(inUtc);
        var outI = HistoryQa.I(outUtc);
        var duration = HistoryQa.Seconds(inI, outI);
        var expectedWorkDate = NodaTime.Text.LocalDatePattern.Iso.Format(Zurich.LocalDate(inI));
        var expectedFlags = new List<string>();
        if (outI - inI > Duration.FromHours(16)) expectedFlags.Add("long_shift");
        if (outI - inI < Duration.FromSeconds(60)) expectedFlags.Add("short_shift");

        var (_, status) = await HistoryQa.GetAsync(client, "/api/me/status", user);
        var (_, mine) = await HistoryQa.GetAsync(client, $"/api/me/shifts?from={expectedWorkDate}&to={expectedWorkDate}", user);
        var (_, theirs) = await HistoryQa.GetAsync(client, $"/api/manager/employees/{empId}/shifts?from={expectedWorkDate}&to={expectedWorkDate}", manager);
        var (_, detail) = await HistoryQa.GetAsync(client, $"/api/manager/shifts/{shiftId}", manager);
        var (_, anomalies) = await HistoryQa.GetAsync(client, "/api/manager/anomalies", manager);

        var seenByPunch = HistoryQa.FromShift(punch);
        var all = new Dictionary<string, HistoryQa.Seen>
        {
            ["status.lastClosedShift"] = HistoryQa.FromShift(status.GetProperty("lastClosedShift")),
            ["me/shifts"] = HistoryQa.FromShift(HistoryQa.FindInHistory(mine, shiftId)),
            ["manager employees/{id}/shifts"] = HistoryQa.FromShift(HistoryQa.FindInHistory(theirs, shiftId)),
            ["manager shifts/{id}"] = HistoryQa.FromShift(detail.GetProperty("shift")),
        };
        foreach (var (name, seen) in all) Assert.True(seenByPunch == seen, $"{name} disagrees with the punch response: {seen} vs {seenByPunch}");

        Assert.Equal(expectedWorkDate, seenByPunch.WorkDate);
        Assert.Equal(duration, seenByPunch.Duration);
        Assert.Equal(string.Join(",", expectedFlags.OrderBy(f => f, StringComparer.Ordinal)), seenByPunch.Flags);
        Assert.Matches(HistoryQa.ZurichText, seenByPunch.In);
        Assert.Matches(HistoryQa.ZurichText, seenByPunch.Out!);

        // endsNextDay (history only) follows the Zurich dates of the two punches.
        var expectedEndsNext = Zurich.LocalDate(outI) > Zurich.LocalDate(inI);
        Assert.Equal(expectedEndsNext, HistoryQa.FindInHistory(mine, shiftId).GetProperty("endsNextDay").GetBoolean());
        Assert.Equal(expectedEndsNext, HistoryQa.FindInHistory(theirs, shiftId).GetProperty("endsNextDay").GetBoolean());
        Assert.Equal(expectedEndsNext, detail.GetProperty("shift").GetProperty("endsNextDay").GetBoolean());

        // The day total of the history is the shift's own duration.
        Assert.Equal(duration, mine.GetProperty("days")[0].GetProperty("totalSeconds").GetInt64());
        Assert.Equal(duration, mine.GetProperty("totalSeconds").GetInt64());

        // The problems list holds the shift exactly when it has a flag, with that flag as its kind.
        var row = anomalies.EnumerateArray().Where(a => a.GetProperty("shiftId").GetInt64() == shiftId).ToArray();
        if (expectedFlags.Count == 0) Assert.Empty(row);
        else
        {
            Assert.Single(row);
            var a = HistoryQa.FromAnomaly(row[0]);
            Assert.Equal(seenByPunch with { Flags = expectedFlags[0] }, a);
        }
    }

    /// <summary>
    /// An open shift and "forgotten": the boundary is strict (exactly 16 h is fine), measured in UTC, and every endpoint that
    /// shows the flag agrees with every other one. Status keeps the flag at the top level (D-036); history and the manager
    /// views put it in the shift's own flags (D-066): that difference is by design and is pinned here.
    /// </summary>
    [SqlServerTheory]
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T22:59:59.9999999Z", false)]
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T23:00:00Z", false)] // exactly 16 h
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T23:00:00.0000001Z", true)] // one tick over
    [InlineData("2026-10-24T20:00:00Z", "2026-10-25T12:00:00Z", false)] // 16 h of UTC over the autumn change, 15 h on the wall clock
    [InlineData("2026-10-24T20:00:00Z", "2026-10-25T12:00:00.0000001Z", true)]
    [InlineData("2026-03-28T20:00:00Z", "2026-03-29T11:59:59.9999999Z", false)] // 15 h 59 over the spring change (wall clock 17 h 59)
    [InlineData("2026-03-28T20:00:00Z", "2026-03-29T12:00:00.0000001Z", true)]
    public async Task An_open_shift_is_forgotten_on_every_endpoint_or_on_none(string inUtc, string nowUtc, bool forgotten)
    {
        var (factory, _) = _kit.NewHost(nowUtc);
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, user) = await _kit.AddEmployeeAsync(displayName: "Open " + Guid.NewGuid().ToString("N")[..6]);
        var shiftId = await _kit.SaveOpenAsync(empId, inUtc);
        var workDate = NodaTime.Text.LocalDatePattern.Iso.Format(Zurich.LocalDate(HistoryQa.I(inUtc)));
        var expected = forgotten ? "forgotten_clock_out" : "";

        var (_, status) = await HistoryQa.GetAsync(client, "/api/me/status", user);
        var (_, mine) = await HistoryQa.GetAsync(client, $"/api/me/shifts?from={workDate}&to={workDate}", user);
        var (_, theirs) = await HistoryQa.GetAsync(client, $"/api/manager/employees/{empId}/shifts?from={workDate}&to={workDate}", manager);
        var (_, detail) = await HistoryQa.GetAsync(client, $"/api/manager/shifts/{shiftId}", manager);
        var (_, list) = await HistoryQa.GetAsync(client, "/api/manager/employees", manager);
        var (_, anomalies) = await HistoryQa.GetAsync(client, "/api/manager/anomalies", manager);

        Assert.Equal(expected, HistoryQa.FlagText(status.GetProperty("flags")));
        Assert.Equal("", HistoryQa.FlagText(status.GetProperty("openShift").GetProperty("flags"))); // D-036: only at the top level
        Assert.Equal(expected, HistoryQa.FromShift(HistoryQa.FindInHistory(mine, shiftId)).Flags);
        Assert.Equal(expected, HistoryQa.FromShift(HistoryQa.FindInHistory(theirs, shiftId)).Flags);
        Assert.Equal(expected, HistoryQa.FromShift(detail.GetProperty("shift")).Flags);
        var row = list.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == empId);
        Assert.Equal(expected, HistoryQa.FlagText(row.GetProperty("flags")));
        Assert.Equal("clocked_in", row.GetProperty("status").GetString());
        Assert.Equal(forgotten ? 1 : 0, anomalies.EnumerateArray().Count(a => a.GetProperty("shiftId").GetInt64() == shiftId));

        // Open: no duration anywhere, in is the same text everywhere, open flag true, never counted.
        var s = HistoryQa.FindInHistory(mine, shiftId);
        Assert.True(s.GetProperty("open").GetBoolean());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("durationSeconds").ValueKind);
        Assert.Equal(0, mine.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(HistoryQa.FromShift(status.GetProperty("openShift")).In, HistoryQa.FromShift(s).In);
        Assert.Equal(HistoryQa.FromShift(s).In, row.GetProperty("since").GetProperty("zurich").GetString());
    }

    [SqlServerFact]
    public async Task Without_trusted_time_no_endpoint_claims_forgotten_and_every_one_still_answers_200()
    {
        var (factory, clock) = _kit.NewHost("2026-02-01T12:00:00Z");
        clock.FailWith(Fakes.FakeTrustedClock.Unavailable());
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, user) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveOpenAsync(empId, "2026-01-01T07:00:00Z"); // a month open

        var (s1, status) = await HistoryQa.GetAsync(client, "/api/me/status", user);
        var (s2, mine) = await HistoryQa.GetAsync(client, "/api/me/shifts", user);
        var (s3, theirs) = await HistoryQa.GetAsync(client, $"/api/manager/employees/{empId}/shifts", manager);
        var (s4, detail) = await HistoryQa.GetAsync(client, $"/api/manager/shifts/{shiftId}", manager);
        var (s5, list) = await HistoryQa.GetAsync(client, "/api/manager/employees", manager);
        var (s6, anomalies) = await HistoryQa.GetAsync(client, "/api/manager/anomalies", manager);

        Assert.All(new[] { s1, s2, s3, s4, s5, s6 }, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(JsonValueKind.Null, status.GetProperty("serverNow").ValueKind);
        Assert.Equal("", HistoryQa.FlagText(status.GetProperty("flags")));
        Assert.Equal("", HistoryQa.FromShift(HistoryQa.FindInHistory(mine, shiftId)).Flags);
        Assert.Equal("", HistoryQa.FromShift(HistoryQa.FindInHistory(theirs, shiftId)).Flags);
        Assert.Equal("", HistoryQa.FromShift(detail.GetProperty("shift")).Flags);
        Assert.Equal("", HistoryQa.FlagText(list.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == empId).GetProperty("flags")));
        Assert.DoesNotContain(anomalies.EnumerateArray(), a => a.GetProperty("shiftId").GetInt64() == shiftId);
    }

    [SqlServerFact]
    public async Task The_manager_long_shift_today_flag_uses_the_Zurich_work_date_not_the_UTC_date()
    {
        var (factory, _) = _kit.NewHost("2026-01-13T20:00:00Z"); // Tuesday 21:00 in Zurich
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (todayId, _) = await _kit.AddEmployeeAsync(displayName: "Today");
        var (yesterdayId, _) = await _kit.AddEmployeeAsync(displayName: "Yesterday");
        // Both clock in on Jan 12 UTC. 23:10Z is 00:10 on Jan 13 in Zurich (today), 22:50Z is 23:50 on Jan 12 (yesterday).
        await _kit.SaveClosedAsync(todayId, "2026-01-12T23:10:00Z", "2026-01-13T16:10:00Z"); // 17 h
        await _kit.SaveClosedAsync(yesterdayId, "2026-01-12T22:50:00Z", "2026-01-13T15:50:00Z"); // 17 h

        var (_, list) = await HistoryQa.GetAsync(client, "/api/manager/employees", manager);

        Assert.Equal("long_shift", HistoryQa.FlagText(list.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == todayId).GetProperty("flags")));
        Assert.Equal("", HistoryQa.FlagText(list.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == yesterdayId).GetProperty("flags")));
    }

    [SqlServerFact]
    public async Task Problems_with_the_same_clock_in_come_out_by_shift_id_descending_every_time()
    {
        var (factory, _) = _kit.NewHost("2026-03-01T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var ids = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var (e, _) = await _kit.AddEmployeeAsync(displayName: "Tie" + i);
            ids.Add(await _kit.SaveClosedAsync(e, "2026-02-02T07:00:00Z", "2026-02-02T07:00:10Z")); // four short shifts, same instants
        }

        List<long>? first = null;
        for (var run = 0; run < 3; run++)
        {
            var (_, list) = await HistoryQa.GetAsync(client, "/api/manager/anomalies", manager);
            var got = list.EnumerateArray().Select(a => a.GetProperty("shiftId").GetInt64()).Where(ids.Contains).ToList();
            Assert.Equal(ids.OrderByDescending(x => x), got);
            first ??= got;
            Assert.Equal(first, got);
        }
    }

    // ---- Corrections and history (item 4) ----

    [SqlServerFact]
    public async Task A_correction_that_moves_the_work_date_into_the_previous_week_moves_the_shift_and_both_week_totals()
    {
        var (factory, _) = _kit.NewHost("2026-11-30T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, user) = await _kit.AddEmployeeAsync();
        // Monday 2026-11-23 08:00-16:00 (8 h), plus a Wednesday shift of 4 h in the same week.
        var mondayId = await _kit.SaveClosedAsync(empId, "2026-11-23T07:00:00Z", "2026-11-23T15:00:00Z");
        await _kit.SaveClosedAsync(empId, "2026-11-25T07:00:00Z", "2026-11-25T11:00:00Z");
        // And a Friday shift in the previous week (2026-11-16..22) of 2 h.
        await _kit.SaveClosedAsync(empId, "2026-11-20T07:00:00Z", "2026-11-20T09:00:00Z");

        var (_, before) = await HistoryQa.GetAsync(client, $"/api/manager/shifts/{mondayId}", manager);
        // The manager says it began on Sunday 2026-11-22 at 22:00 (+01:00): 18 h long, belongs to the previous week.
        var (status, corrected) = await CorrectAsync(client, manager, mondayId, "clockIn", "2026-11-22T22:00:00+01:00",
            before.GetProperty("shift").GetProperty("rowVersion").GetString()!);
        Assert.Equal(HttpStatusCode.Created, status);

        var (_, week47) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-11-16&to=2026-11-22", user);
        var (_, week48) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-11-23&to=2026-11-29", user);
        var (_, mgrWeek47) = await HistoryQa.GetAsync(client, $"/api/manager/employees/{empId}/shifts?from=2026-11-16&to=2026-11-22", manager);
        var (_, mgrWeek48) = await HistoryQa.GetAsync(client, $"/api/manager/employees/{empId}/shifts?from=2026-11-23&to=2026-11-29", manager);

        Assert.Equal(2 * 3600 + 18 * 3600, week47.GetProperty("totalSeconds").GetInt64()); // Friday 2 h + the corrected 18 h
        Assert.Equal(4 * 3600, week48.GetProperty("totalSeconds").GetInt64()); // only the Wednesday shift is left
        Assert.Equal(week47.GetProperty("totalSeconds").GetInt64(), mgrWeek47.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(week48.GetProperty("totalSeconds").GetInt64(), mgrWeek48.GetProperty("totalSeconds").GetInt64());

        var moved = HistoryQa.FindInHistory(week47, mondayId);
        Assert.Equal("2026-11-22", moved.GetProperty("workDate").GetString());
        Assert.True(moved.GetProperty("corrected").GetBoolean());
        Assert.True(moved.GetProperty("endsNextDay").GetBoolean());
        Assert.True(HistoryQa.FindInHistory(mgrWeek47, mondayId).GetProperty("corrected").GetBoolean());
        Assert.True(corrected.GetProperty("shift").GetProperty("corrected").GetBoolean());
        Assert.Equal("long_shift", HistoryQa.FlagText(moved.GetProperty("flags"))); // 18 h now
        Assert.Equal(HistoryQa.FromShift(moved), HistoryQa.FromShift(corrected.GetProperty("shift")));
        Assert.Equal(HistoryQa.FromShift(moved), HistoryQa.FromShift(HistoryQa.FindInHistory(mgrWeek47, mondayId)));
        // It is no longer in the old week's day list.
        Assert.DoesNotContain(week48.GetProperty("days").EnumerateArray(), d => d.GetProperty("date").GetString() == "2026-11-23");

        // Status's last closed shift is by clock-out, so it is the Wednesday one; the problems list now has the 17 h shift.
        var (_, anomalies) = await HistoryQa.GetAsync(client, "/api/manager/anomalies", manager);
        var row = anomalies.EnumerateArray().Single(a => a.GetProperty("shiftId").GetInt64() == mondayId);
        Assert.Equal("long_shift", row.GetProperty("kind").GetString());
        Assert.Equal(18 * 3600, row.GetProperty("durationSeconds").GetInt64());
        Assert.Equal("2026-11-22", row.GetProperty("workDate").GetString());
    }

    [SqlServerFact]
    public async Task A_forgotten_open_shift_closed_by_a_manager_is_closed_counted_and_clean_everywhere()
    {
        var (factory, _) = _kit.NewHost("2026-11-18T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, user) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveOpenAsync(empId, "2026-11-10T07:00:00Z");
        var (_, before) = await HistoryQa.GetAsync(client, $"/api/manager/shifts/{shiftId}", manager);
        Assert.Equal("forgotten_clock_out", HistoryQa.FlagText(before.GetProperty("shift").GetProperty("flags")));

        var (status, _) = await CorrectAsync(client, manager, shiftId, "clockOut", "2026-11-10T16:00:00+01:00",
            before.GetProperty("shift").GetProperty("rowVersion").GetString()!);
        Assert.Equal(HttpStatusCode.Created, status);

        var (_, st) = await HistoryQa.GetAsync(client, "/api/me/status", user);
        var (_, mine) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-11-09&to=2026-11-15", user);
        var (_, list) = await HistoryQa.GetAsync(client, "/api/manager/employees", manager);
        var (_, anomalies) = await HistoryQa.GetAsync(client, "/api/manager/anomalies", manager);

        Assert.Equal(JsonValueKind.Null, st.GetProperty("openShift").ValueKind);
        Assert.Equal("", HistoryQa.FlagText(st.GetProperty("flags")));
        var last = HistoryQa.FromShift(st.GetProperty("lastClosedShift"));
        var inHistory = HistoryQa.FromShift(HistoryQa.FindInHistory(mine, shiftId));
        Assert.Equal(inHistory, last);
        Assert.Equal(8 * 3600, last.Duration); // 16:00+01:00 is 15:00Z, 8 h after 07:00Z
        Assert.Equal(8 * 3600, mine.GetProperty("totalSeconds").GetInt64());
        Assert.True(HistoryQa.FindInHistory(mine, shiftId).GetProperty("corrected").GetBoolean());
        Assert.False(HistoryQa.FindInHistory(mine, shiftId).GetProperty("open").GetBoolean());
        var row = list.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == empId);
        Assert.Equal("clocked_out", row.GetProperty("status").GetString());
        Assert.Equal("", HistoryQa.FlagText(row.GetProperty("flags")));
        Assert.DoesNotContain(anomalies.EnumerateArray(), a => a.GetProperty("shiftId").GetInt64() == shiftId);
    }

    [SqlServerFact]
    public async Task A_correction_of_the_clock_out_into_the_repeated_hour_is_counted_from_UTC_everywhere()
    {
        var (factory, _) = _kit.NewHost("2026-10-26T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, user) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveClosedAsync(empId, "2026-10-25T00:10:00Z", "2026-10-25T00:50:00Z"); // 02:10 to 02:50 (+02:00)
        var (_, before) = await HistoryQa.GetAsync(client, $"/api/manager/shifts/{shiftId}", manager);

        // 02:50 in the SECOND pass of the hour (+01:00) is 01:50Z: the shift becomes 100 minutes, not 40 and not -20.
        var (status, _) = await CorrectAsync(client, manager, shiftId, "clockOut", "2026-10-25T02:50:00+01:00",
            before.GetProperty("shift").GetProperty("rowVersion").GetString()!);
        Assert.Equal(HttpStatusCode.Created, status);

        var (_, mine) = await HistoryQa.GetAsync(client, "/api/me/shifts?from=2026-10-25&to=2026-10-25", user);
        var s = HistoryQa.FromShift(HistoryQa.FindInHistory(mine, shiftId));
        Assert.Equal(6000, s.Duration);
        Assert.Equal("2026-10-25T02:10:00+02:00", s.In);
        Assert.Equal("2026-10-25T02:50:00+01:00", s.Out);
        Assert.Equal(6000, mine.GetProperty("totalSeconds").GetInt64());
        Assert.Equal(["dst_change"], mine.GetProperty("days")[0].GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
    }

    private static Task<(HttpStatusCode Status, JsonElement Body)> CorrectAsync(
        HttpClient client, string manager, long shiftId, string field, string newTime, string rowVersion) =>
        ManagerTestKit.CorrectAsync(client, manager, shiftId, field, newTime, rowVersion);

    // ---- Isolation, quickly (item 6; wave 1 has the rest) ----

    [SqlServerFact]
    public async Task Another_employees_open_shift_never_shows_in_my_status_or_history_and_an_inactive_employee_is_403()
    {
        var (factory, _) = _kit.NewHost("2026-01-20T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (aId, a) = await _kit.AddEmployeeAsync();
        var (bId, b) = await _kit.AddEmployeeAsync();
        await _kit.SaveOpenAsync(aId, "2026-01-05T07:00:00Z"); // a's forgotten shift
        await _kit.SaveClosedAsync(bId, "2026-01-19T07:00:00Z", "2026-01-19T15:00:00Z");

        var (_, bStatus) = await HistoryQa.GetAsync(client, "/api/me/status", b);
        var (_, bMine) = await HistoryQa.GetAsync(client, "/api/me/shifts", b);
        Assert.Equal(JsonValueKind.Null, bStatus.GetProperty("openShift").ValueKind);
        Assert.Equal("", HistoryQa.FlagText(bStatus.GetProperty("flags")));
        Assert.Equal(1, bMine.GetProperty("days").GetArrayLength());
        Assert.Equal("2026-01-19", bMine.GetProperty("days")[0].GetProperty("date").GetString());

        // The manager sees a's open shift only on a's own pages; the employee list keeps the two apart.
        var (_, list) = await HistoryQa.GetAsync(client, "/api/manager/employees", manager);
        Assert.Equal("clocked_in", list.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == aId).GetProperty("status").GetString());
        Assert.Equal("clocked_out", list.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == bId).GetProperty("status").GetString());

        await _kit.DeactivateAsync(bId);
        var (inactiveStatus, problem) = await HistoryQa.GetAsync(client, "/api/me/shifts", b);
        Assert.Equal(HttpStatusCode.Forbidden, inactiveStatus);
        Assert.Equal("employee.inactive", problem.GetProperty("code").GetString());
        // The manager can still read an inactive employee's history (they are records).
        var (mgr, _) = await HistoryQa.GetAsync(client, $"/api/manager/employees/{bId}/shifts", manager);
        Assert.Equal(HttpStatusCode.OK, mgr);
    }
}
