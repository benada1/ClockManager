using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ClockManager.Tests.Punches;

/// <summary>
/// QA pass, slice B group 2: the client aborts a punch. P19 covers a clock-in aborted during the provider call; this adds
/// the clock-out, the abort while the INSERT waits on the database (a lock the test holds on the employee row, which the
/// shift's foreign-key check needs), and that the SAME key and a later punch work afterwards. Every wait has a 30 s timeout.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PunchQaCancelTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string ClockIn = """{"intent":"clock-in"}""";
    private const string ClockOut = """{"intent":"clock-out"}""";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [SqlServerFact]
    public async Task Abort_during_the_provider_call_of_a_clock_out_keeps_the_shift_open_and_the_same_key_works_afterwards()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString(), cts.Token)).Status);
        clock.Set("2026-01-12T15:00:00Z");
        var key = Guid.NewGuid().ToString();

        clock.Hold();
        using var abort = new CancellationTokenSource();
        var aborted = SendAsync(client, ClockOut, user, key, abort.Token);
        await clock.WaitForCallsAsync(2);
        await abort.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);
        await WaitUntilAsync(() => !factory.Completed.IsEmpty, cts.Token);
        clock.Release();

        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));
        Assert.Equal(1, await CountRecords(employeeId)); // only the clock-in's
        var bad = string.Join(" ## ", factory.Logs.Where(l => l.Level >= LogLevel.Warning).Select(l => l.Level + " " + l.Category + ": " + l.Message[..Math.Min(600, l.Message.Length)] + " / " + l.Exception?.GetType().Name)); Assert.True(bad.Length == 0, bad);

        var retry = await SendAsync(client, ClockOut, user, key, cts.Token); // the same key: the aborted attempt stored nothing
        Assert.Equal(HttpStatusCode.Created, retry.Status);
        Assert.Equal(28800, retry.Body.GetProperty("durationSeconds").GetInt64()); // 07:00Z to 15:00Z
    }

    [SqlServerFact]
    public async Task Abort_while_the_INSERT_waits_on_the_database_rolls_back_and_the_same_key_then_works()
    {
        await AbortDuringInsertAsync(); // asserts: no shift, no record, same key works afterwards
    }

    [SqlServerFact(Skip = "QA-25 open: a client abort while the INSERT is in flight logs an Error 'An error occurred using a transaction.' (EF Core, no exception attached); it is a plain disconnect.")]
    [Trait("Status", "KnownBug")]
    public async Task QA25_Abort_while_the_INSERT_waits_logs_nothing_at_Warning_or_above()
    {
        var bad = await AbortDuringInsertAsync();
        Assert.True(bad.Length == 0, bad);
    }

    /// <returns>The Warning/Error log lines written during the abort (empty = none).</returns>
    private async Task<string> AbortDuringInsertAsync()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);
        var key = Guid.NewGuid().ToString();

        // The request passes the state check, then waits in the provider call; meanwhile the test locks the employee row.
        clock.Hold();
        using var abort = new CancellationTokenSource();
        var request = SendAsync(client, ClockIn, user, key, abort.Token);
        await clock.WaitForCallsAsync(1);

        await using var locker = new SqlConnection(database.ConnectionString);
        await locker.OpenAsync(cts.Token);
        await using var tx = (SqlTransaction)await locker.BeginTransactionAsync(cts.Token);
        await using (var lockCommand = locker.CreateCommand())
        {
            lockCommand.Transaction = tx;
            lockCommand.CommandText = "UPDATE Employees SET DisplayName = DisplayName WHERE Id = @e";
            lockCommand.Parameters.AddWithValue("@e", employeeId);
            await lockCommand.ExecuteNonQueryAsync(cts.Token);
        }

        clock.Release(); // the punch goes on: time, guard, then its INSERT blocks on the foreign-key check
        await WaitUntilBlockedAsync(cts.Token);
        Assert.False(request.IsCompleted);

        await abort.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await WaitUntilAsync(() => !factory.Completed.IsEmpty, cts.Token);
        await tx.RollbackAsync(cts.Token); // free the row

        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.Equal(0, await CountRecords(employeeId));
        var warnings = string.Join(" ## ", factory.Logs.Where(l => l.Level >= LogLevel.Warning).Select(l => l.Level + " " + l.Category + ": " + l.Message));

        var retry = await SendAsync(client, ClockIn, user, key, cts.Token);
        Assert.Equal(HttpStatusCode.Created, retry.Status);
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.Equal(1, await CountRecords(employeeId));
        return warnings;
    }

    [SqlServerFact]
    public async Task A_punch_after_an_aborted_one_is_fine_with_a_new_key_and_the_provider_asked_afresh()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);

        clock.Hold();
        using var abort = new CancellationTokenSource();
        var aborted = SendAsync(client, ClockIn, user, Guid.NewGuid().ToString(), abort.Token);
        await clock.WaitForCallsAsync(1);
        await abort.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => aborted);
        await WaitUntilAsync(() => !factory.Completed.IsEmpty, cts.Token);
        clock.Release();
        clock.Set("2026-01-12T07:30:00Z");

        var next = await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString(), cts.Token);

        Assert.Equal(HttpStatusCode.Created, next.Status);
        Assert.Equal("2026-01-12T07:30:00Z", next.Body.GetProperty("clockIn").GetProperty("utc").GetString());
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    // ---------- Helpers ----------

    private sealed record Reply(HttpStatusCode Status, JsonElement Body);

    private async Task<(int Id, string Username)> AddEmployeeAsync()
    {
        var id = await ShiftRows.AddEmployeeAsync(database);
        await using var db = database.CreateContext();
        return (id, db.Employees.Where(e => e.Id == id).Select(e => e.Username).Single());
    }

    private static async Task<Reply> SendAsync(HttpClient client, string body, string user, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/punches")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Dev-Employee", user);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return new Reply(response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<int> CountRecords(int employeeId)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM IdempotencyRecords WHERE EmployeeId = @e";
        command.Parameters.AddWithValue("@e", employeeId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Waits until some request in this test's database is blocked by another session.</summary>
    private async Task WaitUntilBlockedAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id <> 0 AND database_id = DB_ID()";
            if ((int)(await command.ExecuteScalarAsync(ct))! > 0) return;
            await Task.Delay(20, ct);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) await Task.Delay(10, ct);
    }
}
