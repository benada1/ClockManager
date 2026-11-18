using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Punches;

/// <summary>
/// QA pass, slice B group 4: status endpoint and the 409 bodies, beyond StatusApiTests / ServerNowProviderTests (which already
/// cover the cache TTL, the 1 s failure memory, the shared fetch, the 16 h boundary, another employee's shifts and a provider
/// that is down). New here: ties in "last closed", a "now" before the clock-in, the 409 body versus the status read-back,
/// and a 409 whose open shift was closed again by another tab during the provider call. Every wait has a 30 s timeout.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PunchQaStatusTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string ClockIn = """{"intent":"clock-in"}""";
    private const string ClockOut = """{"intent":"clock-out"}""";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [SqlServerFact]
    public async Task Two_closed_shifts_with_the_same_clock_out_resolve_to_the_higher_id_every_time()
    {
        var clock = new FakeTrustedClock("2026-01-12T12:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);
        // Overlapping shifts can exist today (QA-23); they tie on the clock-out instant.
        var first = await SaveShiftAsync(employeeId, "2026-01-12T07:00:00Z", "2026-01-12T10:00:00Z");
        var second = await SaveShiftAsync(employeeId, "2026-01-12T08:00:00Z", "2026-01-12T10:00:00Z");

        for (var i = 0; i < 3; i++)
        {
            var status = await SendAsync(client, HttpMethod.Get, "/api/me/status", null, user, null, cts.Token);
            Assert.Equal(second.Id, status.Body.GetProperty("lastClosedShift").GetProperty("shiftId").GetInt64());
        }
        Assert.True(second.Id > first.Id);
    }

    [SqlServerFact]
    public async Task A_now_before_the_clock_in_gives_200_no_flag_and_no_negative_or_garbage_numbers()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z"); // server time is 3 h BEFORE the open shift's clock-in
        await using var factory = new PunchApiFactory(database, clock, timer: new FakeElapsedTimer());
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);
        await SaveShiftAsync(employeeId, "2026-01-12T10:00:00Z", null);

        var status = await SendAsync(client, HttpMethod.Get, "/api/me/status", null, user, null, cts.Token);

        Assert.Equal(HttpStatusCode.OK, status.Status);
        Assert.Equal(0, status.Body.GetProperty("flags").GetArrayLength());
        var open = status.Body.GetProperty("openShift");
        Assert.Equal(JsonValueKind.Null, open.GetProperty("durationSeconds").ValueKind);
        Assert.Equal(0, open.GetProperty("flags").GetArrayLength());
        var now = status.Body.GetProperty("serverNow");
        Assert.True(now.GetProperty("ageMs").GetInt64() >= 0);
        Assert.Equal("2026-01-12T07:00:00Z", now.GetProperty("utc").GetString());
    }

    [SqlServerFact]
    public async Task The_409_already_open_body_is_the_same_shift_object_the_status_endpoint_returns()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock, timer: new FakeElapsedTimer());
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);
        await SendAsync(client, HttpMethod.Post, "/api/punches", ClockIn, user, Guid.NewGuid().ToString(), cts.Token);
        clock.Set("2026-01-12T07:10:00Z");

        var conflict = await SendAsync(client, HttpMethod.Post, "/api/punches", ClockIn, user, Guid.NewGuid().ToString(), cts.Token);
        var status = await SendAsync(client, HttpMethod.Get, "/api/me/status", null, user, null, cts.Token);

        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Equal(conflict.Body.GetProperty("openShift").GetRawText(), status.Body.GetProperty("openShift").GetRawText());
    }

    [SqlServerFact]
    public async Task Status_follows_every_punch_open_then_closed_then_open_again_with_the_new_shift()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock, timer: new FakeElapsedTimer());
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);

        var in1 = await SendAsync(client, HttpMethod.Post, "/api/punches", ClockIn, user, Guid.NewGuid().ToString(), cts.Token);
        var s1 = await SendAsync(client, HttpMethod.Get, "/api/me/status", null, user, null, cts.Token);
        clock.Set("2026-01-12T08:00:00Z");
        var out1 = await SendAsync(client, HttpMethod.Post, "/api/punches", ClockOut, user, Guid.NewGuid().ToString(), cts.Token);
        var s2 = await SendAsync(client, HttpMethod.Get, "/api/me/status", null, user, null, cts.Token);
        clock.Set("2026-01-12T09:00:00Z");
        var in2 = await SendAsync(client, HttpMethod.Post, "/api/punches", ClockIn, user, Guid.NewGuid().ToString(), cts.Token);
        var s3 = await SendAsync(client, HttpMethod.Get, "/api/me/status", null, user, null, cts.Token);

        Assert.Equal(in1.Body.GetRawText(), s1.Body.GetProperty("openShift").GetRawText());
        Assert.Equal(JsonValueKind.Null, s1.Body.GetProperty("lastClosedShift").ValueKind);
        Assert.Equal(JsonValueKind.Null, s2.Body.GetProperty("openShift").ValueKind);
        Assert.Equal(out1.Body.GetRawText(), s2.Body.GetProperty("lastClosedShift").GetRawText());
        Assert.Equal(in2.Body.GetRawText(), s3.Body.GetProperty("openShift").GetRawText());
        Assert.Equal(out1.Body.GetRawText(), s3.Body.GetProperty("lastClosedShift").GetRawText());
        Assert.NotEqual(in1.Body.GetProperty("shiftId").GetInt64(), in2.Body.GetProperty("shiftId").GetInt64());
    }

    [SqlServerFact]
    public async Task A_clock_in_409_after_another_tab_did_a_whole_in_out_cycle_returns_openShift_null_today()
    {
        var (status, body) = await ClockInOverlappedByAnotherTabsWholeCycleAsync();

        // Recorded as it is: 409 shift.already_open with openShift = null (the shift is already closed again).
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(ErrorCodes.ShiftAlreadyOpen, body.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("openShift").ValueKind);
    }

    [SqlServerFact]
    public async Task QA26_a_409_with_no_open_shift_does_not_claim_a_shift_is_open()
    {
        var (_, body) = await ClockInOverlappedByAnotherTabsWholeCycleAsync();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("openShift").ValueKind);
        var text = body.GetProperty("title").GetString() + " " + body.GetProperty("detail").GetString();
        Assert.DoesNotContain("already clocked in", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Clock out before", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Our clock-in waits in the provider call. Meanwhile "another tab" clocks in at 07:00:00 and out at 07:00:30 (written
    /// straight to the DB). Our trusted time is 07:00:10, not later than that clock-out, so the guard answers 409.
    /// </summary>
    private async Task<(HttpStatusCode Status, JsonElement Body)> ClockInOverlappedByAnotherTabsWholeCycleAsync()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:10Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);

        clock.Hold();
        var request = SendAsync(client, HttpMethod.Post, "/api/punches", ClockIn, user, Guid.NewGuid().ToString(), cts.Token);
        await clock.WaitForCallsAsync(1);
        await SaveShiftAsync(employeeId, "2026-01-12T07:00:00Z", "2026-01-12T07:00:30Z");
        clock.Release();

        var reply = await request.WaitAsync(cts.Token);
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId)); // only the other tab's shift
        return (reply.Status, reply.Body);
    }

    // ---------- Helpers ----------

    private sealed record Reply(HttpStatusCode Status, JsonElement Body);

    private async Task<(int Id, string Username)> AddEmployeeAsync()
    {
        var id = await ShiftRows.AddEmployeeAsync(database);
        await using var db = database.CreateContext();
        return (id, db.Employees.Where(e => e.Id == id).Select(e => e.Username).Single());
    }

    private async Task<Shift> SaveShiftAsync(int employeeId, string inUtc, string? outUtc)
    {
        await using var db = database.CreateContext();
        var shift = new Shift(employeeId, Stamp(inUtc));
        if (outUtc is not null) shift.Close(Stamp(outUtc));
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        return shift;
    }

    private static async Task<Reply> SendAsync(HttpClient client, HttpMethod method, string url, string? body, string user, string? key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Test-User", user);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        }
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return new Reply(response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
}
