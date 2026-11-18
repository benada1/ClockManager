using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Api.Common.Errors;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using Microsoft.Data.SqlClient;
using NodaTime;

namespace ClockManager.Tests.Punches;

/// <summary>
/// QA pass, slice B group 1: bigger storms than P5/P7/P12 (which use 10 hold-synchronised requests). Here: more racers,
/// no hold (real scheduling races), clock-in and clock-out mixed, many employees at once, and long sequential cycles.
/// Invariants after every storm: never two open shifts, every 201 matches a stored row, idempotency records == 201s,
/// no orphan record or shift. Every wait has a 30 s timeout.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PunchQaConcurrencyTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string ClockIn = """{"intent":"clock-in"}""";
    private const string ClockOut = """{"intent":"clock-out"}""";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Thirty_parallel_clock_ins_with_the_SAME_key_give_one_row_one_record_and_every_201_identical(int round)
    {
        _ = round;
        var (replies, employeeId, user, client) = await SameKeyStormAsync();
        using var _c = client.Factory;

        var created = replies.Where(r => r.Status == HttpStatusCode.Created).ToList();
        Assert.NotEmpty(created);
        Assert.All(replies.Where(r => r.Status != HttpStatusCode.Created), r =>
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.Status); // QA-24: should have been the replay
            Assert.Equal(ErrorCodes.TimeWentBackwards, r.Body.GetProperty("code").GetString());
        });
        Assert.Single(created.Select(r => r.Text).Distinct(StringComparer.Ordinal));
        Assert.Equal(1, await Count("Shifts", employeeId));
        Assert.Equal(1, await Count("IdempotencyRecords", employeeId));
    }

    [SqlServerTheory(Skip = "QA-24 open: a parallel request with the SAME key can get 503 time.went_backwards instead of the stored 201 (same root cause: last-punch snapshot after the state check; the replay catch only handles 409s).")]
    [Trait("Status", "KnownBug")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task QA24_Thirty_parallel_clock_ins_with_the_SAME_key_all_get_the_same_201(int round)
    {
        _ = round;
        var (replies, _, _, client) = await SameKeyStormAsync();
        using var _c = client.Factory;

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.Created, r.Status));
        Assert.Single(replies.Select(r => r.Text).Distinct(StringComparer.Ordinal));
    }

    private async Task<(Reply[] Replies, int EmployeeId, string User, (PunchApiFactory Factory, HttpClient Http) Client)> SameKeyStormAsync()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        var factory = new PunchApiFactory(database, clock);
        var http = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();
        using var cts = new CancellationTokenSource(Limit);

        var replies = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => SendAsync(http, ClockIn, user, key, cts.Token)))
            .WaitAsync(cts.Token);
        return (replies, employeeId, user, (factory, http));
    }
    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Thirty_parallel_clock_ins_with_DIFFERENT_keys_give_exactly_one_201_one_row_and_only_409_or_went_backwards_for_the_rest(int round)
    {
        _ = round;
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);

        var replies = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => SendAsync(client, ClockIn, user, null, cts.Token)))
            .WaitAsync(cts.Token);

        var winner = Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
        Assert.All(replies.Where(r => r != winner), r =>
        {
            Assert.Contains(r.Status, new[] { HttpStatusCode.Conflict, HttpStatusCode.ServiceUnavailable });
            Assert.Contains(r.Body.GetProperty("code").GetString(), new[] { ErrorCodes.ShiftAlreadyOpen, ErrorCodes.TimeWentBackwards });
        });
        Assert.Equal(1, await Count("Shifts", employeeId));
        Assert.Equal(1, await Count("IdempotencyRecords", employeeId));
    }

    [SqlServerTheory(Skip = "QA-24 open: the loser of a clock-in race can get 503 time.went_backwards instead of 409 shift.already_open (last-punch snapshot is read after the state check).")]
    [Trait("Status", "KnownBug")]
    [InlineData(1)]
    [InlineData(2)]
    public async Task QA24_Thirty_parallel_clock_ins_with_DIFFERENT_keys_give_exactly_one_201_and_every_loser_409(int round)
    {
        _ = round;
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);

        var replies = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => SendAsync(client, ClockIn, user, null, cts.Token)))
            .WaitAsync(cts.Token);

        var winner = Assert.Single(replies, r => r.Status == HttpStatusCode.Created);
        Assert.All(replies.Where(r => r != winner), r =>
        {
            Assert.Equal(HttpStatusCode.Conflict, r.Status);
            Assert.Equal(ErrorCodes.ShiftAlreadyOpen, r.Body.GetProperty("code").GetString());
        });
        Assert.Equal(1, await Count("Shifts", employeeId));
        Assert.Equal(1, await Count("IdempotencyRecords", employeeId)); // a 409 stores nothing
    }

    [SqlServerFact]
    public async Task An_interleaved_clock_in_clock_out_storm_keeps_counts_records_and_the_open_shift_rule_consistent()
    {
        var (replies, shifts, employeeId) = await RunStormAsync();

        // Only these outcomes are legal. A 500 or any other code would be a bug.
        Assert.All(replies, r =>
        {
            Assert.Contains(r.Status, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.ServiceUnavailable });
            if (r.Status == HttpStatusCode.Conflict)
                Assert.Contains(r.Body.GetProperty("code").GetString(), new[] { ErrorCodes.ShiftAlreadyOpen, ErrorCodes.ShiftNotOpen });
            if (r.Status == HttpStatusCode.ServiceUnavailable)
                Assert.Equal(ErrorCodes.TimeWentBackwards, r.Body.GetProperty("code").GetString()); // equal instants within one tick
        });

        var created = replies.Where(r => r.Status == HttpStatusCode.Created).ToList();
        Assert.NotEmpty(created);
        Assert.True(shifts.Count(s => s.OutUtc is null) <= 1, "two open shifts");
        Assert.Equal(created.Count, await Count("IdempotencyRecords", employeeId)); // records == successful punches
        Assert.Equal(created.Count(r => r.Body.GetProperty("clockOut").ValueKind == JsonValueKind.Null), shifts.Count);
        Assert.Equal(created.Count(r => r.Body.GetProperty("clockOut").ValueKind != JsonValueKind.Null), shifts.Count(s => s.OutUtc is not null));
        foreach (var r in created) Assert.Contains(shifts, s => s.Id == r.Body.GetProperty("shiftId").GetInt64());
        Assert.All(shifts.Where(s => s.OutUtc is not null), s => Assert.True(s.OutUtc > s.InUtc));
    }

    [SqlServerFact(Skip = "QA-23 open: the monotonic guard is not atomic with the INSERT, so a punch stalled between them can land before a newer punch of the same employee (shifts overlap).")]
    [Trait("Status", "KnownBug")]
    public async Task QA23_a_storm_never_leaves_overlapping_or_touching_shifts()
    {
        for (var round = 0; round < 6; round++)
        {
            var (_, shifts, _) = await RunStormAsync();
            DateTimeOffset? previousEnd = null;
            foreach (var s in shifts.OrderBy(s => s.InUtc).ThenBy(s => s.Id))
            {
                if (previousEnd is not null)
                    Assert.True(s.InUtc > previousEnd, "shifts overlap or touch: " + string.Join(" | ", shifts.OrderBy(x => x.InUtc).Select(x => $"{x.Id}:{x.InUtc:O}->{x.OutUtc:O}")));
                previousEnd = s.OutUtc ?? s.InUtc;
            }
        }
    }

    /// <summary>80 alternating clock-in / clock-out requests for ONE employee, no hold, while the fake time advances 1 s every 3 ms.</summary>
    private async Task<(Reply[] Replies, List<ShiftRow> Shifts, int EmployeeId)> RunStormAsync()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);

        using var stopTicker = new CancellationTokenSource();
        var ticker = Task.Run(async () =>
        {
            while (!stopTicker.IsCancellationRequested)
            {
                clock.Advance(Duration.FromSeconds(1));
                await Task.Delay(3);
            }
        });

        var requests = Enumerable.Range(0, 80)
            .Select(i => Task.Run(() => SendAsync(client, i % 2 == 0 ? ClockIn : ClockOut, user, null, cts.Token)))
            .ToArray();
        var replies = await Task.WhenAll(requests).WaitAsync(cts.Token);
        await stopTicker.CancelAsync();
        await ticker;
        return (replies, await ReadShiftsAsync(employeeId), employeeId);
    }

    [SqlServerFact]
    public async Task Parallel_punches_of_twenty_different_employees_all_succeed_and_each_has_only_their_own_shift()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var people = new List<(int Id, string User)>();
        for (var i = 0; i < 20; i++) people.Add(await AddEmployeeAsync());
        using var cts = new CancellationTokenSource(Limit);

        var ins = await Task.WhenAll(people.Select(p => SendAsync(client, ClockIn, p.User, null, cts.Token))).WaitAsync(cts.Token);
        clock.Set("2026-01-12T15:00:00Z");
        var outs = await Task.WhenAll(people.Select(p => SendAsync(client, ClockOut, p.User, null, cts.Token))).WaitAsync(cts.Token);

        Assert.All(ins.Concat(outs), r => Assert.Equal(HttpStatusCode.Created, r.Status));
        for (var i = 0; i < people.Count; i++)
        {
            Assert.Equal(ins[i].Body.GetProperty("shiftId").GetInt64(), outs[i].Body.GetProperty("shiftId").GetInt64());
            var mine = await ReadShiftsAsync(people[i].Id);
            var shift = Assert.Single(mine);
            Assert.Equal(ins[i].Body.GetProperty("shiftId").GetInt64(), shift.Id);
            Assert.NotNull(shift.OutUtc);
            Assert.Equal(2, await Count("IdempotencyRecords", people[i].Id));
        }
        Assert.Equal(20, outs.Select(o => o.Body.GetProperty("shiftId").GetInt64()).Distinct().Count());
    }

    [SqlServerFact]
    public async Task Thirty_sequential_cycles_across_the_DST_end_have_durations_from_UTC_and_strict_ordering()
    {
        var clock = new FakeTrustedClock("2026-10-24T20:00:00Z"); // 5 h before the October change
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);

        var expected = new List<long>();
        for (var i = 0; i < 30; i++)
        {
            var seconds = 1800 + i * 61; // 30 min + a growing odd amount: the shifts straddle 01:00Z
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(client, ClockIn, user, null, cts.Token)).Status);
            clock.Advance(Duration.FromSeconds(seconds));
            var o = await SendAsync(client, ClockOut, user, null, cts.Token);
            Assert.Equal(HttpStatusCode.Created, o.Status);
            Assert.Equal(seconds, o.Body.GetProperty("durationSeconds").GetInt64());
            expected.Add(seconds);
            clock.Advance(Duration.FromSeconds(1)); // the next clock-in must be strictly after this clock-out
        }

        var shifts = (await ReadShiftsAsync(employeeId)).OrderBy(s => s.InUtc).ToList();
        Assert.Equal(30, shifts.Count);
        Assert.Equal(expected, shifts.Select(s => (long)(s.OutUtc!.Value - s.InUtc).TotalSeconds));
        for (var i = 1; i < shifts.Count; i++) Assert.True(shifts[i].InUtc > shifts[i - 1].OutUtc);
        Assert.Equal(60, await Count("IdempotencyRecords", employeeId));
        Assert.Equal(0, await ShiftRows.CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));
    }

    [SqlServerFact]
    public async Task Failures_leave_no_orphan_shift_or_record_a_503_storm_then_a_clean_punch()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        clock.FailWith(FakeTrustedClock.Unavailable());
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);

        var failed = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => SendAsync(client, ClockIn, user, null, cts.Token))).WaitAsync(cts.Token);

        Assert.All(failed, r => Assert.Equal(HttpStatusCode.ServiceUnavailable, r.Status));
        Assert.Equal(0, await Count("Shifts", employeeId));
        Assert.Equal(0, await Count("IdempotencyRecords", employeeId));
        clock.Recover();
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client, ClockIn, user, null, cts.Token)).Status);
    }

    // ---------- Helpers ----------

    private sealed record Reply(HttpStatusCode Status, string Text, JsonElement Body);

    private sealed record ShiftRow(long Id, DateTimeOffset InUtc, DateTimeOffset? OutUtc);

    private async Task<(int Id, string Username)> AddEmployeeAsync()
    {
        var id = await ShiftRows.AddEmployeeAsync(database);
        await using var db = database.CreateContext();
        return (id, db.Employees.Where(e => e.Id == id).Select(e => e.Username).Single());
    }

    private static async Task<Reply> SendAsync(HttpClient client, string body, string user, string? key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/punches")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Dev-Employee", user);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return new Reply(response.StatusCode, text, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<int> Count(string table, int employeeId)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE EmployeeId = @e";
        command.Parameters.AddWithValue("@e", employeeId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<ShiftRow>> ReadShiftsAsync(int employeeId)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ClockIn_Utc, ClockOut_Utc FROM Shifts WHERE EmployeeId = @e";
        command.Parameters.AddWithValue("@e", employeeId);
        var rows = new List<ShiftRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new ShiftRow(reader.GetInt64(0), reader.GetDateTimeOffset(1), reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2)));
        return rows;
    }
}
