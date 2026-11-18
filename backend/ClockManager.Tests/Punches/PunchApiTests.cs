using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Api.Application.Punches;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Punches;

/// <summary>
/// <c>POST /api/punches</c> through the real host and a real SQL Server (plans/03-punch-api.md §6: P1, P5, P9, P11, P12,
/// P13–P15, P17–P19, part of P16). The trusted clock is a fake; everything else (controller, service, EF Core,
/// constraints, ProblemDetails) is the production code.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PunchApiTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string Url = "/api/punches";
    private const string ClockIn = """{"intent":"clock-in"}""";
    private const string ClockOut = """{"intent":"clock-out"}""";

    // ---- P1: the normal day ----

    [SqlServerFact]
    public async Task P1_clock_in_then_clock_out_returns_201_twice_with_the_duration_from_UTC_T1()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var (inStatus, inBody) = await SendAsync(client, ClockIn, user);
        clock.Set("2026-01-12T15:30:00Z");
        var (outStatus, outBody) = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.Created, inStatus);
        Assert.Equal(["shiftId", "workDate", "clockIn", "clockOut", "durationSeconds", "flags"], Names(inBody));
        Assert.Equal("2026-01-12", inBody.GetProperty("workDate").GetString());
        AssertPunch(inBody.GetProperty("clockIn"), "2026-01-12T07:00:00Z", "2026-01-12T08:00:00+01:00");
        Assert.Equal(JsonValueKind.Null, inBody.GetProperty("clockOut").ValueKind);
        Assert.Equal(JsonValueKind.Null, inBody.GetProperty("durationSeconds").ValueKind);
        Assert.Equal(0, inBody.GetProperty("flags").GetArrayLength());

        Assert.Equal(HttpStatusCode.Created, outStatus);
        Assert.Equal(inBody.GetProperty("shiftId").GetInt64(), outBody.GetProperty("shiftId").GetInt64());
        AssertPunch(outBody.GetProperty("clockOut"), "2026-01-12T15:30:00Z", "2026-01-12T16:30:00+01:00");
        Assert.Equal(30600, outBody.GetProperty("durationSeconds").GetInt64());
        Assert.Equal(0, outBody.GetProperty("flags").GetArrayLength());

        await using var db = database.CreateContext();
        var stored = await db.Shifts.AsNoTracking().SingleAsync(s => s.EmployeeId == employeeId);
        Assert.Equal(Utc("2026-01-12T07:00:00Z"), stored.ClockIn.Utc);
        Assert.Equal(Utc("2026-01-12T15:30:00Z"), stored.ClockOut!.Utc);
        Assert.Equal(FakeTrustedClock.ProviderName, stored.ClockOut.Provider);
        Assert.Equal(2, clock.Calls);
    }

    [SqlServerFact]
    public async Task Shift_across_DST_end_over_HTTP_lasts_9_hours_with_both_offsets_T5()
    {
        var clock = new FakeTrustedClock("2026-10-24T20:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        await SendAsync(client, ClockIn, user);
        clock.Set("2026-10-25T05:00:00Z");
        var (status, body) = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(9 * 3600, body.GetProperty("durationSeconds").GetInt64());
        Assert.Equal("2026-10-24T22:00:00+02:00", body.GetProperty("clockIn").GetProperty("zurich").GetString());
        Assert.Equal("2026-10-25T06:00:00+01:00", body.GetProperty("clockOut").GetProperty("zurich").GetString());
        Assert.Equal("2026-10-24", body.GetProperty("workDate").GetString());
    }

    // ---- State checks (fast path, no provider call) ----

    [SqlServerFact]
    public async Task Clock_in_while_a_shift_is_open_is_409_already_open_with_the_open_shift_and_no_provider_call()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var (_, first) = await SendAsync(client, ClockIn, user);
        clock.Set("2026-01-12T07:05:00Z");

        var (status, problem) = await SendAsync(client, ClockIn, user);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(ErrorCodes.ShiftAlreadyOpen, problem.GetProperty("code").GetString());
        var open = problem.GetProperty("openShift");
        Assert.Equal(first.GetProperty("shiftId").GetInt64(), open.GetProperty("shiftId").GetInt64());
        Assert.Equal("2026-01-12T07:00:00Z", open.GetProperty("clockIn").GetProperty("utc").GetString());
        Assert.Equal(1, clock.Calls);
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task Clock_out_without_an_open_shift_is_409_not_open_and_no_provider_call()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var (status, problem) = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(ErrorCodes.ShiftNotOpen, problem.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    // ---- P5: 10 parallel clock-ins ----

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task P5_ten_parallel_clock_ins_give_exactly_one_201_nine_409_already_open_and_one_row(int round)
    {
        _ = round; // independent rounds: one lucky pass is not proof
        const int racers = 10;
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        // All 10 pass the state check and wait inside the "provider call", then save at once: the DB index decides.
        clock.Hold();
        var requests = Enumerable.Range(0, racers).Select(_ => SendAsync(client, ClockIn, user)).ToArray();
        await clock.WaitForCallsAsync(racers);
        clock.Release();
        var results = await Task.WhenAll(requests);

        var winner = Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        var losers = results.Where(r => r.Status != HttpStatusCode.Created).ToList();
        Assert.Equal(racers - 1, losers.Count);
        Assert.All(losers, l =>
        {
            Assert.Equal(HttpStatusCode.Conflict, l.Status);
            Assert.Equal(ErrorCodes.ShiftAlreadyOpen, l.Body.GetProperty("code").GetString());
            Assert.Equal(winner.Body.GetProperty("shiftId").GetInt64(), l.Body.GetProperty("openShift").GetProperty("shiftId").GetInt64());
        });
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    // ---- P9: no trusted time ----

    [SqlServerTheory]
    [InlineData(ErrorCodes.TimeUnavailable)]
    [InlineData(ErrorCodes.TimeInvalidResponse)]
    [InlineData(ErrorCodes.TimeProvidersDisagree)]
    public async Task P9_provider_failure_is_503_with_its_code_and_nothing_is_saved(string code)
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        clock.FailWith(new TrustedTimeException(code, [new ProviderOutcome("time.now", "timeout"), new ProviderOutcome("timeapi.io", "ok")]));
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var (status, problem) = await SendAsync(client, ClockIn, user);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(2, problem.GetProperty("providers").GetArrayLength());
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task P9_provider_failure_on_clock_out_leaves_the_shift_open()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.FailWith(FakeTrustedClock.Unavailable());

        var (status, problem) = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(ErrorCodes.TimeUnavailable, problem.GetProperty("code").GetString());
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));
    }

    // ---- P11: time went backwards ----

    [SqlServerTheory]
    [InlineData("2026-01-12T07:00:00Z")] // equal to the clock-in (T11)
    [InlineData("2026-01-12T06:59:58Z")] // 2 s before it (T10)
    public async Task P11_clock_out_at_or_before_the_clock_in_is_503_went_backwards_and_the_shift_stays_open(string outUtc)
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set(outUtc);

        var (status, problem) = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(ErrorCodes.TimeWentBackwards, problem.GetProperty("code").GetString());
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));
        Assert.Contains(factory.Logs, l => l.Level == LogLevel.Warning && l.Message.Contains("not later than"));
    }

    [SqlServerFact]
    public async Task P11_clock_in_earlier_than_the_previous_clock_out_is_503_went_backwards_and_nothing_is_saved()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set("2026-01-12T15:30:00Z");
        await SendAsync(client, ClockOut, user);
        clock.Set("2026-01-12T15:00:00Z"); // after the last clock-in, but before the last clock-out

        var (status, problem) = await SendAsync(client, ClockIn, user);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(ErrorCodes.TimeWentBackwards, problem.GetProperty("code").GetString());
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    // ---- A punch saved by another tab DURING our provider call, at the same trusted instant ----
    // Regression for a bug found by P5 under load: the loser's monotonic guard saw the winner's punch and answered
    // 503 time.went_backwards, although the time source was fine and the right answer is the 409 of a lost race.

    [SqlServerFact]
    public async Task Clock_in_with_the_same_time_as_a_clock_in_saved_during_its_provider_call_is_409_already_open_not_503()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        clock.Hold();
        var request = SendAsync(client, ClockIn, user);
        await clock.WaitForCallsAsync(1);       // past the state check, waiting for the "provider"
        var otherTab = await SaveShiftAsync(employeeId, "2026-01-12T07:00:00Z"); // the other tab wins, same instant
        clock.Release();
        var (status, problem) = await request;

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(ErrorCodes.ShiftAlreadyOpen, problem.GetProperty("code").GetString());
        Assert.Equal(otherTab.Id, problem.GetProperty("openShift").GetProperty("shiftId").GetInt64());
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.DoesNotContain(factory.Logs, l => l.Category == typeof(PunchService).FullName && l.Level >= LogLevel.Warning);
    }

    [SqlServerFact]
    public async Task Clock_out_with_the_same_time_as_a_clock_out_saved_during_its_provider_call_is_409_not_open_not_503()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var (_, opened) = await SendAsync(client, ClockIn, user);
        clock.Set("2026-01-12T15:30:00Z");

        clock.Hold();
        var request = SendAsync(client, ClockOut, user);
        await clock.WaitForCallsAsync(2);
        await using (var otherTab = database.CreateContext())
        {
            var shift = await otherTab.Shifts.SingleAsync(s => s.Id == opened.GetProperty("shiftId").GetInt64());
            shift.Close(Stamp("2026-01-12T15:30:00Z", provider: "time.now"));
            await otherTab.SaveChangesAsync();
        }
        clock.Release();
        var (status, problem) = await request;

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(ErrorCodes.ShiftNotOpen, problem.GetProperty("code").GetString());
        await using var db = database.CreateContext();
        var stored = await db.Shifts.AsNoTracking().SingleAsync(s => s.EmployeeId == employeeId);
        Assert.Equal("time.now", stored.ClockOut!.Provider); // the other tab's clock-out stays
    }

    [SqlServerFact]
    public async Task Time_behind_a_punch_that_existed_before_the_call_is_still_503_went_backwards()
    {
        // The same as P11, but with a held provider call: the refusal depends on what existed BEFORE the call.
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);

        clock.Hold();
        var request = SendAsync(client, ClockOut, user); // the clock still says 07:00, equal to the clock-in
        await clock.WaitForCallsAsync(2);
        clock.Release();
        var (status, problem) = await request;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(ErrorCodes.TimeWentBackwards, problem.GetProperty("code").GetString());
    }

    // ---- P12: two tabs clock out the same shift ----

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task P12_two_parallel_clock_outs_give_one_201_and_one_409_not_open(int round)
    {
        _ = round;
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set("2026-01-12T15:30:00Z");

        clock.Hold(); // both load the open shift (same RowVersion), then both try to close it
        var a = SendAsync(client, ClockOut, user);
        var b = SendAsync(client, ClockOut, user);
        await clock.WaitForCallsAsync(3);
        clock.Release();
        var results = await Task.WhenAll(a, b);

        Assert.Single(results, r => r.Status == HttpStatusCode.Created);
        var loser = Assert.Single(results, r => r.Status == HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.ShiftNotOpen, loser.Body.GetProperty("code").GetString());
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    // ---- P13, P14: flags on the returned shift ----

    [SqlServerTheory]
    [InlineData("2026-01-13T00:00:00Z", 61200, "long_shift")]  // 17 h (P13)
    [InlineData("2026-01-12T07:00:20Z", 20, "short_shift")]    // 20 s (P14)
    public async Task P13_P14_clock_out_returns_the_computed_flag(string outUtc, long seconds, string flag)
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set(outUtc);

        var (status, body) = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(seconds, body.GetProperty("durationSeconds").GetInt64());
        Assert.Equal([flag], body.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
    }

    // ---- P15: strict body ----

    [SqlServerTheory]
    [InlineData("""{"intent":"clock-in","time":"2026-01-12T07:00:00Z"}""")] // a client trying to send a timestamp
    [InlineData("""{"intent":"clock-in","clockInUtc":"2026-01-12T07:00:00Z"}""")]
    [InlineData("""{"intent":"clock-sideways"}""")]
    [InlineData("""{"intent":"Clock-In"}""")]
    [InlineData("""{"intent":" clock-in"}""")]
    [InlineData("""{"intent":""}""")]
    [InlineData("""{"intent":null}""")]
    [InlineData("""{"intent":0}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""not json""")]
    [InlineData("")]
    public async Task P15_invalid_body_is_400_request_invalid_nothing_saved_and_provider_not_asked(string body)
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var (status, problem) = await SendAsync(client, body, user);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(ErrorCodes.RequestInvalid, problem.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    // ---- P16 (step 1 part): the Idempotency-Key header is required ----

    [SqlServerTheory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("3f1c0d7e8a4b4c559a510d2b7c1e9f10")] // UUID without hyphens: only the canonical form is accepted
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task P16_missing_or_malformed_Idempotency_Key_is_400_key_missing_and_provider_not_asked(string? key)
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var (status, problem) = await SendAsync(client, ClockIn, user, key: key, sendKey: key is not null);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(ErrorCodes.IdempotencyKeyMissing, problem.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    // ---- Who is punching (P17, P18) ----

    [SqlServerFact]
    public async Task P17_the_old_X_Dev_Employee_header_is_ignored_in_every_environment_and_answers_401()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock); // Development, where the header used to work
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync(); // a real, active employee

        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(ClockIn, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Dev-Employee", user);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var status = response.StatusCode;

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(ErrorCodes.Unauthenticated, problem.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerTheory]
    [InlineData(null)]           // no header
    [InlineData("")]             // blank
    [InlineData("no-such-user")] // unknown username
    public async Task Development_without_a_known_employee_is_401(string? user)
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();

        var (status, problem) = await SendAsync(client, ClockIn, user);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(ErrorCodes.Unauthenticated, problem.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
    }

    [SqlServerFact]
    public async Task P18_inactive_employee_is_403_employee_inactive()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync(active: false);

        var (status, problem) = await SendAsync(client, ClockIn, user);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(ErrorCodes.EmployeeInactive, problem.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    // ---- P19: the client disconnects during the provider call ----

    [SqlServerFact]
    public async Task P19_client_disconnect_during_the_provider_call_saves_nothing_and_logs_no_failure()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        clock.Hold(); // the "provider" never answers on its own
        using var disconnect = new CancellationTokenSource();
        var request = SendAsync(client, ClockIn, user, ct: disconnect.Token);
        await clock.WaitForCallsAsync(1);
        disconnect.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        // The server's provider wait is cancelled by RequestAborted (the clock is still held), so the request finishes
        // on its own. ASP.NET Core's exception middleware treats an aborted request's OperationCanceledException as
        // "client went away" (no handler, no error log).
        await WaitUntilAsync(() => !factory.Completed.IsEmpty);
        clock.Release();

        var completed = Assert.Single(factory.Completed);
        Assert.True(completed.Aborted);
        Assert.NotEqual(StatusCodes.Status503ServiceUnavailable, completed.Status); // not reported as "time unavailable"
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.DoesNotContain(factory.Logs, l => l.Level >= LogLevel.Warning);
    }

    // ---------- Helpers ----------

    private async Task<(int Id, string Username)> AddEmployeeAsync(bool active = true)
    {
        await using var db = database.CreateContext();
        var employee = NewEmployee();
        db.Employees.Add(employee);
        if (!active) db.Entry(employee).Property(e => e.IsActive).CurrentValue = false;
        await db.SaveChangesAsync();
        return (employee.Id, employee.Username);
    }

    /// <summary>An open shift written directly to the DB, as another tab or server would have saved it.</summary>
    private async Task<Shift> SaveShiftAsync(int employeeId, string clockInUtc)
    {
        await using var db = database.CreateContext();
        var shift = new Shift(employeeId, Stamp(clockInUtc));
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        return shift;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpClient client, string body, string? user, string? key = null, bool sendKey = true, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (user is not null) request.Headers.TryAddWithoutValidation("X-Test-User", user);
        if (sendKey) request.Headers.TryAddWithoutValidation("Idempotency-Key", key ?? Guid.NewGuid().ToString());

        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return (response.StatusCode, json);
    }

    private static void AssertPunch(JsonElement punch, string utc, string zurich)
    {
        Assert.Equal(["utc", "zurich", "provider", "crossCheckedBy", "lowPrecision"], Names(punch));
        Assert.Equal(utc, punch.GetProperty("utc").GetString());
        Assert.Equal(zurich, punch.GetProperty("zurich").GetString());
        Assert.Equal(FakeTrustedClock.ProviderName, punch.GetProperty("provider").GetString());
        Assert.Equal(JsonValueKind.Null, punch.GetProperty("crossCheckedBy").ValueKind);
        Assert.False(punch.GetProperty("lowPrecision").GetBoolean());
    }

    private static string[] Names(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name).ToArray();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
