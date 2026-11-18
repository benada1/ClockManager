using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Api.Application.Status;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using Microsoft.Extensions.Logging;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Punches;

/// <summary>
/// <c>GET /api/me/status</c> through the real host and a real SQL Server (plans/03-punch-api.md §4; P13 second half, P20;
/// D-035, D-036). The trusted clock is a fake and the monotonic timer is moved by hand, so "the provider was asked N
/// times" and "the cache expired" are exact, and no test sleeps.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StatusApiTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string StatusUrl = "/api/me/status";
    private const string ClockIn = """{"intent":"clock-in"}""";
    private const string ClockOut = """{"intent":"clock-out"}""";

    private (PunchApiFactory Factory, FakeTrustedClock Clock, FakeElapsedTimer Timer) NewHost(string isoUtc, string environment = "Development")
    {
        var clock = new FakeTrustedClock(isoUtc);
        var timer = new FakeElapsedTimer();
        return (new PunchApiFactory(database, clock, environment, timer), clock, timer);
    }

    // ---- State ----

    [SqlServerFact]
    public async Task No_shift_at_all_gives_200_with_the_employee_no_shifts_a_server_time_and_no_flags()
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync(displayName: "Alice Example");

        var (status, body) = await GetStatusAsync(client, user);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["employee", "openShift", "lastClosedShift", "serverNow", "flags"], Names(body));
        Assert.Equal(user, body.GetProperty("employee").GetProperty("username").GetString());
        Assert.Equal("Alice Example", body.GetProperty("employee").GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("openShift").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastClosedShift").ValueKind);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("serverNow").ValueKind);
        Assert.Equal(0, body.GetProperty("flags").GetArrayLength());
        Assert.Equal(1, clock.Calls);
    }

    [SqlServerFact]
    public async Task An_open_shift_is_returned_in_the_same_shape_as_the_punch_response()
    {
        var (factory, clock, timer) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        var (_, punched) = await PunchAsync(client, ClockIn, user);
        clock.Set("2026-01-12T09:00:00Z");
        timer.Advance(TimeSpan.FromSeconds(30));

        var (status, body) = await GetStatusAsync(client, user);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(punched.GetRawText(), body.GetProperty("openShift").GetRawText());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastClosedShift").ValueKind);
        Assert.Equal("2026-01-12T09:00:00Z", body.GetProperty("serverNow").GetProperty("utc").GetString());
        Assert.Equal(0, body.GetProperty("flags").GetArrayLength());
    }

    [SqlServerFact]
    public async Task After_a_clock_out_the_shift_is_the_last_closed_one_and_nothing_is_open()
    {
        var (factory, clock, timer) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await PunchAsync(client, ClockIn, user);
        clock.Set("2026-01-12T15:30:00Z");
        var (_, closed) = await PunchAsync(client, ClockOut, user);
        timer.Advance(TimeSpan.FromSeconds(11));

        var (_, body) = await GetStatusAsync(client, user);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("openShift").ValueKind);
        Assert.Equal(closed.GetRawText(), body.GetProperty("lastClosedShift").GetRawText());
        Assert.Equal(30600, body.GetProperty("lastClosedShift").GetProperty("durationSeconds").GetInt64());
    }

    [SqlServerFact]
    public async Task The_last_closed_shift_is_the_one_with_the_latest_clock_out_not_the_latest_clock_in()
    {
        var (factory, _, _) = NewHost("2026-02-01T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SaveClosedShiftAsync(employeeId, "2026-01-10T07:00:00Z", "2026-01-10T15:00:00Z");
        var latestOut = await SaveClosedShiftAsync(employeeId, "2026-01-12T06:00:00Z", "2026-01-14T00:00:00Z"); // started EARLIER than the next one
        await SaveClosedShiftAsync(employeeId, "2026-01-13T07:00:00Z", "2026-01-13T08:00:00Z");                 // started latest, ended earlier

        var (_, body) = await GetStatusAsync(client, user);

        Assert.Equal(latestOut.Id, body.GetProperty("lastClosedShift").GetProperty("shiftId").GetInt64());
        Assert.Equal("2026-01-14T00:00:00Z", body.GetProperty("lastClosedShift").GetProperty("clockOut").GetProperty("utc").GetString());
    }

    [SqlServerFact]
    public async Task Another_employees_shifts_never_appear()
    {
        var (factory, _, _) = NewHost("2026-01-12T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, me) = await AddEmployeeAsync();
        var (otherId, _) = await AddEmployeeAsync();
        await SaveClosedShiftAsync(otherId, "2026-01-11T07:00:00Z", "2026-01-11T15:00:00Z");
        await SaveOpenShiftAsync(otherId, "2026-01-12T07:00:00Z");

        var (status, body) = await GetStatusAsync(client, me);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("openShift").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastClosedShift").ValueKind);
        Assert.Equal(0, body.GetProperty("flags").GetArrayLength());
    }

    // ---- Who is asking (same path as a punch) ----

    [SqlServerTheory]
    [InlineData(null)]           // no header
    [InlineData("")]             // blank
    [InlineData("no-such-user")] // unknown username
    public async Task Without_a_known_employee_it_is_401_and_the_provider_is_not_asked(string? user)
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();

        var (status, problem) = await GetStatusAsync(client, user);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(ErrorCodes.Unauthenticated, problem.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
    }

    [SqlServerFact]
    public async Task The_old_X_Dev_Employee_header_is_ignored_and_answers_401()
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, StatusUrl);
        request.Headers.TryAddWithoutValidation("X-Dev-Employee", user); // the removed dev header
        var (status, problem) = await ReadAsync(client, request);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(ErrorCodes.Unauthenticated, problem.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
    }

    [SqlServerFact]
    public async Task An_inactive_employee_is_403_employee_inactive()
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync(active: false);

        var (status, problem) = await GetStatusAsync(client, user);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(ErrorCodes.EmployeeInactive, problem.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
    }

    // ---- serverNow: shape and format ----

    [SqlServerTheory]
    [InlineData("2026-07-06T06:00:00.123Z", "2026-07-06T08:00:00.123+02:00")] // T2 summer
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T08:00:00+01:00")]         // T1 winter: always +01:00, never +01
    [InlineData("2026-03-29T01:00:00Z", "2026-03-29T03:00:00+02:00")]         // T7: exactly at the DST start, never 02:00
    [InlineData("2026-10-25T00:30:00Z", "2026-10-25T02:30:00+02:00")]         // T6: the repeated hour, first pass
    [InlineData("2026-10-25T01:15:00Z", "2026-10-25T02:15:00+01:00")]         // T6: second pass, the wall clock looks backwards
    public async Task ServerNow_has_the_labeled_shape_with_the_Zurich_offset_as_plus_HH_mm(string utc, string zurich)
    {
        var (factory, _, _) = NewHost(utc);
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        var (_, body) = await GetStatusAsync(client, user);

        var now = body.GetProperty("serverNow");
        Assert.Equal(["utc", "zurich", "source", "ageMs"], Names(now));
        Assert.Equal(utc, now.GetProperty("utc").GetString());
        Assert.Equal(zurich, now.GetProperty("zurich").GetString());
        Assert.Equal(StatusService.TrustedClockSource, now.GetProperty("source").GetString());
        Assert.Equal("trusted-clock", now.GetProperty("source").GetString());
        Assert.Equal(0, now.GetProperty("ageMs").GetInt64());
    }

    [SqlServerFact]
    public async Task The_response_is_marked_no_store()
    {
        var (factory, _, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        using var request = StatusRequest(user);
        using var response = await client.SendAsync(request);

        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    // ---- The cache (Q3) ----

    [SqlServerFact]
    public async Task Two_status_calls_within_the_TTL_ask_the_provider_once_and_report_the_age()
    {
        var (factory, clock, timer) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        var (_, first) = await GetStatusAsync(client, user);
        clock.Set("2026-01-12T07:00:04Z"); // real time moves; the cached value must not
        timer.Advance(TimeSpan.FromMilliseconds(3200));
        var (_, second) = await GetStatusAsync(client, user);

        Assert.Equal(1, clock.Calls);
        Assert.Equal(0, first.GetProperty("serverNow").GetProperty("ageMs").GetInt64());
        Assert.Equal("2026-01-12T07:00:00Z", second.GetProperty("serverNow").GetProperty("utc").GetString()); // as fetched, not extrapolated
        Assert.Equal(3200, second.GetProperty("serverNow").GetProperty("ageMs").GetInt64());
    }

    [SqlServerFact]
    public async Task A_status_call_after_the_TTL_asks_the_provider_again()
    {
        var (factory, clock, timer) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await GetStatusAsync(client, user);
        clock.Set("2026-01-12T07:00:11Z");
        timer.Advance(TimeSpan.FromSeconds(10)); // the default TTL

        var (_, body) = await GetStatusAsync(client, user);

        Assert.Equal(2, clock.Calls);
        Assert.Equal("2026-01-12T07:00:11Z", body.GetProperty("serverNow").GetProperty("utc").GetString());
        Assert.Equal(0, body.GetProperty("serverNow").GetProperty("ageMs").GetInt64());
    }

    [SqlServerFact]
    public async Task Parallel_status_calls_share_one_provider_call()
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        clock.Hold();
        var requests = Enumerable.Range(0, 6).Select(_ => GetStatusAsync(client, user)).ToArray();
        await clock.WaitForCallsAsync(1);
        clock.Release();
        var results = await Task.WhenAll(requests);

        Assert.All(results, r =>
        {
            Assert.Equal(HttpStatusCode.OK, r.Status);
            Assert.Equal("2026-01-12T07:00:00Z", r.Body.GetProperty("serverNow").GetProperty("utc").GetString());
        });
        Assert.Equal(1, clock.Calls);
    }

    // ---- D1: a punch never uses the cached time ----

    [SqlServerFact]
    public async Task D1_a_punch_right_after_a_status_call_asks_the_provider_afresh_and_records_the_new_time()
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await GetStatusAsync(client, user);            // caches 07:00:00
        Assert.Equal(1, clock.Calls);
        clock.Set("2026-01-12T07:00:05Z");             // 5 s later; well inside the cache TTL

        var (status, punched) = await PunchAsync(client, ClockIn, user);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(2, clock.Calls);                  // the punch asked the provider itself
        Assert.Equal("2026-01-12T07:00:05Z", punched.GetProperty("clockIn").GetProperty("utc").GetString()); // not the cached 07:00:00
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));

        // ...and the status still serves its own cache (the punch did not refresh it, and does not depend on it).
        var (_, after) = await GetStatusAsync(client, user);
        Assert.Equal(2, clock.Calls);
        Assert.Equal("2026-01-12T07:00:00Z", after.GetProperty("serverNow").GetProperty("utc").GetString());
    }

    [SqlServerFact]
    public async Task D1_a_punch_does_not_fill_the_status_cache_either()
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await PunchAsync(client, ClockIn, user);
        Assert.Equal(1, clock.Calls);

        await GetStatusAsync(client, user);

        Assert.Equal(2, clock.Calls); // the status made its own fetch
    }

    [SqlServerFact]
    public async Task D1_a_punch_succeeds_while_the_status_cache_holds_a_stale_value_and_fails_closed_when_the_provider_is_down()
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await GetStatusAsync(client, user); // a good value is cached
        clock.FailWith(FakeTrustedClock.Unavailable());

        var (status, problem) = await PunchAsync(client, ClockIn, user);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status); // the cached time is NOT a fallback for a punch
        Assert.Equal(ErrorCodes.TimeUnavailable, problem.GetProperty("code").GetString());
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    // ---- P20: provider down ----

    [SqlServerFact]
    public async Task P20_provider_down_gives_200_with_the_state_and_no_server_time_and_no_time_flags()
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        var (_, punched) = await PunchAsync(client, ClockIn, user);
        clock.FailWith(new ClockManager.Api.Application.Time.TrustedTimeException(
            ErrorCodes.TimeUnavailable, [new ClockManager.Api.Application.Time.ProviderOutcome("time.now", "timeout")]));

        var (status, body) = await GetStatusAsync(client, user);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(punched.GetRawText(), body.GetProperty("openShift").GetRawText()); // the state is still there
        Assert.Equal(JsonValueKind.Null, body.GetProperty("serverNow").ValueKind);
        Assert.Equal(0, body.GetProperty("flags").GetArrayLength());
        Assert.Equal(["employee", "openShift", "lastClosedShift", "serverNow", "flags"], Names(body)); // no error fields, no provider details
        Assert.DoesNotContain("time.now", body.GetRawText());
        Assert.DoesNotContain(factory.Logs, l => l.Level >= LogLevel.Error);
    }

    [SqlServerTheory]
    [InlineData(ErrorCodes.TimeUnavailable)]
    [InlineData(ErrorCodes.TimeInvalidResponse)]
    [InlineData(ErrorCodes.TimeProvidersDisagree)]
    public async Task P20_every_kind_of_time_failure_is_a_200_never_a_5xx(string code)
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        clock.FailWith(new ClockManager.Api.Application.Time.TrustedTimeException(
            code, [new ClockManager.Api.Application.Time.ProviderOutcome("time.now", "timeout"), new ClockManager.Api.Application.Time.ProviderOutcome("timeapi.io", "ok")]));

        var (status, body) = await GetStatusAsync(client, user);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("serverNow").ValueKind);
    }

    [SqlServerFact]
    public async Task P20_an_unexpected_exception_in_the_clock_is_still_a_200_and_its_text_never_reaches_the_client()
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        clock.FailWith(new InvalidOperationException("secret connection detail"));

        using var request = StatusRequest(user);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("secret", text);
    }

    [SqlServerFact]
    public async Task P20_a_forgotten_clock_out_is_not_guessed_when_there_is_no_trusted_time()
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z"); // T9
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await PunchAsync(client, ClockIn, user);
        clock.FailWith(FakeTrustedClock.Unavailable()); // and the open shift is, in reality, far older than 16 h

        var (_, body) = await GetStatusAsync(client, user);

        Assert.Equal(JsonValueKind.Object, body.GetProperty("openShift").ValueKind);
        Assert.Equal(0, body.GetProperty("flags").GetArrayLength());
    }

    [SqlServerFact]
    public async Task P20_the_server_time_comes_back_as_soon_as_the_provider_does_after_a_second()
    {
        var (factory, clock, timer) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        clock.FailWith(FakeTrustedClock.Unavailable());
        var (_, down) = await GetStatusAsync(client, user);
        clock.Recover();
        timer.Advance(TimeSpan.FromSeconds(1)); // not the 10 s success TTL

        var (_, up) = await GetStatusAsync(client, user);

        Assert.Equal(JsonValueKind.Null, down.GetProperty("serverNow").ValueKind);
        Assert.Equal("2026-01-12T07:00:00Z", up.GetProperty("serverNow").GetProperty("utc").GetString());
    }

    // ---- P13, second half: forgotten clock-out ----

    [SqlServerTheory]
    [InlineData("2026-01-12T22:59:59Z", false)] // 15 h 59 min 59 s
    [InlineData("2026-01-12T23:00:00Z", false)] // exactly 16 h: not flagged (strict, like long_shift, D-031)
    [InlineData("2026-01-12T23:00:01Z", true)]  // 16 h 1 s
    [InlineData("2026-01-13T00:00:00Z", true)]  // 17 h
    [InlineData("2026-01-13T07:00:00Z", true)]  // T9: still open a day later
    public async Task P13_an_open_shift_older_than_16_hours_by_the_trusted_now_gets_forgotten_clock_out(string nowUtc, bool flagged)
    {
        var (factory, clock, _) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await PunchAsync(client, ClockIn, user);
        clock.Set(nowUtc);

        var (_, body) = await GetStatusAsync(client, user);

        Assert.Equal(flagged ? new[] { "forgotten_clock_out" } : Array.Empty<string>(), body.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(0, body.GetProperty("openShift").GetProperty("flags").GetArrayLength()); // top-level only
        Assert.Equal(JsonValueKind.Object, body.GetProperty("openShift").ValueKind);        // never auto-closed
    }

    [SqlServerFact]
    public async Task A_long_closed_shift_is_long_shift_on_the_shift_and_not_forgotten_clock_out()
    {
        var (factory, clock, timer) = NewHost("2026-01-12T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await PunchAsync(client, ClockIn, user);
        clock.Set("2026-01-13T00:00:00Z"); // 17 h
        await PunchAsync(client, ClockOut, user);
        timer.Advance(TimeSpan.FromSeconds(11));

        var (_, body) = await GetStatusAsync(client, user);

        Assert.Equal(0, body.GetProperty("flags").GetArrayLength());
        Assert.Equal(["long_shift"], body.GetProperty("lastClosedShift").GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
    }

    [SqlServerFact]
    public async Task The_flag_is_measured_in_UTC_across_the_DST_start()
    {
        var (factory, clock, _) = NewHost("2026-03-28T21:00:00Z"); // T4: 22:00 +01:00
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await PunchAsync(client, ClockIn, user);
        clock.Set("2026-03-29T13:00:00Z"); // 15:00 +02:00: 17 h on the wall clock, exactly 16 h in UTC

        var (_, body) = await GetStatusAsync(client, user);

        Assert.Equal(0, body.GetProperty("flags").GetArrayLength());
    }

    // ---------- Helpers ----------

    private async Task<(int Id, string Username)> AddEmployeeAsync(bool active = true, string displayName = "Test Employee")
    {
        await using var db = database.CreateContext();
        var employee = new Employee("e" + Guid.NewGuid().ToString("N")[..12], displayName, EmployeeRole.Employee);
        db.Employees.Add(employee);
        if (!active) db.Entry(employee).Property(e => e.IsActive).CurrentValue = false;
        await db.SaveChangesAsync();
        return (employee.Id, employee.Username);
    }

    private async Task<Shift> SaveClosedShiftAsync(int employeeId, string inUtc, string outUtc)
    {
        await using var db = database.CreateContext();
        var shift = new Shift(employeeId, Stamp(inUtc));
        shift.Close(Stamp(outUtc));
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        return shift;
    }

    private async Task<Shift> SaveOpenShiftAsync(int employeeId, string inUtc)
    {
        await using var db = database.CreateContext();
        var shift = new Shift(employeeId, Stamp(inUtc));
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        return shift;
    }

    private static HttpRequestMessage StatusRequest(string? user)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, StatusUrl);
        if (user is not null) request.Headers.TryAddWithoutValidation("X-Test-User", user);
        return request;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetStatusAsync(HttpClient client, string? user)
    {
        using var request = StatusRequest(user);
        return await ReadAsync(client, request);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PunchAsync(HttpClient client, string body, string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/punches")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Test-User", user);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        return await ReadAsync(client, request);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response.StatusCode, json);
    }

    private static string[] Names(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name).ToArray();
}
