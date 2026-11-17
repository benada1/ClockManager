using System.Net;
using System.Text;
using ClockManager.Api.Common.Errors;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using Microsoft.Extensions.Logging;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Punches;

/// <summary>
/// QA pass, slice A group 3: expected business refusals (400/401/403/409/422/503) must not look like server failures in
/// the logs. An Error-level entry means an alert for something that is working as designed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PunchQaLogTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const string ClockIn = """{"intent":"clock-in"}""";
    private const string ClockOut = """{"intent":"clock-out"}""";

    [SqlServerTheory]
    [InlineData("409-already-open")]
    [InlineData("409-not-open")]
    [InlineData("422-key-reused")]
    [InlineData("503-unavailable")]
    [InlineData("503-went-backwards")]
    [InlineData("403-inactive")]
    public async Task A_handled_business_refusal_does_not_log_at_Error(string scenario)
    {
        var (status, factory, logs) = await RunAsync(scenario);
        await using var _ = factory;

        Assert.NotEqual(HttpStatusCode.InternalServerError, status);
        var errors = logs().Where(l => l.Level >= LogLevel.Error).Select(l => $"{l.Category}: {l.Message}").ToList();
        Assert.True(errors.Count == 0, "Error-level logs for a handled refusal: " + string.Join(" | ", errors));
    }

    /// <summary>What the host DOES log for a refusal today, for the report: each scenario's highest level and category.</summary>
    [SqlServerTheory]
    [InlineData("409-already-open", HttpStatusCode.Conflict)]
    [InlineData("409-not-open", HttpStatusCode.Conflict)]
    [InlineData("422-key-reused", HttpStatusCode.UnprocessableEntity)]
    [InlineData("503-unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("503-went-backwards", HttpStatusCode.ServiceUnavailable)]
    [InlineData("403-inactive", HttpStatusCode.Forbidden)]
    public async Task A_handled_refusal_has_the_expected_status_and_the_application_logs_it_below_Error(string scenario, HttpStatusCode expected)
    {
        var (status, factory, logs) = await RunAsync(scenario);
        await using var _ = factory;

        Assert.Equal(expected, status);
        // The application's own handler never goes above Warning for these (Information for 4xx, Warning for 5xx).
        Assert.DoesNotContain(logs(), l => l.Category == typeof(AppExceptionHandler).FullName && l.Level >= LogLevel.Error);
    }

    [SqlServerFact]
    public async Task A_real_500_does_log_at_Error_so_the_alert_still_works()
    {
        // Sanity check of the checker itself: a genuine server fault IS an Error (record insert fails via a trigger).
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        var factory = new PunchApiFactory(database, clock);
        await using var _ = factory;
        using var client = factory.CreateClient();
        var (_, user) = await AddEmployeeAsync();
        await ExecuteAsync("CREATE TRIGGER TR_IdempotencyRecords_QaLog_Fail ON IdempotencyRecords AFTER INSERT AS THROW 50001, 'qa log test', 1;");
        try
        {
            var status = await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.InternalServerError, status);
            Assert.Contains(factory.Logs, l => l.Level >= LogLevel.Error);
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER TR_IdempotencyRecords_QaLog_Fail;");
        }
    }

    // ---------- Helpers ----------

    private async Task<(HttpStatusCode Status, PunchApiFactory Factory, Func<List<PunchApiFactory.LogEntry>> Logs)> RunAsync(string scenario)
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        var factory = new PunchApiFactory(database, clock, environment: "Development");
        using var client = factory.CreateClient();
        var (employeeId, user) = await AddEmployeeAsync();
        var key = Guid.NewGuid().ToString();
        HttpStatusCode status;

        switch (scenario)
        {
            case "409-already-open":
                await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());
                factory.Logs.Clear();
                status = await SendAsync(client, ClockIn, user, key);
                break;
            case "409-not-open":
                status = await SendAsync(client, ClockOut, user, key);
                break;
            case "422-key-reused":
                await SendAsync(client, ClockIn, user, key);
                factory.Logs.Clear();
                status = await SendAsync(client, ClockOut, user, key);
                break;
            case "503-unavailable":
                clock.FailWith(FakeTrustedClock.Unavailable());
                status = await SendAsync(client, ClockIn, user, key);
                break;
            case "503-went-backwards":
                await SendAsync(client, ClockIn, user, Guid.NewGuid().ToString());
                factory.Logs.Clear();
                status = await SendAsync(client, ClockOut, user, key); // same instant as the clock-in
                break;
            case "403-inactive":
                await ExecuteAsync($"UPDATE Employees SET IsActive = 0 WHERE Id = {employeeId}");
                status = await SendAsync(client, ClockIn, user, key);
                break;
            default:
                throw new ArgumentException(scenario);
        }

        return (status, factory, () => factory.Logs.ToList());
    }

    private async Task<(int Id, string Username)> AddEmployeeAsync()
    {
        var id = await ShiftRows.AddEmployeeAsync(database);
        await using var db = database.CreateContext();
        return (id, db.Employees.Where(e => e.Id == id).Select(e => e.Username).Single());
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client, string body, string user, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/punches")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Dev-Employee", user);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        await response.Content.ReadAsStringAsync();
        return response.StatusCode;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
