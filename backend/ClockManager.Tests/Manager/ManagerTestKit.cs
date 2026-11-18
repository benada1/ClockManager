using System.Net;
using System.Text;
using System.Text.Json;
using ClockManager.Api.Application.Auth;
using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Identity;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using ClockManager.Tests.Punches;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Manager;

/// <summary>Seeding and HTTP helpers for the manager tests. Fixed instants, the fake trusted clock, no local clock.</summary>
internal sealed class ManagerTestKit(MigratedDatabaseFixture database)
{
    public (PunchApiFactory Factory, FakeTrustedClock Clock) NewHost(string nowUtc)
    {
        var clock = new FakeTrustedClock(nowUtc);
        return (new PunchApiFactory(database, clock), clock);
    }

    public async Task<(int Id, string Username)> AddEmployeeAsync(EmployeeRole role = EmployeeRole.Employee, string displayName = "Test Employee")
    {
        await using var db = database.CreateContext();
        var employee = new Employee((role == EmployeeRole.Manager ? "m" : "e") + Guid.NewGuid().ToString("N")[..12], displayName, role);
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return (employee.Id, employee.Username);
    }

    public async Task SetRoleAsync(int employeeId, EmployeeRole role)
    {
        await using var db = database.CreateContext();
        var text = role.ToString();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Employees] SET [Role] = {text} WHERE [Id] = {employeeId}");
    }

    public async Task DeactivateAsync(int employeeId)
    {
        await using var db = database.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [Employees] SET [IsActive] = 0 WHERE [Id] = {employeeId}");
    }

    public async Task<long> SaveClosedAsync(int employeeId, string inUtc, string outUtc)
    {
        await using var db = database.CreateContext();
        var shift = new Shift(employeeId, Stamp(inUtc));
        shift.Close(Stamp(outUtc));
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        return shift.Id;
    }

    public async Task<long> SaveOpenAsync(int employeeId, string inUtc)
    {
        await using var db = database.CreateContext();
        var shift = new Shift(employeeId, Stamp(inUtc));
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        return shift.Id;
    }

    public async Task<Shift> LoadShiftAsync(long shiftId)
    {
        await using var db = database.CreateContext();
        return await db.Shifts.AsNoTracking().SingleAsync(s => s.Id == shiftId);
    }

    public async Task<string> RowVersionAsync(long shiftId) => Convert.ToBase64String((await LoadShiftAsync(shiftId)).RowVersion);

    public async Task<List<ShiftCorrection>> CorrectionsAsync(long shiftId)
    {
        await using var db = database.CreateContext();
        return await db.ShiftCorrections.AsNoTracking().Where(c => c.ShiftId == shiftId).OrderBy(c => c.Id).ToListAsync();
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpClient client, HttpMethod method, string url, string? user, object? body = null, string? cookie = null,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (user is not null) request.Headers.TryAddWithoutValidation(PunchApiFactory.TestUserHeader, user);
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (idempotencyKey is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (body is not null)
        {
            var json = body as string ?? JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static Task<(HttpStatusCode Status, JsonElement Body)> CorrectAsync(
        HttpClient client, string? user, long shiftId, string field, string newTime, string rowVersion, string reason = "Forgot to clock out") =>
        SendAsync(client, HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections", user,
            new { field, newTime, reason, rowVersion });

    /// <summary>A real login cookie claiming <paramref name="role"/>, independent of the role now in the database.</summary>
    public static string CookieFor(PunchApiFactory factory, int employeeId, EmployeeRole role)
    {
        var token = factory.Services.GetRequiredService<ITokenIssuer>().Issue(employeeId, role);
        return $"{factory.Services.GetRequiredService<IOptions<AuthOptions>>().Value.CookieName}={token.Value}";
    }

    public static string Code(JsonElement problem) => problem.GetProperty("code").GetString()!;

    public static string Zurich(JsonElement punch) => punch.GetProperty("zurich").GetString()!;
}
