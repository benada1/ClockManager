using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClockManager.Tests.Persistence;

[Trait("Category", "Integration")]
public sealed class DevelopmentSeederTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    [SqlServerFact]
    public async Task S11_seeding_twice_still_gives_one_manager_and_three_employees()
    {
        await using (var first = database.CreateContext()) await DevelopmentSeeder.SeedAsync(first);
        await using (var second = database.CreateContext()) await DevelopmentSeeder.SeedAsync(second);

        await using var db = database.CreateContext();
        var employees = await db.Employees.AsNoTracking().OrderBy(e => e.Username).ToListAsync();

        Assert.Equal(["alice", "bob", "carol", "manager"], employees.Select(e => e.Username));
        Assert.Equal(EmployeeRole.Manager, Assert.Single(employees, e => e.Role == EmployeeRole.Manager).Role);
        Assert.Equal(3, employees.Count(e => e.Role == EmployeeRole.Employee));
        Assert.All(employees, e => Assert.True(e.IsActive));
    }
}
