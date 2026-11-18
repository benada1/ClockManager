using System.Net;
using System.Text;
using ClockManager.Api.Domain;
using ClockManager.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using static ClockManager.Tests.Manager.ManagerTestKit;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Manager;

/// <summary>
/// QA wave 1, area A, group 4: who may call the manager endpoints and the employee ones, and whether the answers leak that an
/// id exists. Roles are read from the database on every request (D-070), so an old token never carries more than the row does.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ManagerQaAccessTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private readonly ManagerTestKit _kit = new(database);
    private const string Now = "2026-01-13T07:00:00Z";

    private static string Shape(HttpStatusCode status, System.Text.Json.JsonElement problem) =>
        string.Join("|", status, problem.GetProperty("title"), problem.GetProperty("detail"), problem.GetProperty("code"), problem.GetProperty("status"));

    [SqlServerFact]
    public async Task A_manager_who_was_deactivated_is_403_on_every_manager_endpoint_and_nothing_changes()
    {
        var (factory, _) = _kit.NewHost(Now);
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (managerId, _) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (employeeId, _) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveClosedAsync(employeeId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");
        var rowVersion = await _kit.RowVersionAsync(shiftId);
        var cookie = CookieFor(factory, managerId, EmployeeRole.Manager); // issued while active
        var correction = new { field = "clockOut", newTime = "2026-01-12T16:00:00+01:00", reason = "Left at four", rowVersion };
        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, "/api/manager/employees", null),
            (HttpMethod.Get, "/api/manager/anomalies", null),
            (HttpMethod.Get, $"/api/manager/employees/{employeeId}/shifts", null),
            (HttpMethod.Get, $"/api/manager/shifts/{shiftId}", null),
            (HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections", correction),
        };
        foreach (var (method, url, body) in calls.Take(4))
        {
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, method, url, null, body, cookie)).Status); // control while active
        }

        await _kit.DeactivateAsync(managerId);

        foreach (var (method, url, body) in calls)
        {
            var (status, problem) = await SendAsync(client, method, url, null, body, cookie);
            Assert.True(HttpStatusCode.Forbidden == status, $"{method} {url}: {status}");
            Assert.Equal("auth.forbidden", Code(problem));
        }
        Assert.Empty(await _kit.CorrectionsAsync(shiftId));
        Assert.Equal(rowVersion, await _kit.RowVersionAsync(shiftId));
        var (meStatus, me) = await SendAsync(client, HttpMethod.Get, "/api/auth/me", null, cookie: cookie);
        Assert.Equal(HttpStatusCode.Forbidden, meStatus);
        Assert.Equal("employee.inactive", Code(me));
    }

    [SqlServerFact]
    public async Task Employees_and_anonymous_callers_cannot_tell_which_employee_or_shift_ids_exist()
    {
        var (factory, _) = _kit.NewHost(Now);
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, caller) = await _kit.AddEmployeeAsync();
        var (otherId, _) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveClosedAsync(otherId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");
        var rowVersion = await _kit.RowVersionAsync(shiftId);
        var valid = new { field = "clockOut", newTime = "2026-01-12T16:00:00+01:00", reason = "Left at four", rowVersion };
        const long Missing = 2_000_000_000;
        var calls = new (string Label, HttpMethod Method, string Url, object? Body)[]
        {
            ("employee that exists", HttpMethod.Get, $"/api/manager/employees/{otherId}/shifts", null),
            ("employee that does not", HttpMethod.Get, $"/api/manager/employees/{Missing}/shifts", null),
            ("shift that exists", HttpMethod.Get, $"/api/manager/shifts/{shiftId}", null),
            ("shift that does not", HttpMethod.Get, $"/api/manager/shifts/{Missing}", null),
            ("correction, shift exists", HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections", valid),
            ("correction, no such shift", HttpMethod.Post, $"/api/manager/shifts/{Missing}/corrections", valid),
            ("correction, broken JSON", HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections", "{not json"),
            ("correction, empty body", HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections", null),
            ("bad dates", HttpMethod.Get, $"/api/manager/employees/{otherId}/shifts?from=garbage", null),
        };

        string? employeeShape = null, anonymousShape = null;
        foreach (var (label, method, url, body) in calls)
        {
            var (forbidden, p1) = await SendAsync(client, method, url, caller, body);
            Assert.True(HttpStatusCode.Forbidden == forbidden, $"employee, {label}: {forbidden}");
            var shape = Shape(forbidden, p1);
            Assert.True((employeeShape ??= shape) == shape, $"employee, {label}: another answer than the first one: {shape}");

            var (unauthorized, p2) = await SendAsync(client, method, url, null, body);
            Assert.True(HttpStatusCode.Unauthorized == unauthorized, $"anonymous, {label}: {unauthorized}");
            var anonymous = Shape(unauthorized, p2);
            Assert.True((anonymousShape ??= anonymous) == anonymous, $"anonymous, {label}: {anonymous}");
        }

        // A wrong content type is refused by the routing step ([Consumes]) before the policy runs: 415 for everyone, whatever the
        // role and whether or not the shift exists, so it tells nothing either.
        foreach (var id in new[] { shiftId, Missing })
        {
            foreach (var user in new[] { caller, null })
            {
                using var plain = new HttpRequestMessage(HttpMethod.Post, $"/api/manager/shifts/{id}/corrections")
                {
                    Content = new StringContent("x", Encoding.UTF8, "text/plain"),
                };
                if (user is not null) plain.Headers.TryAddWithoutValidation(PunchApiFactoryHeader, user);
                using var plainResponse = await client.SendAsync(plain);
                Assert.Equal(HttpStatusCode.UnsupportedMediaType, plainResponse.StatusCode);
            }
        }
        Assert.Empty(await _kit.CorrectionsAsync(shiftId));
    }

    private const string PunchApiFactoryHeader = Punches.PunchApiFactory.TestUserHeader;

    [SqlServerFact]
    public async Task A_manager_gets_a_plain_404_for_unknown_or_malformed_ids_and_no_internals_leak()
    {
        var (factory, _) = _kit.NewHost(Now);
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var valid = new { field = "clockOut", newTime = "2026-01-12T16:00:00+01:00", reason = "Left at four", rowVersion = "AAAAAAAAB9E=" };
        var urls = new (string Label, HttpMethod Method, string Url, object? Body)[]
        {
            ("unknown employee", HttpMethod.Get, "/api/manager/employees/2000000000/shifts", null),
            ("employee id 0", HttpMethod.Get, "/api/manager/employees/0/shifts", null),
            ("negative employee id", HttpMethod.Get, "/api/manager/employees/-1/shifts", null),
            ("employee id over int", HttpMethod.Get, "/api/manager/employees/99999999999/shifts", null),
            ("employee id text", HttpMethod.Get, "/api/manager/employees/abc/shifts", null),
            ("employee id sql", HttpMethod.Get, "/api/manager/employees/1%20OR%201=1/shifts", null),
            ("unknown shift", HttpMethod.Get, "/api/manager/shifts/2000000000", null),
            ("shift id 0", HttpMethod.Get, "/api/manager/shifts/0", null),
            ("negative shift id", HttpMethod.Get, "/api/manager/shifts/-1", null),
            ("shift id over long", HttpMethod.Get, "/api/manager/shifts/9223372036854775808", null),
            ("shift id text", HttpMethod.Get, "/api/manager/shifts/abc", null),
            ("shift id with a semicolon", HttpMethod.Get, "/api/manager/shifts/1;2", null),
            ("correction on an unknown shift", HttpMethod.Post, "/api/manager/shifts/2000000000/corrections", valid),
            ("correction on shift id text", HttpMethod.Post, "/api/manager/shifts/abc/corrections", valid),
        };

        foreach (var (label, method, url, body) in urls)
        {
            var (status, problem) = await SendAsync(client, method, url, manager, body);
            Assert.True(HttpStatusCode.NotFound == status, $"{label}: {status}");
            Assert.Equal("request.not_found", Code(problem));
            AssertNoInternals(problem.GetRawText(), label);
        }
    }

    private static void AssertNoInternals(string text, string where)
    {
        foreach (var marker in new[] { "System.", "ClockManager.", "Exception", "   at ", ".cs:", "Microsoft.", "SqlClient", "SELECT ", "dbo." })
        {
            Assert.False(text.Contains(marker, StringComparison.Ordinal), $"{where}: body leaks '{marker}': {text}");
        }
    }

    [SqlServerFact]
    public async Task An_employee_only_ever_gets_their_own_shifts_status_and_punches_whatever_ids_the_request_carries()
    {
        var (factory, clock) = _kit.NewHost(Now);
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (aId, a) = await _kit.AddEmployeeAsync();
        var (bId, b) = await _kit.AddEmployeeAsync();
        var aShift = await _kit.SaveClosedAsync(aId, "2026-01-12T07:00:00Z", "2026-01-12T15:00:00Z"); // 8 h
        var bShift = await _kit.SaveClosedAsync(bId, "2026-01-12T08:00:00Z", "2026-01-12T11:00:00Z"); // 3 h
        var bOpen = await _kit.SaveOpenAsync(bId, "2026-01-13T06:00:00Z");

        var spoofs = new[]
        {
            "", $"&employeeId={bId}", $"&id={bId}", $"&userId={bId}", $"&employee={bId}", $"&username={b}", $"&EmployeeId={bId}", $"&shiftId={bShift}",
        };
        foreach (var extra in spoofs)
        {
            var (status, body) = await SendAsync(client, HttpMethod.Get, "/api/me/shifts?from=2026-01-12&to=2026-01-12" + extra, a);
            Assert.True(HttpStatusCode.OK == status, $"{extra}: {status}");
            var shifts = body.GetProperty("days").EnumerateArray().SelectMany(d => d.GetProperty("shifts").EnumerateArray()).ToList();
            Assert.True(shifts.Count == 1 && shifts[0].GetProperty("shiftId").GetInt64() == aShift, $"{extra}: {body.GetRawText()}");
            Assert.Equal(8 * 3600, body.GetProperty("totalSeconds").GetInt64());
        }

        var headers = new[] { "X-Employee-Id", "X-User-Id", "X-Dev-Employee", "X-Forwarded-User", "X-Remote-User" };
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/api/me/status?employeeId={bId}"))
        {
            request.Headers.TryAddWithoutValidation(PunchApiFactoryHeader, a);
            foreach (var h in headers) request.Headers.TryAddWithoutValidation(h, bId.ToString());
            using var response = await client.SendAsync(request);
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("openShift").ValueKind); // B's open shift is not A's
        }

        // A punch carrying B's id anywhere is either refused (body) or ignored (query, headers): it is only ever A's punch.
        var (bodyStatus, bodyProblem) = await SendAsync(client, HttpMethod.Post, "/api/punches", a,
            new { intent = "clock-in", employeeId = bId }, idempotencyKey: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.BadRequest, bodyStatus);
        Assert.Equal("request.invalid", Code(bodyProblem));
        var (queryStatus, _) = await SendAsync(client, HttpMethod.Post, $"/api/punches?employeeId={bId}", a,
            new { intent = "clock-in" }, idempotencyKey: Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, queryStatus); // would be 409 if it had been applied to B (B has an open shift)
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Shifts.CountAsync(s => s.EmployeeId == aId && s.ClockOut == null));
        Assert.Equal(1, await db.Shifts.CountAsync(s => s.EmployeeId == bId && s.ClockOut == null));
        Assert.Equal(bOpen, (await db.Shifts.SingleAsync(s => s.EmployeeId == bId && s.ClockOut == null)).Id);
        _ = clock;
    }

    [SqlServerFact]
    public async Task A_correction_records_who_and_when_from_the_server_never_from_extra_body_fields()
    {
        var (factory, _) = _kit.NewHost(Now);
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (managerId, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (otherManagerId, _) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (employeeId, _) = await _kit.AddEmployeeAsync();
        var shiftId = await _kit.SaveClosedAsync(employeeId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");
        var rowVersion = await _kit.RowVersionAsync(shiftId);

        var (status, _) = await SendAsync(client, HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections", manager, $$"""
            {"field":"clockOut","newTime":"2026-01-12T16:00:00+01:00","reason":"Left at four","rowVersion":"{{rowVersion}}",
             "managerId":{{otherManagerId}},"createdUtc":"2020-01-01T00:00:00Z","createdProvider":"evil","oldUtc":"2020-01-01T00:00:00Z",
             "employeeId":{{managerId}},"shiftId":1,"id":99,"by":{"id":{{otherManagerId}}},"at":"2020-01-01T00:00:00Z"}
            """);

        Assert.Equal(HttpStatusCode.Created, status);
        var row = Assert.Single(await _kit.CorrectionsAsync(shiftId));
        Assert.Equal(managerId, row.ManagerId);
        Assert.Equal(shiftId, row.ShiftId);
        Assert.Equal(Utc(Now), row.CreatedUtc); // the trusted clock, not "2020-01-01"
        Assert.Equal("fake", row.CreatedProvider);
        Assert.Equal(Utc("2026-01-12T15:30:00Z"), row.OldUtc); // what the shift really held
        Assert.NotEqual(99, row.Id);
        Assert.Equal(employeeId, (await _kit.LoadShiftAsync(shiftId)).EmployeeId);
    }

    [SqlServerFact]
    public async Task Auth_me_reports_the_role_from_the_database_not_from_the_token()
    {
        var (factory, _) = _kit.NewHost(Now);
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (demotedId, _) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (promotedId, _) = await _kit.AddEmployeeAsync();
        var managerToken = CookieFor(factory, demotedId, EmployeeRole.Manager);
        var employeeToken = CookieFor(factory, promotedId, EmployeeRole.Employee);
        await _kit.SetRoleAsync(demotedId, EmployeeRole.Employee);
        await _kit.SetRoleAsync(promotedId, EmployeeRole.Manager);

        var (_, demoted) = await SendAsync(client, HttpMethod.Get, "/api/auth/me", null, cookie: managerToken);
        var (_, promoted) = await SendAsync(client, HttpMethod.Get, "/api/auth/me", null, cookie: employeeToken);

        Assert.Equal("Employee", demoted.GetProperty("role").GetString());
        Assert.Equal("Manager", promoted.GetProperty("role").GetString());
    }
}
