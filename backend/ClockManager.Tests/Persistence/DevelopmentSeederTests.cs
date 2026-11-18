using ClockManager.Api.Application.Auth;
using ClockManager.Api.Domain;
using Microsoft.AspNetCore.Identity;
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

    [SqlServerFact]
    public async Task Manager_password_is_set_only_with_the_key_and_only_while_the_manager_has_none()
    {
        var passwords = new PasswordService(new PasswordHasher<Employee>());

        await using (var none = database.CreateContext()) await DevelopmentSeeder.SeedAsync(none, default, passwords, null);
        await using (var blank = database.CreateContext()) await DevelopmentSeeder.SeedAsync(blank, default, passwords, "  ");
        Assert.All(await LoadAsync(), e => Assert.Null(e.PasswordHash));

        await using (var first = database.CreateContext()) await DevelopmentSeeder.SeedAsync(first, default, passwords, "first secret 1");
        var afterFirst = await LoadAsync();
        var manager = Assert.Single(afterFirst, e => e.Username == "manager");
        Assert.NotNull(manager.PasswordHash);
        Assert.DoesNotContain("first secret 1", manager.PasswordHash, StringComparison.Ordinal);
        Assert.True(passwords.Verify(manager, "first secret 1"));
        Assert.All(afterFirst.Where(e => e.Username != "manager"), e => Assert.Null(e.PasswordHash));

        await using (var second = database.CreateContext()) await DevelopmentSeeder.SeedAsync(second, default, passwords, "other secret 2");
        var again = Assert.Single(await LoadAsync(), e => e.Username == "manager");
        Assert.Equal(manager.PasswordHash, again.PasswordHash);
    }

    private async Task<List<Employee>> LoadAsync()
    {
        await using var db = database.CreateContext();
        return await db.Employees.AsNoTracking().Where(e => DevelopmentSeeder.Employees.Select(s => s.Username).Contains(e.Username)).ToListAsync();
    }
}
