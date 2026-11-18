using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Api.Common.Errors;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Punches;

/// <summary>
/// QA pass, slice A group 4: the monotonic guard over HTTP at the edges (equal, 1 ms, sub-tick, far future) and
/// proof that <c>Shift.Close</c>'s InvalidOperationException can't surface as a 500.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PunchQaGuardTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string ClockIn = """{"intent":"clock-in"}""";
    private const string ClockOut = """{"intent":"clock-out"}""";
    private const string T0 = "2026-01-12T07:00:00.123Z";

    [SqlServerTheory]
    [InlineData("2026-01-12T07:00:00.123Z")]  // the same millisecond
    [InlineData("2026-01-12T07:00:00.122Z")]  // 1 ms earlier
    [InlineData("2026-01-12T07:00:00.000Z")]  // earlier inside the same second
    [InlineData("2026-01-11T07:00:00.123Z")]  // a day earlier
    public async Task Clock_out_at_or_before_the_clock_in_to_the_millisecond_is_503_went_backwards_never_500(string outUtc)
    {
        var clock = new FakeTrustedClock(T0);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set(outUtc);

        var reply = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.Status);
        Assert.Equal(ErrorCodes.TimeWentBackwards, reply.Body.GetProperty("code").GetString());
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));
        Assert.DoesNotContain("InvalidOperation", reply.Text, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Clock_out_one_millisecond_after_the_clock_in_is_accepted_with_a_zero_second_short_shift()
    {
        var clock = new FakeTrustedClock(T0);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set("2026-01-12T07:00:00.124Z");

        var reply = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.Created, reply.Status);
        Assert.Equal(0, reply.Body.GetProperty("durationSeconds").GetInt64());
        Assert.Equal(["short_shift"], reply.Body.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
    }

    [SqlServerFact]
    public async Task Clock_in_at_exactly_the_previous_clock_out_instant_is_503_went_backwards()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set("2026-01-12T15:30:00.500Z");
        await SendAsync(client, ClockOut, user);

        var reply = await SendAsync(client, ClockIn, user); // clock did not move: same instant as the clock-out

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.Status);
        Assert.Equal(ErrorCodes.TimeWentBackwards, reply.Body.GetProperty("code").GetString());
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task A_new_clock_in_one_millisecond_after_the_previous_clock_out_is_accepted()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set("2026-01-12T15:30:00.500Z");
        await SendAsync(client, ClockOut, user);
        clock.Set("2026-01-12T15:30:00.501Z");

        var reply = await SendAsync(client, ClockIn, user);

        Assert.Equal(HttpStatusCode.Created, reply.Status);
        Assert.Equal(2, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    /// <summary>
    /// NodaTime Instants have nanoseconds; datetimeoffset(7) keeps 100 ns ticks. A trusted instant that is later than
    /// the clock-in by less than one tick passes the guard and Shift.Close (compared in ns) but is stored equal. The
    /// real providers only give ms, so this is unreachable in production today (low); it shows what a future
    /// higher-resolution source would hit.
    /// </summary>
    [SqlServerTheory]
    [InlineData(1)]   // 1 ns
    [InlineData(50)]  // half a tick
    [InlineData(99)]  // one ns short of a tick
    public async Task Clock_out_less_than_one_database_tick_after_the_clock_in_is_503_went_backwards_not_500(int nanos)
    {
        var clock = new FakeTrustedClock(Instant.FromUtc(2026, 1, 12, 7, 0, 0));
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set(Instant.FromUtc(2026, 1, 12, 7, 0, 0) + Duration.FromNanoseconds(nanos));

        var reply = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.Status);
        Assert.Equal(ErrorCodes.TimeWentBackwards, reply.Body.GetProperty("code").GetString());
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));
    }

    [SqlServerFact]
    public async Task Clock_out_exactly_one_tick_after_the_clock_in_is_accepted_and_stored_strictly_later()
    {
        var start = Instant.FromUtc(2026, 1, 12, 7, 0, 0);
        var clock = new FakeTrustedClock(start);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set(start + Duration.FromNanoseconds(100));

        var reply = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.Created, reply.Status);
        var stored = await LoadShiftAsync(employeeId);
        Assert.Equal(start, stored.ClockIn.Utc);
        Assert.Equal(start + Duration.FromNanoseconds(100), stored.ClockOut!.Utc);
        Assert.True(stored.ClockOut.Utc > stored.ClockIn.Utc);
    }

    [SqlServerFact]
    public async Task A_sub_tick_instant_is_floored_and_the_stored_value_round_trips_equal()
    {
        var instant = Instant.FromUtc(2026, 1, 12, 7, 0, 0) + Duration.FromNanoseconds(1234567_89); // 0.123456789 s
        var clock = new FakeTrustedClock(instant);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, ClockIn, user);

        Assert.Equal(HttpStatusCode.Created, reply.Status);
        var floored = Instant.FromUtc(2026, 1, 12, 7, 0, 0) + Duration.FromNanoseconds(1234567_00);
        Assert.Equal(floored, (await LoadShiftAsync(employeeId)).ClockIn.Utc);
        Assert.Equal("2026-01-12T07:00:00.1234567Z", reply.Body.GetProperty("clockIn").GetProperty("utc").GetString());
    }

    [SqlServerFact]
    public async Task Sub_tick_clock_in_after_clock_out_is_not_a_500()
    {
        var clock = new FakeTrustedClock(Instant.FromUtc(2026, 1, 12, 7, 0, 0));
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set(Instant.FromUtc(2026, 1, 12, 8, 0, 0));
        await SendAsync(client, ClockOut, user);
        clock.Set(Instant.FromUtc(2026, 1, 12, 8, 0, 0) + Duration.FromNanoseconds(50));

        var reply = await SendAsync(client, ClockIn, user);

        Assert.NotEqual(HttpStatusCode.InternalServerError, reply.Status);
    }

    /// <summary>
    /// The punch path has no upper bound of its own beyond the source's year window [2026, 2100): one wrong but in-range
    /// reading (2099) is accepted, and from then on every honest punch is 503 went_backwards. Observed and accepted
    /// (rule 1 forbids comparing with the local clock; the two-provider cross-check is the defence): see edge-case row "far-future in-range instant" and D-030.
    /// </summary>
    [SqlServerFact]
    public async Task A_far_future_but_in_range_instant_is_accepted_and_then_blocks_honest_punches_until_a_manager_fixes_it()
    {
        var clock = new FakeTrustedClock("2099-12-31T23:59:59Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var bad = await SendAsync(client, ClockIn, user);
        clock.Set("2026-01-12T07:00:00Z"); // the provider is right again
        var honest = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.Created, bad.Status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, honest.Status);
        Assert.Equal(ErrorCodes.TimeWentBackwards, honest.Body.GetProperty("code").GetString());
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true)); // stuck open
    }

    [SqlServerFact]
    public async Task A_1900_instant_is_refused_without_a_500()
    {
        var clock = new FakeTrustedClock("1900-01-01T00:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, ClockIn, user);

        Assert.NotEqual(HttpStatusCode.InternalServerError, reply.Status);
    }

    /// <summary>Out-of-window instants must never reach the database as a 500 even if the clock layer let one through.</summary>
    [SqlServerTheory]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("9999-12-31T23:59:59Z")]
    public async Task An_absurd_instant_from_the_clock_is_503_invalid_response_and_nothing_is_saved(string utc)
    {
        var clock = new FakeTrustedClock(utc);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, ClockIn, user);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.Status);
        Assert.Equal(ErrorCodes.TimeInvalidResponse, reply.Body.GetProperty("code").GetString());
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.DoesNotContain("0001", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("9999", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", reply.Text, StringComparison.Ordinal);
    }

    [SqlServerTheory]
    [InlineData("2026-01-01T00:00:00Z", true)]            // first instant inside
    [InlineData("2025-12-31T23:59:59.9999999Z", false)]   // one tick before
    [InlineData("2099-12-31T23:59:59.9999999Z", true)]    // last tick inside
    [InlineData("2100-01-01T00:00:00Z", false)]           // first instant outside
    public async Task The_plausible_window_boundaries_are_exact(string utc, bool accepted)
    {
        var clock = new FakeTrustedClock(utc);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, ClockIn, user);

        if (accepted)
        {
            Assert.Equal(HttpStatusCode.Created, reply.Status);
            Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));
        }
        else
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.Status);
            Assert.Equal(ErrorCodes.TimeInvalidResponse, reply.Body.GetProperty("code").GetString());
            Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        }
    }

    /// <summary>
    /// Shift.Close throws InvalidOperationException for out &lt;= in. ClockOutAsync runs the guard first, but the guard
    /// compares against the latest punch of ANY shift. Here the clock-in is the latest punch, so Close can only throw if
    /// the guard is skipped; this drives out-of-order data through every ordering the DB can hold.
    /// </summary>
    [SqlServerTheory]
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T07:00:00Z")]
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T06:59:59.999Z")]
    [InlineData("2026-01-12T07:00:00Z", "2025-01-12T07:00:00Z")]
    public async Task Close_with_out_not_after_in_is_always_the_guards_503_never_an_InvalidOperationException_500(string inUtc, string outUtc)
    {
        var clock = new FakeTrustedClock(inUtc);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user);
        clock.Set(outUtc);

        var reply = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.Status);
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));
    }

    /// <summary>
    /// An older closed shift whose clock-out is LATER than the open shift's clock-in cannot exist through the API, but a
    /// manager correction (or raw SQL) could create it. The guard uses the latest punch overall, so this is still 503.
    /// </summary>
    [SqlServerFact]
    public async Task A_corrupt_history_with_a_later_closed_shift_still_ends_in_503_not_500()
    {
        var clock = new FakeTrustedClock("2026-01-12T12:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user); // open at 12:00
        var closed = ShiftRows.ValidClosed(employeeId); // 07:00 - 15:30, later than the open shift's clock-in
        await ShiftRows.InsertAsync(database.ConnectionString, closed);
        clock.Set("2026-01-12T13:00:00Z");

        var reply = await SendAsync(client, ClockOut, user);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.Status);
        Assert.Equal(ErrorCodes.TimeWentBackwards, reply.Body.GetProperty("code").GetString());
    }

    // ---------- Helpers ----------

    private sealed record Reply(HttpStatusCode Status, string Text, JsonElement Body);

    private async Task<(int Id, string Username)> AddEmployeeAsync()
    {
        await using var db = database.CreateContext();
        var employee = NewEmployee();
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (employee.Id, employee.Username);
    }

    private async Task<ClockManager.Api.Domain.Shift> LoadShiftAsync(int employeeId)
    {
        await using var db = database.CreateContext();
        return await db.Shifts.AsNoTracking().SingleAsync(s => s.EmployeeId == employeeId);
    }

    private static async Task<Reply> SendAsync(HttpClient client, string body, string user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/punches")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Test-User", user);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var response = await client.SendAsync(request, cts.Token);
        var text = await response.Content.ReadAsStringAsync(cts.Token);
        var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return new Reply(response.StatusCode, text, json);
    }
}
