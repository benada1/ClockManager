using System.Net;
using ClockManager.Api.Application.Manager;
using ClockManager.Api.Domain;
using ClockManager.Tests.Persistence;
using static ClockManager.Tests.Manager.ManagerTestKit;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Manager;

/// <summary>The 100-row cap of <c>GET /api/manager/anomalies</c>, on its own database so other tests' rows can't interfere.</summary>
[Trait("Category", "Integration")]
public sealed class ManagerAnomalyLimitTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    [SqlServerFact]
    public async Task At_most_100_anomalies_newest_first()
    {
        var kit = new ManagerTestKit(database);
        var (factory, _) = kit.NewHost("2026-12-01T12:00:00Z");
        await using var _f = factory;
        using var client = factory.CreateClient();
        var (_, manager) = await kit.AddEmployeeAsync(EmployeeRole.Manager);
        var (empId, _) = await kit.AddEmployeeAsync();
        await using (var db = database.CreateContext())
        {
            // 105 short shifts (30 s), one per day from 2026-01-01, plus a normal one that is not an anomaly.
            var day = Utc("2026-01-01T07:00:00Z");
            for (var i = 0; i < 105; i++)
            {
                var start = day + NodaTime.Duration.FromDays(i);
                var shift = new Shift(empId, PunchStamp.From(Trusted(start), PersistenceTestData.Zurich));
                shift.Close(PunchStamp.From(Trusted(start + NodaTime.Duration.FromSeconds(30)), PersistenceTestData.Zurich));
                db.Shifts.Add(shift);
            }
            var normal = new Shift(empId, Stamp("2026-11-30T07:00:00Z"));
            normal.Close(Stamp("2026-11-30T15:00:00Z"));
            db.Shifts.Add(normal);
            await db.SaveChangesAsync();
        }

        var (status, list) = await SendAsync(client, HttpMethod.Get, "/api/manager/anomalies", manager);

        Assert.Equal(HttpStatusCode.OK, status);
        var rows = list.EnumerateArray().ToArray();
        Assert.Equal(ManagerService.MaxAnomalies, rows.Length);
        Assert.Equal("2026-04-15", rows[0].GetProperty("workDate").GetString()); // day 105, the newest
        Assert.Equal("2026-01-06", rows[^1].GetProperty("workDate").GetString()); // the 5 oldest are cut
        Assert.All(rows, r => Assert.Equal("short_shift", r.GetProperty("kind").GetString()));
    }
}
