using System.Net;
using ClockManager.Api.Domain;
using ClockManager.Tests.Persistence;
using static ClockManager.Tests.Manager.ManagerTestKit;

namespace ClockManager.Tests.Manager;

/// <summary>Phase 8 review: a correction body with a repeated member is ambiguous, like the punch and login bodies (D-041).</summary>
[Trait("Category", "Integration")]
public sealed class ManagerDuplicateMemberTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private readonly ManagerTestKit _kit = new(database);

    [SqlServerFact]
    public async Task A_correction_with_a_repeated_member_is_a_400_and_changes_nothing()
    {
        var (factory, _) = _kit.NewHost("2026-01-13T07:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (managerId, manager) = await _kit.AddEmployeeAsync(EmployeeRole.Manager);
        var shiftId = await _kit.SaveClosedAsync(managerId, "2026-01-12T07:00:00Z", "2026-01-12T15:30:00Z");
        var rv = await _kit.RowVersionAsync(shiftId);
        var body = $$"""{"field":"clockOut","newTime":"2026-01-12T16:00:00+01:00","newTime":"2026-01-12T17:00:00+01:00","reason":"Left at four","rowVersion":"{{rv}}"}""";

        var (status, _) = await SendAsync(client, HttpMethod.Post, $"/api/manager/shifts/{shiftId}/corrections", manager, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(await _kit.CorrectionsAsync(shiftId));
    }
}
