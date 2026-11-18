using System.Net;
using System.Text.Json;
using ClockManager.Api.Application.Manager;
using ClockManager.Api.Domain;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using static ClockManager.Tests.Manager.ManagerTestKit;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Manager;

/// <summary>
/// Manager endpoints through the real host and SQL Server (plans/07-manager.md §5, §6). The database is shared by the
/// class, so list assertions filter by the test's own employees.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ManagerApiTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private readonly ManagerTestKit _kit = new(database);

    // ---- Who ----

    [SqlServerFact]
    public async Task Every_manager_endpoint_is_401_anonymous_403_for_an_employee_and_open_to_a_manager()
    {
        var (factory, _) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, employee) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveClosedAsync(empId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");
        var rv = await _kit.RowVersionAsync(shiftId);
        var correction = new { field = "clockOut", newTime = "2026-01-12T16:00:00+01:00", reason = "Left at four", rowVersion = rv };
        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, "/api/manager/employees", null),
            (HttpMethod.Get, "/api/manager/anomalies", null),
            (HttpMethod.Get, $"/api/manager/employees/{empId}/shifts", null),
            (HttpMethod.Get, $"/api/manager/shifts/{shiftId}", null),
            (HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections", correction),
        };

        foreach (var (method, url, body) in calls)
        {
            var (anonymous, p1) = await SendAsync(client, method, url, null, body);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous);
            Assert.Equal("auth.unauthenticated", Code(p1));
            var (forbidden, p2) = await SendAsync(client, method, url, employee, body);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden);
            Assert.Equal("auth.forbidden", Code(p2));
        }
        Assert.Empty(await _kit.CorrectionsAsync(shiftId));

        foreach (var (method, url, body) in calls)
        {
            var (status, _) = await SendAsync(client, method, url, manager, body);
            Assert.Equal(method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK, status);
        }
    }

    [SqlServerFact]
    public async Task The_database_role_wins_over_the_role_in_the_token()
    {
        var (factory, _) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (demotedId, _) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (promotedId, _) = await _kit.AddEmployeeAsync();
        var managerToken = CookieFor(factory, demotedId, EmployeeRole.Manager);
        var employeeToken = CookieFor(factory, promotedId, EmployeeRole.Employee);
        var shiftId = await _kit.SaveClosedAsync(promotedId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");

        await _kit.SetRoleAsync(demotedId, EmployeeRole.Employee);
        await _kit.SetRoleAsync(promotedId, EmployeeRole.Manager);

        var (s1, p1) = await SendAsync(client, HttpMethod.Get, "/api/manager/employees", null, cookie: managerToken);
        var body = new { field = "clockOut", newTime = "2026-01-12T16:00:00+01:00", reason = "Left at four", rowVersion = await _kit.RowVersionAsync(shiftId) };
        var (s2, _) = await SendAsync(client, HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections", null, body, cookie: managerToken);
        var (s3, _) = await SendAsync(client, HttpMethod.Get, "/api/manager/employees", null, cookie: employeeToken);

        Assert.Equal(HttpStatusCode.Forbidden, s1);
        Assert.Equal("auth.forbidden", Code(p1));
        Assert.Equal(HttpStatusCode.Forbidden, s2);
        Assert.Empty(await _kit.CorrectionsAsync(shiftId));
        Assert.Equal(HttpStatusCode.OK, s3);
    }

    // ---- Lists ----

    [SqlServerFact]
    public async Task Employees_list_shows_status_since_flags_and_inactive_employees()
    {
        var (withTime, _) = _kit.NewHost("2026-01-13T07:00:00Z"); // 24 h after the open clock-in
        var (withoutTime, broken) = _kit.NewHost("2026-01-13T07:00:00Z");
        broken.FailWith(FakeTrustedClock.Unavailable());
        await using var _f1 = withTime;
        await using var _f2 = withoutTime;
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (inId, _) = await _kit.AddEmployeeAsync();
        var (outId, _) = await _kit.AddEmployeeAsync();
        await _kit.SaveOpenAsync(inId, "2026-01-12T07:00:00Z");
        await _kit.SaveClosedAsync(outId, "2026-01-11T07:00:00Z", "2026-01-11T15:00:00Z");
        await _kit.DeactivateAsync(outId);

        using var c1 = withTime.CreateClient();
        var (status, list) = await SendAsync(c1, HttpMethod.Get, "/api/manager/employees", manager);
        using var c2 = withoutTime.CreateClient();
        var (status2, list2) = await SendAsync(c2, HttpMethod.Get, "/api/manager/employees", manager);

        Assert.Equal(HttpStatusCode.OK, status);
        var clockedIn = Row(list, inId);
        Assert.Equal(["id", "username", "displayName", "role", "isActive", "status", "since", "flags"], clockedIn.EnumerateObject().Select(p => p.Name));
        Assert.Equal("clocked_in", clockedIn.GetProperty("status").GetString());
        Assert.Equal("2026-01-12T08:00:00+01:00", Zurich(clockedIn.GetProperty("since")));
        Assert.Equal(["forgotten_clock_out"], clockedIn.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal("Employee", clockedIn.GetProperty("role").GetString());

        var inactive = Row(list, outId);
        Assert.False(inactive.GetProperty("isActive").GetBoolean());
        Assert.Equal("clocked_out", inactive.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, inactive.GetProperty("since").ValueKind);

        Assert.Equal(HttpStatusCode.OK, status2); // no trusted time: still a list, no time-based flags
        Assert.Equal("clocked_in", Row(list2, inId).GetProperty("status").GetString());
        Assert.Equal(0, Row(list2, inId).GetProperty("flags").GetArrayLength());
    }

    [SqlServerFact]
    public async Task Anomalies_list_forgotten_only_with_trusted_time_plus_long_and_short_newest_first()
    {
        var (withTime, _) = _kit.NewHost("2026-01-20T07:00:00Z");
        var (withoutTime, broken) = _kit.NewHost("2026-01-20T07:00:00Z");
        broken.FailWith(FakeTrustedClock.Unavailable());
        await using var _f1 = withTime;
        await using var _f2 = withoutTime;
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (a, _) = await _kit.AddEmployeeAsync(displayName: "Anna");
        var (b, _) = await _kit.AddEmployeeAsync(displayName: "Beat");
        var longId = await _kit.SaveClosedAsync(a, "2026-01-12T05:00:00Z", "2026-01-12T22:00:00Z"); // 17 h
        var shortId = await _kit.SaveClosedAsync(a, "2026-01-13T07:00:00Z", "2026-01-13T07:00:30Z"); // 30 s
        await _kit.SaveClosedAsync(a, "2026-01-14T07:00:00Z", "2026-01-14T23:00:00Z"); // exactly 16 h: not long
        await _kit.SaveClosedAsync(a, "2026-01-15T07:00:00Z", "2026-01-15T07:01:00Z"); // exactly 60 s: not short
        var forgottenId = await _kit.SaveOpenAsync(b, "2026-01-16T07:00:00Z"); // 4 days open
        var mine = new HashSet<int> { a, b };

        using var c1 = withTime.CreateClient();
        var (status, list) = await SendAsync(c1, HttpMethod.Get, "/api/manager/anomalies", manager);
        using var c2 = withoutTime.CreateClient();
        var (_, list2) = await SendAsync(c2, HttpMethod.Get, "/api/manager/anomalies", manager);

        Assert.Equal(HttpStatusCode.OK, status);
        var rows = list.EnumerateArray().Where(r => mine.Contains(r.GetProperty("employeeId").GetInt32())).ToArray();
        Assert.Equal([forgottenId, shortId, longId], rows.Select(r => r.GetProperty("shiftId").GetInt64()));
        Assert.Equal(["forgotten_clock_out", "short_shift", "long_shift"], rows.Select(r => r.GetProperty("kind").GetString()));
        Assert.Equal(["shiftId", "employeeId", "displayName", "workDate", "kind", "clockIn", "clockOut", "durationSeconds"],
            rows[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("Beat", rows[0].GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("clockOut").ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("durationSeconds").ValueKind);
        Assert.Equal(61200, rows[2].GetProperty("durationSeconds").GetInt64());
        Assert.Equal("2026-01-12", rows[2].GetProperty("workDate").GetString());
        Assert.Equal("2026-01-12T23:00:00+01:00", Zurich(rows[2].GetProperty("clockOut")));

        var rows2 = list2.EnumerateArray().Where(r => mine.Contains(r.GetProperty("employeeId").GetInt32()));
        Assert.Equal([shortId, longId], rows2.Select(r => r.GetProperty("shiftId").GetInt64()));
    }

    [SqlServerFact]
    public async Task Employee_shifts_carry_row_version_and_corrected_and_me_shifts_carry_corrected_only()
    {
        var (factory, _) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, employee) = await _kit.AddEmployeeAsync();
        var fixedId = await _kit.SaveClosedAsync(empId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z"); // T1
        await _kit.SaveClosedAsync(empId, "2026-01-13T07:00:00Z", "2026-01-13T08:00:00Z");
        var rv = await _kit.RowVersionAsync(fixedId);

        var (s0, before) = await SendAsync(client, HttpMethod.Get, $"/api/manager/employees/{empId}/shifts?from=2026-01-12&to=2026-01-18", manager);
        Assert.Equal(HttpStatusCode.OK, s0);
        Assert.Equal(["from", "to", "days", "totalSeconds", "employee"], before.EnumerateObject().Select(p => p.Name));
        Assert.Equal(empId, before.GetProperty("employee").GetProperty("id").GetInt32());
        Assert.Equal("Test Employee", before.GetProperty("employee").GetProperty("displayName").GetString());
        var first = before.GetProperty("days")[0].GetProperty("shifts")[0];
        Assert.Equal(["shiftId", "workDate", "clockIn", "clockOut", "durationSeconds", "endsNextDay", "open", "flags", "corrected", "rowVersion"],
            first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(rv, first.GetProperty("rowVersion").GetString());
        Assert.False(first.GetProperty("corrected").GetBoolean());

        var (created, _) = await CorrectAsync(client, manager, fixedId, "clockOut", "2026-01-12T17:00:00+01:00", rv);
        Assert.Equal(HttpStatusCode.Created, created);

        var (_, after) = await SendAsync(client, HttpMethod.Get, $"/api/manager/employees/{empId}/shifts?from=2026-01-12&to=2026-01-18", manager);
        var (_, mine) = await SendAsync(client, HttpMethod.Get, "/api/me/shifts?from=2026-01-12&to=2026-01-18", employee);
        var corrected = after.GetProperty("days")[0].GetProperty("shifts")[0];
        Assert.True(corrected.GetProperty("corrected").GetBoolean());
        Assert.NotEqual(rv, corrected.GetProperty("rowVersion").GetString());
        Assert.False(after.GetProperty("days")[1].GetProperty("shifts")[0].GetProperty("corrected").GetBoolean());

        var own = mine.GetProperty("days")[0].GetProperty("shifts")[0];
        Assert.True(own.GetProperty("corrected").GetBoolean());
        Assert.False(own.TryGetProperty("rowVersion", out _)); // employees never get the row version
        Assert.Equal(32400, own.GetProperty("durationSeconds").GetInt64()); // totals use the corrected time: 08:00 to 17:00
        Assert.Equal(32400 + 3600, mine.GetProperty("totalSeconds").GetInt64());

        var (missing, problem) = await SendAsync(client, HttpMethod.Get, "/api/manager/employees/2147483000/shifts", manager);
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("request.not_found", Code(problem));
        var (badRange, _) = await SendAsync(client, HttpMethod.Get, $"/api/manager/employees/{empId}/shifts?from=2026-01-12", manager);
        Assert.Equal(HttpStatusCode.BadRequest, badRange);
    }

    // ---- Corrections: happy paths ----

    [SqlServerFact]
    public async Task Fixing_a_clock_out_updates_the_shift_and_writes_the_audit_row()
    {
        var (factory, _) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (managerId, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager, "Maria Manager");
        var (empId, _) = await _kit.AddEmployeeAsync(displayName: "Eva Employee");
        var shiftId = await _kit.SaveClosedAsync(empId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");

        var (status, body) = await CorrectAsync(client, manager, shiftId, "clockOut", "2026-01-12T17:12:00+01:00",
            await _kit.RowVersionAsync(shiftId), "  Forgot to clock out at the end  ");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(["shift", "employee", "corrections"], body.EnumerateObject().Select(p => p.Name));
        var shift = body.GetProperty("shift");
        Assert.Equal("2026-01-12T17:12:00+01:00", Zurich(shift.GetProperty("clockOut")));
        Assert.Equal(33120, shift.GetProperty("durationSeconds").GetInt64());
        Assert.True(shift.GetProperty("corrected").GetBoolean());
        Assert.Equal(await _kit.RowVersionAsync(shiftId), shift.GetProperty("rowVersion").GetString());
        Assert.Equal(empId, body.GetProperty("employee").GetProperty("id").GetInt32());
        Assert.Equal("Eva Employee", body.GetProperty("employee").GetProperty("displayName").GetString());
        var row = Assert.Single(body.GetProperty("corrections").EnumerateArray());
        Assert.Equal(["id", "field", "old", "new", "reason", "by", "at"], row.EnumerateObject().Select(p => p.Name));
        Assert.Equal("clockOut", row.GetProperty("field").GetString());
        Assert.Equal("2026-01-12T16:30:00+01:00", Zurich(row.GetProperty("old")));
        Assert.Equal("2026-01-12T17:12:00+01:00", Zurich(row.GetProperty("new")));
        Assert.Equal("Forgot to clock out at the end", row.GetProperty("reason").GetString());
        Assert.Equal(managerId, row.GetProperty("by").GetProperty("id").GetInt32());
        Assert.Equal("Maria Manager", row.GetProperty("by").GetProperty("displayName").GetString());
        Assert.Equal("2026-01-13T08:00:00+01:00", Zurich(row.GetProperty("at")));

        var stored = await _kit.LoadShiftAsync(shiftId);
        Assert.Equal(Utc("2026-01-12T16:12:00Z"), stored.ClockOut!.Utc);
        Assert.Equal("manager_correction", stored.ClockOut.Provider);
        Assert.Equal($"corrected by manager #{managerId}", stored.ClockOut.RawPayload);
        Assert.Equal(0, stored.ClockOut.RoundTripMs);
        Assert.Null(stored.ClockOut.CrossCheckedBy);
        Assert.Equal("time.now", stored.ClockIn.Provider); // the other punch is untouched

        var audit = Assert.Single(await _kit.CorrectionsAsync(shiftId));
        Assert.Equal(ShiftCorrectionField.ClockOut, audit.Field);
        Assert.Equal(Utc("2026-01-12T15:30:00Z"), audit.OldUtc);
        Assert.Equal(Utc("2026-01-12T16:12:00Z"), audit.NewUtc);
        Assert.Equal(managerId, audit.ManagerId);
        Assert.Equal(Utc("2026-01-13T07:00:00Z"), audit.CreatedUtc); // the trusted instant
        Assert.Equal(FakeTrustedClock.ProviderName, audit.CreatedProvider);
    }

    [SqlServerFact]
    public async Task Moving_a_clock_in_to_the_previous_day_moves_the_work_date()
    {
        var (factory, _) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, employee) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveClosedAsync(empId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");

        var (status, body) = await CorrectAsync(client, manager, shiftId, "clockIn", "2026-01-11T23:30:00+01:00", await _kit.RowVersionAsync(shiftId));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("2026-01-11", body.GetProperty("shift").GetProperty("workDate").GetString());
        Assert.True(body.GetProperty("shift").GetProperty("endsNextDay").GetBoolean());
        var stored = await _kit.LoadShiftAsync(shiftId);
        Assert.Equal(new NodaTime.LocalDate(2026, 1, 11), stored.ClockIn.ZurichDate);
        Assert.Equal(NodaTime.Offset.FromHours(1), stored.ClockIn.ZurichOffset);

        var (_, history) = await SendAsync(client, HttpMethod.Get, "/api/me/shifts?from=2026-01-11&to=2026-01-11", employee);
        Assert.Equal(61200, history.GetProperty("totalSeconds").GetInt64()); // 23:30 to 16:30 = 17 h
    }

    [SqlServerFact]
    public async Task Closing_a_forgotten_shift_lets_the_employee_clock_in_again()
    {
        var (factory, _) = _kit.NewHost("2026-01-13T07:00:00Z"); // T9: open 24 h
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, employee) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveOpenAsync(empId, "2026-01-12T07:00:00Z");

        var (status, body) = await CorrectAsync(client, manager, shiftId, "clockOut", "2026-01-12T16:00:00+01:00", await _kit.RowVersionAsync(shiftId));
        var (punch, _) = await SendAsync(client, HttpMethod.Post, "/api/punches", employee, """{"intent":"clock-in"}""",
            idempotencyKey: Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.False(body.GetProperty("shift").GetProperty("open").GetBoolean());
        var audit = Assert.Single(await _kit.CorrectionsAsync(shiftId));
        Assert.Null(audit.OldUtc); // closed an open shift: no previous value
        Assert.Equal(HttpStatusCode.Created, punch);
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, empId, openOnly: true));
    }

    [SqlServerFact]
    public async Task The_repeated_October_hour_is_accepted_with_each_matching_offset()
    {
        var (factory, _) = _kit.NewHost("2026-10-26T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, _) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveClosedAsync(empId, "2026-10-25T00:00:00Z", "2026-10-25T03:00:00Z");

        var (s1, _) = await CorrectAsync(client, manager, shiftId, "clockOut", "2026-10-25T02:30:00+01:00", await _kit.RowVersionAsync(shiftId));
        var (s2, body) = await CorrectAsync(client, manager, shiftId, "clockIn", "2026-10-25T02:30:00+02:00", await _kit.RowVersionAsync(shiftId));
        var (gap, problem) = await CorrectAsync(client, manager, shiftId, "clockIn", "2026-03-29T02:30:00+01:00", await _kit.RowVersionAsync(shiftId));

        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal(HttpStatusCode.Created, s2);
        var shift = body.GetProperty("shift");
        Assert.Equal("2026-10-25T02:30:00+02:00", Zurich(shift.GetProperty("clockIn"))); // T6: wall clock looks backwards
        Assert.Equal("2026-10-25T02:30:00+01:00", Zurich(shift.GetProperty("clockOut")));
        Assert.Equal(3600, shift.GetProperty("durationSeconds").GetInt64());
        Assert.Equal(HttpStatusCode.BadRequest, gap); // T12
        Assert.Equal(CorrectionInput.OffsetMessage, problem.GetProperty("errors").GetProperty("newTime")[0].GetString());
    }

    [SqlServerFact]
    public async Task A_manager_may_correct_their_own_shift_and_it_is_audited()
    {
        var (factory, _) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (managerId, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var shiftId = await _kit.SaveClosedAsync(managerId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");

        var (status, _) = await CorrectAsync(client, manager, shiftId, "clockIn", "2026-01-12T07:30:00+01:00", await _kit.RowVersionAsync(shiftId));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(managerId, Assert.Single(await _kit.CorrectionsAsync(shiftId)).ManagerId);
    }

    [SqlServerFact]
    public async Task Two_corrections_of_the_same_field_keep_the_original_reconstructable()
    {
        var (factory, clock) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, _) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveClosedAsync(empId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");

        await CorrectAsync(client, manager, shiftId, "clockOut", "2026-01-12T17:00:00+01:00", await _kit.RowVersionAsync(shiftId), "First fix");
        var firstRow = Assert.Single(await _kit.CorrectionsAsync(shiftId));
        clock.Set("2026-01-13T08:00:00Z");
        var (status, body) = await CorrectAsync(client, manager, shiftId, "clockOut", "2026-01-12T18:00:00+01:00", await _kit.RowVersionAsync(shiftId), "Second fix");

        Assert.Equal(HttpStatusCode.Created, status);
        var rows = await _kit.CorrectionsAsync(shiftId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(firstRow.OldUtc, rows[0].OldUtc); // the first row never changes
        Assert.Equal(firstRow.NewUtc, rows[0].NewUtc);
        Assert.Equal(firstRow.Reason, rows[0].Reason);
        Assert.Equal(Utc("2026-01-12T15:30:00Z"), rows[0].OldUtc); // the original punch
        Assert.Equal(rows[0].NewUtc, rows[1].OldUtc);
        Assert.Equal(Utc("2026-01-12T17:00:00Z"), rows[1].NewUtc);
        Assert.Equal(Utc("2026-01-13T08:00:00Z"), rows[1].CreatedUtc);
        Assert.Equal(["First fix", "Second fix"], body.GetProperty("corrections").EnumerateArray().Select(c => c.GetProperty("reason").GetString()));
    }

    // ---- Corrections: refusals (nothing saved) ----

    [SqlServerFact]
    public async Task Rule_violations_get_their_codes_and_save_nothing()
    {
        var (factory, _) = _kit.NewHost("2026-01-14T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, _) = await _kit.AddEmployeeAsync();
        var previous = await _kit.SaveClosedAsync(empId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");
        var middle = await _kit.SaveClosedAsync(empId, "2026-01-13T07:00:00Z", "2026-01-13T15:00:00Z");
        var next = await _kit.SaveClosedAsync(empId, "2026-01-13T17:00:00Z", "2026-01-13T20:00:00Z");
        var running = await _kit.SaveOpenAsync(empId, "2026-01-14T07:00:00Z"); // open 5 h: may still be running
        var rv = await _kit.RowVersionAsync(middle);

        async Task Expect(long shiftId, string field, string newTime, HttpStatusCode status, string code, string? rowVersion = null)
        {
            var (s, p) = await CorrectAsync(client, manager, shiftId, field, newTime, rowVersion ?? await _kit.RowVersionAsync(shiftId));
            Assert.Equal(status, s);
            Assert.Equal(code, Code(p));
        }

        await Expect(running, "clockOut", "2026-01-14T10:00:00+01:00", HttpStatusCode.Conflict, "correction.shift_still_running");
        await Expect(middle, "clockOut", "2026-01-14T14:00:00+01:00", HttpStatusCode.UnprocessableEntity, "correction.in_future");
        await Expect(middle, "clockOut", "2026-01-13T07:00:00+01:00", HttpStatusCode.UnprocessableEntity, "correction.invalid_order");
        await Expect(middle, "clockIn", "2026-01-13T16:00:00+01:00", HttpStatusCode.UnprocessableEntity, "correction.invalid_order");
        await Expect(middle, "clockOut", "2026-01-13T16:00:00+01:00", HttpStatusCode.UnprocessableEntity, "correction.unchanged");
        await Expect(middle, "clockIn", "2026-01-12T16:30:00+01:00", HttpStatusCode.Conflict, "correction.overlap"); // touches previous
        await Expect(middle, "clockIn", "2026-01-12T16:29:59+01:00", HttpStatusCode.Conflict, "correction.overlap"); // into previous
        await Expect(middle, "clockOut", "2026-01-13T18:00:00+01:00", HttpStatusCode.Conflict, "correction.overlap"); // touches next
        await Expect(next, "clockOut", "2026-01-14T09:00:00+01:00", HttpStatusCode.Conflict, "correction.overlap"); // into the running shift
        await Expect(middle, "clockOut", "2026-01-13T17:00:00+01:00", HttpStatusCode.Conflict, "correction.stale", "AAAAAAAAAAE=");
        var (missing, p404) = await CorrectAsync(client, manager, 9_000_000_000, "clockOut", "2026-01-13T17:00:00+01:00", rv);
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("request.not_found", Code(p404));

        var (_, overlap) = await CorrectAsync(client, manager, middle, "clockIn", "2026-01-12T16:00:00+01:00", rv);
        Assert.Equal(previous, overlap.GetProperty("conflictingShiftId").GetInt64());
        foreach (var id in new[] { previous, middle, next, running }) Assert.Empty(await _kit.CorrectionsAsync(id));
        Assert.Equal(rv, await _kit.RowVersionAsync(middle));
    }

    [SqlServerFact]
    public async Task Two_managers_with_the_same_row_version_one_wins_the_other_gets_stale()
    {
        var (factory, _) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, m1) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (_, m2) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, _) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveClosedAsync(empId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");
        var rv = await _kit.RowVersionAsync(shiftId);

        var results = await Task.WhenAll(
            CorrectAsync(client, m1, shiftId, "clockOut", "2026-01-12T17:00:00+01:00", rv),
            CorrectAsync(client, m2, shiftId, "clockOut", "2026-01-12T18:00:00+01:00", rv));

        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        var loser = Assert.Single(results, r => r.Status != HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, loser.Status);
        Assert.Equal("correction.stale", Code(loser.Body));
        Assert.Single(await _kit.CorrectionsAsync(shiftId));
    }

    [SqlServerFact]
    public async Task Invalid_bodies_are_400_with_fixed_messages_and_never_ask_the_clock()
    {
        var (factory, clock) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, _) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveClosedAsync(empId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");
        var rv = await _kit.RowVersionAsync(shiftId);
        var callsBefore = clock.Calls;

        foreach (var reason in new[] { "    ", "abc", new string('x', 501) })
        {
            var (status, problem) = await CorrectAsync(client, manager, shiftId, "clockOut", "2026-01-12T17:00:00+01:00", rv, reason);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("request.invalid", Code(problem));
            Assert.Equal(CorrectionInput.ReasonMessage, problem.GetProperty("errors").GetProperty("reason")[0].GetString());
        }
        var (s2, p2) = await SendAsync(client, HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections", manager,
            new { field = "start", newTime = "<script>", reason = "Valid reason", rowVersion = rv });
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Equal(["field", "newTime"], p2.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)); // camelCase keys
        Assert.Equal(CorrectionInput.FieldMessage, p2.GetProperty("errors").GetProperty("field")[0].GetString());
        Assert.Equal(CorrectionInput.NewTimeMessage, p2.GetProperty("errors").GetProperty("newTime")[0].GetString());
        var (s3, _) = await SendAsync(client, HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections", manager, "[]");
        Assert.Equal(HttpStatusCode.BadRequest, s3);

        Assert.Equal(callsBefore, clock.Calls);
        Assert.Empty(await _kit.CorrectionsAsync(shiftId));
    }

    [SqlServerFact]
    public async Task Without_trusted_time_a_correction_is_503_and_nothing_changes()
    {
        var (factory, clock) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, _) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveOpenAsync(empId, "2026-01-12T07:00:00Z");
        var rv = await _kit.RowVersionAsync(shiftId);
        clock.FailWith(FakeTrustedClock.Unavailable());

        var (status, problem) = await CorrectAsync(client, manager, shiftId, "clockOut", "2026-01-12T16:00:00+01:00", rv);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("time.unavailable", Code(problem));
        Assert.Empty(await _kit.CorrectionsAsync(shiftId));
        Assert.Equal(rv, await _kit.RowVersionAsync(shiftId));
        Assert.True((await _kit.LoadShiftAsync(shiftId)).IsOpen);
    }

    // ---- Races with the employee's own punch ----

    [SqlServerFact]
    public async Task A_correction_committing_during_the_employees_clock_out_wins_and_the_punch_gets_409()
    {
        var (employeeHost, employeeClock) = _kit.NewHost("2026-01-13T07:00:00Z");
        var (managerHost, _) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f1 = employeeHost;
        await using var _f2 = managerHost;
        using var employeeClient = employeeHost.CreateClient();
        using var managerClient = managerHost.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, employee) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveOpenAsync(empId, "2026-01-12T07:00:00Z"); // forgotten

        employeeClock.Hold();
        var punch = SendAsync(employeeClient, HttpMethod.Post, "/api/punches", employee, """{"intent":"clock-out"}""",
            idempotencyKey: Guid.NewGuid().ToString());
        await employeeClock.WaitForCallsAsync(1); // the punch has read the open shift and waits for its time
        var (corrected, _) = await CorrectAsync(managerClient, manager, shiftId, "clockOut", "2026-01-12T16:00:00+01:00", await _kit.RowVersionAsync(shiftId));
        employeeClock.Release();
        var (punchStatus, problem) = await punch.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.Created, corrected);
        Assert.Equal(HttpStatusCode.Conflict, punchStatus);
        Assert.Equal("shift.not_open", Code(problem));
        var stored = await _kit.LoadShiftAsync(shiftId);
        Assert.Equal(Utc("2026-01-12T15:00:00Z"), stored.ClockOut!.Utc);
        Assert.Single(await _kit.CorrectionsAsync(shiftId));
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, empId));
    }

    [SqlServerFact]
    public async Task A_clock_out_committing_during_the_corrections_clock_call_wins_and_the_correction_is_stale()
    {
        var (employeeHost, _) = _kit.NewHost("2026-01-13T07:00:00Z");
        var (managerHost, managerClock) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f1 = employeeHost;
        await using var _f2 = managerHost;
        using var employeeClient = employeeHost.CreateClient();
        using var managerClient = managerHost.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, employee) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveOpenAsync(empId, "2026-01-12T07:00:00Z");
        var rv = await _kit.RowVersionAsync(shiftId);

        managerClock.Hold();
        var correction = CorrectAsync(managerClient, manager, shiftId, "clockOut", "2026-01-12T16:00:00+01:00", rv);
        await managerClock.WaitForCallsAsync(1);
        var (punchStatus, _) = await SendAsync(employeeClient, HttpMethod.Post, "/api/punches", employee, """{"intent":"clock-out"}""",
            idempotencyKey: Guid.NewGuid().ToString());
        managerClock.Release();
        var (status, problem) = await correction.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.Created, punchStatus);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("correction.stale", Code(problem));
        Assert.Equal(Utc("2026-01-13T07:00:00Z"), (await _kit.LoadShiftAsync(shiftId)).ClockOut!.Utc);
        Assert.Empty(await _kit.CorrectionsAsync(shiftId));
    }

    private static JsonElement Row(JsonElement list, int id) =>
        list.EnumerateArray().Single(r => r.GetProperty("id").GetInt32() == id);
}
