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
/// QA pass, slice A group 2: Idempotency-Key edge cases beyond P6-P10 (PunchIdempotencyTests): spelling variants of one
/// UUID, malformed or duplicated headers, cross-employee use, and a replay after the employee was deactivated.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PunchQaKeyTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string ClockIn = """{"intent":"clock-in"}""";
    private const string ClockOut = """{"intent":"clock-out"}""";

    [SqlServerFact]
    public async Task The_same_UUID_in_upper_and_lower_case_is_ONE_key_and_replays()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString("D");

        var first = await SendAsync(client, ClockIn, user, [key.ToLowerInvariant()]);
        var upper = await SendAsync(client, ClockIn, user, [key.ToUpperInvariant()]);

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.Created, upper.Status);
        Assert.Equal(first.Text, upper.Text);
        Assert.Equal(1, clock.Calls);
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.Equal(1, await CountRecordsAsync(employeeId));
    }

    [SqlServerTheory]
    [InlineData("{3f1c0d7e-8a4b-4c55-9a51-0d2b7c1e9f10}")]    // braces
    [InlineData("(3f1c0d7e-8a4b-4c55-9a51-0d2b7c1e9f10)")]    // parentheses
    [InlineData("urn:uuid:3f1c0d7e-8a4b-4c55-9a51-0d2b7c1e9f10")]
    [InlineData("3f1c0d7e8a4b4c559a510d2b7c1e9f10")]          // N format
    [InlineData("3f1c0d7e-8a4b-4c55-9a51-0d2b7c1e9f1")]       // one char short
    [InlineData("3f1c0d7e-8a4b-4c55-9a51-0d2b7c1e9f100")]     // one char long
    [InlineData("3f1c0d7e-8a4b-4c55-9a51-0d2b7c1e9f1g")]      // not hex
    [InlineData("3f1c0d7e-8a4b-4c55-9a51-0d2b7c1e9f10, 3f1c0d7e-8a4b-4c55-9a51-0d2b7c1e9f11")] // comma-joined list in ONE header
    [InlineData("3f1c0d7e-8a4b-4c55-9a51-0d2b7c1e9f10;x=1")]
    public async Task Other_UUID_spellings_and_junk_are_400_key_missing_and_nothing_is_saved(string key)
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, ClockIn, user, [key]);

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal(ErrorCodes.IdempotencyKeyMissing, reply.Body.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task Surrounding_whitespace_is_ignored_and_it_is_the_same_key()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();

        var first = await SendAsync(client, ClockIn, user, ["  " + key + "\t"]);
        var plain = await SendAsync(client, ClockIn, user, [key]);

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(first.Text, plain.Text);
        Assert.Equal(1, await CountRecordsAsync(employeeId));
    }

    [SqlServerFact]
    public async Task Two_Idempotency_Key_headers_are_refused_with_400_never_guessed()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();

        var reply = await SendAsync(client, ClockIn, user, [Guid.NewGuid().ToString(), Guid.NewGuid().ToString()]);

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal(ErrorCodes.IdempotencyKeyMissing, reply.Body.GetProperty("code").GetString());
        Assert.Equal(0, clock.Calls);
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task Two_identical_Idempotency_Key_headers_are_also_refused()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();

        var reply = await SendAsync(client, ClockIn, user, [key, key]);

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
    }

    /// <summary>Bob presenting alice's key must neither read alice's reply nor learn that the key exists (no 422 key_reused).</summary>
    [SqlServerFact]
    public async Task Bob_using_alices_key_with_the_other_intent_gets_his_own_normal_answer_not_alices_reply_or_a_422()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, alice) = await AddEmployeeAsync();
        var (bobId, bob) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();
        var alices = await SendAsync(client, ClockIn, alice, [key]);

        var bobs = await SendAsync(client, ClockOut, bob, [key]); // bob has no open shift

        Assert.Equal(HttpStatusCode.Conflict, bobs.Status);
        Assert.Equal(ErrorCodes.ShiftNotOpen, bobs.Body.GetProperty("code").GetString());
        Assert.DoesNotContain(alices.Body.GetProperty("shiftId").GetInt64().ToString() + ",", bobs.Text);
        Assert.Equal(0, await CountRecordsAsync(bobId));
    }

    /// <summary>
    /// Observed behaviour (D-032 order: who comes before replay): a deactivated employee gets 403 employee.inactive even
    /// for a key that has a stored answer. Sensible: an inactive account gets no data back, and the original success
    /// was already delivered or is visible to a manager. Recorded as an edge case.
    /// </summary>
    [SqlServerFact]
    public async Task Replay_after_the_employee_was_deactivated_is_403_employee_inactive_not_the_stored_answer()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();
        var first = await SendAsync(client, ClockIn, user, [key]);
        Assert.Equal(HttpStatusCode.Created, first.Status);
        await ExecuteAsync($"UPDATE Employees SET IsActive = 0 WHERE Id = {employeeId}");
        var calls = clock.Calls;

        var replay = await SendAsync(client, ClockIn, user, [key]);

        Assert.Equal(HttpStatusCode.Forbidden, replay.Status);
        Assert.Equal(ErrorCodes.EmployeeInactive, replay.Body.GetProperty("code").GetString());
        Assert.Equal(calls, clock.Calls);
        Assert.Equal(1, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId)); // the stored shift stays
    }

    [SqlServerFact]
    public async Task A_key_that_got_a_409_is_not_stored_so_it_is_free_for_the_other_intent()
    {
        // A 409 stores nothing, so the same key may later be used with the OTHER intent without a 422.
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();

        var refused = await SendAsync(client, ClockOut, user, [key]); // 409 not open
        var other = await SendAsync(client, ClockIn, user, [key]);    // same key, other intent

        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Equal(HttpStatusCode.Created, other.Status);
        Assert.Equal(1, await CountRecordsAsync(employeeId));
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

    private static async Task<Reply> SendAsync(HttpClient client, string body, string user, string[] keys)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/punches")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Dev-Employee", user);
        // One TryAdd per value = one header line each (a client sending the header twice).
        foreach (var key in keys) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        return new Reply(response.StatusCode, text, json);
    }

    private async Task<int> CountRecordsAsync(int employeeId)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM IdempotencyRecords WHERE EmployeeId = @e";
        command.Parameters.AddWithValue("@e", employeeId);
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
}
