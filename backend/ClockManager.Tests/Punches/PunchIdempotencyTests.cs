using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Api.Common.Errors;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using Microsoft.Data.SqlClient;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Punches;

/// <summary>
/// Idempotent punches over HTTP against a real SQL Server (plans/03-punch-api.md §5 and §6: P6, P7, P8, P10, part of P16;
/// D-032). The trusted clock is a fake, so "the provider was asked N times" is an exact assertion.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PunchIdempotencyTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string ClockIn = """{"intent":"clock-in"}""";
    private const string ClockOut = """{"intent":"clock-out"}""";

    // ---- P6: the same request twice (a lost response) ----

    [SqlServerFact]
    public async Task P6_same_request_twice_returns_the_same_status_and_body_one_row_and_one_provider_call()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();

        var first = await SendAsync(client, ClockIn, user, key);
        clock.Set("2026-01-12T07:05:00Z"); // a replay must not even look at the new time
        var second = await SendAsync(client, ClockIn, user, key);

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Text, second.Text); // byte for byte
        Assert.Equal(1, clock.Calls);
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.Equal(1, await CountRecordsAsync(employeeId));
    }

    [SqlServerFact]
    public async Task A_replay_stores_the_trusted_instant_of_the_punch_not_a_local_clock()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());

        Assert.Equal(DateTimeOffset.Parse("2026-01-12T07:00:00Z"), await ReadRecordCreatedUtcAsync(employeeId));
    }

    [SqlServerFact]
    public async Task A_replay_works_while_the_provider_is_down_because_it_never_asks_the_provider()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();
        var first = await SendAsync(client, ClockIn, user, key);
        clock.FailWith(FakeTrustedClock.Unavailable());

        var replay = await SendAsync(client, ClockIn, user, key);

        Assert.Equal(HttpStatusCode.Created, replay.Status);
        Assert.Equal(first.Text, replay.Text);
        Assert.Equal(1, clock.Calls);
    }

    // ---- Replay after the state moved on ----

    [SqlServerFact]
    public async Task A_clock_in_key_still_replays_after_the_shift_was_closed_and_a_clock_out_key_replays_the_clock_out()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var inKey = Guid.NewGuid().ToString();
        var outKey = Guid.NewGuid().ToString();
        var clockedIn = await SendAsync(client, ClockIn, user, inKey);
        clock.Set("2026-01-12T15:30:00Z");
        var clockedOut = await SendAsync(client, ClockOut, user, outKey);

        var inReplay = await SendAsync(client, ClockIn, user, inKey);
        var outReplay = await SendAsync(client, ClockOut, user, outKey);

        // The clock-in key returns the ORIGINAL answer: the shift as it was then (still open), not as it is now.
        Assert.Equal(HttpStatusCode.Created, inReplay.Status);
        Assert.Equal(clockedIn.Text, inReplay.Text);
        Assert.Equal(JsonValueKind.Null, inReplay.Body.GetProperty("clockOut").ValueKind);
        Assert.Equal(JsonValueKind.Null, inReplay.Body.GetProperty("durationSeconds").ValueKind);

        Assert.Equal(HttpStatusCode.Created, outReplay.Status);
        Assert.Equal(clockedOut.Text, outReplay.Text);
        Assert.Equal(30600, outReplay.Body.GetProperty("durationSeconds").GetInt64());
        Assert.Equal(clockedIn.Body.GetProperty("shiftId").GetInt64(), outReplay.Body.GetProperty("shiftId").GetInt64());

        Assert.Equal(2, clock.Calls);
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.Equal(2, await CountRecordsAsync(employeeId));
    }

    // ---- Scope: per employee ----

    [SqlServerFact]
    public async Task The_same_key_used_by_two_employees_is_independent_and_nobody_reads_the_other_ones_reply()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (aliceId, alice) = await AddEmployeeAsync();
        var (bobId, bob) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();

        var forAlice = await SendAsync(client, ClockIn, alice, key);
        clock.Set("2026-01-12T07:01:00Z");
        var forBob = await SendAsync(client, ClockIn, bob, key);

        Assert.Equal(HttpStatusCode.Created, forAlice.Status);
        Assert.Equal(HttpStatusCode.Created, forBob.Status);
        Assert.NotEqual(forAlice.Body.GetProperty("shiftId").GetInt64(), forBob.Body.GetProperty("shiftId").GetInt64());
        Assert.Equal("2026-01-12T07:01:00Z", forBob.Body.GetProperty("clockIn").GetProperty("utc").GetString());
        Assert.Equal(2, clock.Calls);
        Assert.Equal(1, await CountRecordsAsync(aliceId));
        Assert.Equal(1, await CountRecordsAsync(bobId));

        // And each one's replay is their own.
        var aliceAgain = await SendAsync(client, ClockIn, alice, key);
        Assert.Equal(forAlice.Text, aliceAgain.Text);
    }

    // ---- P8: the same key for the other intent ----

    [SqlServerTheory]
    [InlineData(true)]  // key first used for clock-in, then sent with clock-out
    [InlineData(false)] // key first used for clock-out, then sent with clock-in
    public async Task P8_same_key_with_the_other_intent_is_422_key_reused_and_nothing_changes(bool firstIsClockIn)
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();
        if (!firstIsClockIn)
        {
            await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());
            clock.Set("2026-01-12T15:30:00Z");
        }

        var original = await SendAsync(client, firstIsClockIn ? ClockIn : ClockOut, user, key);
        clock.Set("2026-01-12T16:00:00Z");
        var calls = clock.Calls;
        var reused = await SendAsync(client, firstIsClockIn ? ClockOut : ClockIn, user, key);

        Assert.Equal(HttpStatusCode.Created, original.Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.Status);
        Assert.Equal(ErrorCodes.IdempotencyKeyReused, reused.Body.GetProperty("code").GetString());
        Assert.Equal(calls, clock.Calls); // no provider call
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.Equal(firstIsClockIn ? 1 : 2, await CountRecordsAsync(employeeId));
        // The stored original is untouched: sending it again with its own intent still replays it.
        var again = await SendAsync(client, firstIsClockIn ? ClockIn : ClockOut, user, key);
        Assert.Equal(original.Text, again.Text);
    }

    // ---- P10 and "only successes are stored" ----

    [SqlServerFact]
    public async Task P10_retry_with_the_same_key_after_a_503_really_retries_because_the_failure_was_not_stored()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        clock.FailWith(FakeTrustedClock.Unavailable());
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();

        var failed = await SendAsync(client, ClockIn, user, key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.Status);
        Assert.Equal(0, await CountRecordsAsync(employeeId));
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));

        clock.Recover();
        var retried = await SendAsync(client, ClockIn, user, key);

        Assert.Equal(HttpStatusCode.Created, retried.Status);
        Assert.Equal(2, clock.Calls);
        Assert.Equal(1, await CountRecordsAsync(employeeId));
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task A_409_is_not_stored_so_the_same_key_works_once_the_state_allows_it()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();

        var refused = await SendAsync(client, ClockOut, user, key); // nothing to clock out of
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal(0, await CountRecordsAsync(employeeId));

        await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());
        clock.Set("2026-01-12T15:30:00Z");
        var accepted = await SendAsync(client, ClockOut, user, key); // same key, same intent, now valid

        Assert.Equal(HttpStatusCode.Created, accepted.Status);
        Assert.Equal(2, await CountRecordsAsync(employeeId));
    }

    [SqlServerFact]
    public async Task A_new_key_is_a_new_punch_a_clock_in_with_a_new_key_while_open_is_still_409()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());

        var other = await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Conflict, other.Status);
        Assert.Equal(ErrorCodes.ShiftAlreadyOpen, other.Body.GetProperty("code").GetString());
    }

    // ---- P7: the same key in parallel ----

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task P7_the_same_clock_in_key_ten_times_in_parallel_gives_one_row_and_identical_answers(int round)
    {
        _ = round; // independent rounds: one lucky pass is not proof
        const int racers = 10;
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();

        // All 10 are past the replay lookup (no record exists yet) and wait in the "provider call"; then they all save.
        clock.Hold();
        var requests = Enumerable.Range(0, racers).Select(_ => SendAsync(client, ClockIn, user, key)).ToArray();
        await clock.WaitForCallsAsync(racers);
        clock.Release();
        var results = await Task.WhenAll(requests);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Single(results.Select(r => r.Text).Distinct()); // identical answers, byte for byte
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.Equal(1, await CountRecordsAsync(employeeId));
    }

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task P7_the_same_clock_out_key_in_parallel_gives_one_clock_out_and_identical_answers(int round)
    {
        _ = round;
        const int racers = 5;
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());
        clock.Set("2026-01-12T15:30:00Z");
        var key = Guid.NewGuid().ToString();

        clock.Hold();
        var requests = Enumerable.Range(0, racers).Select(_ => SendAsync(client, ClockOut, user, key)).ToArray();
        await clock.WaitForCallsAsync(1 + racers);
        clock.Release();
        var results = await Task.WhenAll(requests);

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Single(results.Select(r => r.Text).Distinct());
        Assert.Equal(30600, results[0].Body.GetProperty("durationSeconds").GetInt64());
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.Equal(2, await CountRecordsAsync(employeeId)); // the clock-in's and this clock-out's
    }

    [SqlServerFact]
    public async Task The_shift_and_its_record_are_one_transaction_a_failing_record_insert_rolls_the_shift_back()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        // A trigger makes every record INSERT fail AFTER the shift INSERT succeeded in the same transaction.
        await ExecuteAsync("CREATE TRIGGER TR_IdempotencyRecords_Test_Fail ON IdempotencyRecords AFTER INSERT AS THROW 50001, 'test: record insert fails', 1;");
        try
        {
            var failed = await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());

            Assert.Equal(HttpStatusCode.InternalServerError, failed.Status);
            Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId)); // rolled back with it
            Assert.Equal(0, await CountRecordsAsync(employeeId));
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER TR_IdempotencyRecords_Test_Fail;");
        }

        // The same user can punch normally afterwards: nothing was left open.
        var ok = await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, ok.Status);
    }

    [SqlServerFact]
    public async Task Every_successful_punch_stores_exactly_one_record_next_to_its_shift()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());
        clock.Set("2026-01-12T15:30:00Z");
        await SendAsync(client, ClockOut, user, Guid.NewGuid().ToString());
        clock.Set("2026-01-13T07:00:00Z");
        await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());

        Assert.Equal(2, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.Equal(3, await CountRecordsAsync(employeeId)); // 2 clock-ins + 1 clock-out
        Assert.Equal(2, await CountRecordsAsync(employeeId, "clock-in"));
    }

    // ---------- Helpers ----------

    private sealed record Reply(HttpStatusCode Status, string Text, JsonElement Body);

    private async Task<(int Id, string Username)> AddEmployeeAsync()
    {
        var id = await ShiftRows.AddEmployeeAsync(database);
        await using var db = database.CreateContext();
        var username = db.Employees.Where(e => e.Id == id).Select(e => e.Username).Single();
        return (id, username);
    }

    private static async Task<Reply> SendAsync(HttpClient client, string body, string user, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/punches")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Test-User", user);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return new Reply(response.StatusCode, text, json);
    }

    private async Task<int> CountRecordsAsync(int employeeId, string? intent = null)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM IdempotencyRecords WHERE EmployeeId = @e"
            + (intent is null ? string.Empty : " AND Intent = @i");
        command.Parameters.AddWithValue("@e", employeeId);
        if (intent is not null) command.Parameters.AddWithValue("@i", intent);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<DateTimeOffset> ReadRecordCreatedUtcAsync(int employeeId)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CreatedUtc FROM IdempotencyRecords WHERE EmployeeId = @e";
        command.Parameters.AddWithValue("@e", employeeId);
        return (DateTimeOffset)(await command.ExecuteScalarAsync())!;
    }
}
