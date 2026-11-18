using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using Microsoft.Data.SqlClient;

namespace ClockManager.Tests.Punches;

/// <summary>
/// QA pass, slice B group 3: the zurich-time-cases matrix through HTTP + SQL Server + the status read-back, with exact UTC
/// instants. PunchRulesTests already covers the mapper in memory; here the new part is the round trip: what the punch
/// response says, what the DB stored (offset in minutes, Zurich date) and what a later status call shows must agree, at
/// both DST changes, at midnight, at year end, and at the exact flag thresholds. Every wait has a 30 s timeout.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PunchQaDstHttpTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string ClockIn = """{"intent":"clock-in"}""";
    private const string ClockOut = """{"intent":"clock-out"}""";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    // in (UTC), out (UTC), duration s, in (Zurich), out (Zurich), work date, in offset min, out offset min
    [SqlServerTheory]
    // T6, both passes of the repeated October hour. 00:30Z is 02:30+02:00 (first pass), 01:15Z is 02:15+01:00 (second pass).
    [InlineData("2026-10-25T00:30:00Z", "2026-10-25T01:15:00Z", 2700, "2026-10-25T02:30:00+02:00", "2026-10-25T02:15:00+01:00", "2026-10-25", 120, 60)]
    // Both punches in the second pass: two different 02:xx strings with the SAME +01:00, later in reality and on the wall.
    [InlineData("2026-10-25T01:20:00Z", "2026-10-25T01:50:00Z", 1800, "2026-10-25T02:20:00+01:00", "2026-10-25T02:50:00+01:00", "2026-10-25", 60, 60)]
    // The last second of the first pass and the first second of the second pass.
    [InlineData("2026-10-25T00:59:59Z", "2026-10-25T01:00:00Z", 1, "2026-10-25T02:59:59+02:00", "2026-10-25T02:00:00+01:00", "2026-10-25", 120, 60)]
    // T4 over DST start: 7 h, not the 8 h the wall clock shows.
    [InlineData("2026-03-28T21:00:00Z", "2026-03-29T04:00:00Z", 25200, "2026-03-28T22:00:00+01:00", "2026-03-29T06:00:00+02:00", "2026-03-28", 60, 120)]
    // T7: the second before and the second at the DST start. 02:xx never appears.
    [InlineData("2026-03-29T00:59:59Z", "2026-03-29T01:00:00Z", 1, "2026-03-29T01:59:59+01:00", "2026-03-29T03:00:00+02:00", "2026-03-29", 60, 120)]
    // T8 year end: 23:00 local on New Year's Eve -> 07:00 on 1 January; belongs to 2026-12-31.
    [InlineData("2026-12-31T22:00:00Z", "2027-01-01T06:00:00Z", 28800, "2026-12-31T23:00:00+01:00", "2027-01-01T07:00:00+01:00", "2026-12-31", 60, 60)]
    // Clock-in at 23:00Z on New Year's Eve is already 00:00 on 1 January in Zurich: the work date is the NEW year.
    [InlineData("2026-12-31T23:00:00Z", "2027-01-01T08:00:00Z", 32400, "2027-01-01T00:00:00+01:00", "2027-01-01T09:00:00+01:00", "2027-01-01", 60, 60)]
    // Zurich midnight on the DST-end day (22:00Z on the 24th is 00:00+02:00 on the 25th) vs one second before.
    [InlineData("2026-10-24T22:00:00Z", "2026-10-25T06:00:00Z", 28800, "2026-10-25T00:00:00+02:00", "2026-10-25T07:00:00+01:00", "2026-10-25", 120, 60)]
    [InlineData("2026-10-24T21:59:59Z", "2026-10-25T06:00:00Z", 28801, "2026-10-24T23:59:59+02:00", "2026-10-25T07:00:00+01:00", "2026-10-24", 120, 60)]
    // 2027 and 2028 change dates (last Sunday: 28 March 2027, 31 October 2027, 26 March 2028, 29 October 2028).
    [InlineData("2027-03-27T21:00:00Z", "2027-03-28T04:00:00Z", 25200, "2027-03-27T22:00:00+01:00", "2027-03-28T06:00:00+02:00", "2027-03-27", 60, 120)]
    [InlineData("2027-10-30T20:00:00Z", "2027-10-31T05:00:00Z", 32400, "2027-10-30T22:00:00+02:00", "2027-10-31T06:00:00+01:00", "2027-10-30", 120, 60)]
    [InlineData("2028-03-25T21:00:00Z", "2028-03-26T04:00:00Z", 25200, "2028-03-25T22:00:00+01:00", "2028-03-26T06:00:00+02:00", "2028-03-25", 60, 120)]
    [InlineData("2028-10-28T20:00:00Z", "2028-10-29T05:00:00Z", 32400, "2028-10-28T22:00:00+02:00", "2028-10-29T06:00:00+01:00", "2028-10-28", 120, 60)]
    public async Task The_response_the_stored_row_and_the_status_read_back_agree_at_every_Zurich_boundary(
        string inUtc, string outUtc, long seconds, string inZurich, string outZurich, string workDate, short inOffsetMinutes, short outOffsetMinutes)
    {
        var clock = new FakeTrustedClock(inUtc);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);

        var @in = await SendAsync(client, HttpMethod.Post, "/api/punches", ClockIn, user, cts.Token);
        clock.Set(outUtc);
        var @out = await SendAsync(client, HttpMethod.Post, "/api/punches", ClockOut, user, cts.Token);
        var status = await SendAsync(client, HttpMethod.Get, "/api/me/status", null, user, cts.Token);

        Assert.Equal(HttpStatusCode.Created, @in.Status);
        Assert.Equal(HttpStatusCode.Created, @out.Status);
        var shift = @out.Body;
        Assert.Equal(inUtc, shift.GetProperty("clockIn").GetProperty("utc").GetString());
        Assert.Equal(outUtc, shift.GetProperty("clockOut").GetProperty("utc").GetString());
        Assert.Equal(inZurich, shift.GetProperty("clockIn").GetProperty("zurich").GetString());
        Assert.Equal(outZurich, shift.GetProperty("clockOut").GetProperty("zurich").GetString());
        Assert.Equal(workDate, shift.GetProperty("workDate").GetString());
        Assert.Equal(seconds, shift.GetProperty("durationSeconds").GetInt64());

        // What the DB stored: the offset seen at each punch and the Zurich date of the clock-in.
        var row = await ReadRowAsync(employeeId);
        Assert.Equal(inOffsetMinutes, row.InOffset);
        Assert.Equal(outOffsetMinutes, row.OutOffset);
        Assert.Equal(workDate, row.InZurichDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(seconds, (long)(row.OutUtc - row.InUtc).TotalSeconds);

        // A later status call reads the stored row back: same strings as at punch time.
        Assert.Equal(HttpStatusCode.OK, status.Status);
        Assert.Equal(JsonValueKind.Null, status.Body.GetProperty("openShift").ValueKind);
        var last = status.Body.GetProperty("lastClosedShift");
        Assert.Equal(inZurich, last.GetProperty("clockIn").GetProperty("zurich").GetString());
        Assert.Equal(outZurich, last.GetProperty("clockOut").GetProperty("zurich").GetString());
        Assert.Equal(workDate, last.GetProperty("workDate").GetString());
        Assert.Equal(seconds, last.GetProperty("durationSeconds").GetInt64());
    }

    // flags at exact thresholds over HTTP (sub-second values go through the real column)
    [SqlServerTheory]
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T23:00:00Z", 57600, "")]                           // exactly 16 h: not long
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T23:00:00.001Z", 57600, "long_shift")]            // 1 ms more: long (duration truncated to 57600 s)
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T22:59:59.999Z", 57599, "")]                      // 1 ms less: not long
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T07:01:00Z", 60, "")]                             // exactly 60 s: not short
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T07:00:59.999Z", 59, "short_shift")]              // 1 ms less: short
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T07:01:00.001Z", 60, "")]                         // 1 ms more: not short
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T07:00:00.001Z", 0, "short_shift")]               // 1 ms: duration 0 s, still a valid, flagged shift
    // 16 h on the wall clock across DST start is only 15 h in UTC: not long. 16 h across DST end is 17 h: long.
    [InlineData("2026-03-28T20:00:00Z", "2026-03-29T11:00:00Z", 54000, "")]
    [InlineData("2026-10-24T21:00:00Z", "2026-10-25T13:00:00Z", 57600, "")]                          // wall 23:00 -> 14:00 is 15 h, UTC exactly 16 h: not long
    [InlineData("2026-10-24T21:00:00Z", "2026-10-25T13:00:00.001Z", 57600, "long_shift")]
    public async Task Flags_and_duration_at_the_exact_thresholds_come_from_UTC_instants(string inUtc, string outUtc, long seconds, string flag)
    {
        var clock = new FakeTrustedClock(inUtc);
        await using var factory = new PunchApiFactory(database, clock);
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        using var cts = new CancellationTokenSource(Limit);

        await SendAsync(client, HttpMethod.Post, "/api/punches", ClockIn, user, cts.Token);
        clock.Set(outUtc);
        var @out = await SendAsync(client, HttpMethod.Post, "/api/punches", ClockOut, user, cts.Token);
        var status = await SendAsync(client, HttpMethod.Get, "/api/me/status", null, user, cts.Token);

        Assert.Equal(HttpStatusCode.Created, @out.Status);
        var expected = flag.Length == 0 ? Array.Empty<string>() : [flag];
        Assert.Equal(seconds, @out.Body.GetProperty("durationSeconds").GetInt64());
        Assert.Equal(expected, @out.Body.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(expected, status.Body.GetProperty("lastClosedShift").GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(0, status.Body.GetProperty("flags").GetArrayLength()); // forgotten_clock_out is for OPEN shifts only
    }

    // ---------- Helpers ----------

    private sealed record Reply(HttpStatusCode Status, JsonElement Body);

    private sealed record Row(DateTimeOffset InUtc, DateTimeOffset OutUtc, short InOffset, short OutOffset, DateTime InZurichDate);

    private async Task<(int Id, string Username)> AddEmployeeAsync()
    {
        var id = await ShiftRows.AddEmployeeAsync(database);
        await using var db = database.CreateContext();
        return (id, db.Employees.Where(e => e.Id == id).Select(e => e.Username).Single());
    }

    private static async Task<Reply> SendAsync(HttpClient client, HttpMethod method, string url, string? body, string user, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Test-User", user);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString());
        }
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return new Reply(response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<Row> ReadRowAsync(int employeeId)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ClockIn_Utc, ClockOut_Utc, ClockIn_ZurichOffsetMinutes, ClockOut_ZurichOffsetMinutes, ClockIn_ZurichDate FROM Shifts WHERE EmployeeId = @e";
        command.Parameters.AddWithValue("@e", employeeId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new Row(reader.GetDateTimeOffset(0), reader.GetDateTimeOffset(1), reader.GetInt16(2), reader.GetInt16(3), reader.GetDateTime(4));
    }
}
