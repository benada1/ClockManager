using System.Net;
using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Persistence;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Persistence;
using ClockManager.Tests.Punches;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ClockManager.Tests.Auth.AuthQa;

namespace ClockManager.Tests.Auth;

/// <summary>
/// QA wave 1, area A, group 8: the seeded manager through the real host start-up (the existing seeder tests call the seeder
/// directly). Seeding happens only in Development, a missing or blank <c>Seed:ManagerPassword</c> leaves everyone without a
/// login, the password is set once, and a stranger cannot take over a seeded name by signing up.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthQaSeedTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private static readonly string[] SeededNames = [.. DevelopmentSeeder.Employees.Select(e => e.Username)];

    private WebApplicationFactory<Program> Host(string environment, string? managerPassword, bool seedOnStartup = true) =>
        new PunchApiFactory(database, new FakeTrustedClock(StartInstant), environment).WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?> { ["Database:MigrateAndSeedOnStartup"] = seedOnStartup ? "true" : "false" };
                if (managerPassword is not null) settings["Seed:ManagerPassword"] = managerPassword;
                config.AddInMemoryCollection(settings);
            }));

    private async Task<List<Employee>> SeededAsync()
    {
        await using var db = database.CreateContext();
        return await db.Employees.AsNoTracking().Where(e => SeededNames.Contains(e.Username)).OrderBy(e => e.Username).ToListAsync();
    }

    private static async Task<HttpStatusCode> LoginAsync(WebApplicationFactory<Program> host, string username, string password)
    {
        using var client = host.CreateClient();
        return (await PostJsonAsync(client, "/api/auth/login", new { username, password })).Status;
    }

    [SqlServerFact]
    public async Task The_start_up_seed_runs_only_in_Development_and_the_manager_password_follows_the_documented_rules()
    {
        // 1. Production with seeding switched on and a password set: nothing is created, nothing can log in.
        await using (var production = Host("Production", "prod-seed-password-1"))
        {
            using var client = production.CreateClient(); // starts the host
            Assert.Empty(await SeededAsync());
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(production, "manager", "prod-seed-password-1"));
        }
        await using (var staging = Host("Staging", "staging-seed-password-1"))
        {
            using var client = staging.CreateClient();
            Assert.Empty(await SeededAsync());
        }

        // 2. Development with the seed on but no password (missing, then blank): four employees, nobody has a hash.
        foreach (var password in new string?[] { null, "", "   " })
        {
            await using var development = Host("Development", password);
            using var client = development.CreateClient();
            var seeded = await SeededAsync();
            Assert.Equal(["alice", "bob", "carol", "manager"], seeded.Select(e => e.Username));
            Assert.All(seeded, e => Assert.Null(e.PasswordHash));
            Assert.Equal(EmployeeRole.Manager, Assert.Single(seeded, e => e.Username == "manager").Role);
            foreach (var name in SeededNames)
            {
                Assert.True(HttpStatusCode.Unauthorized == await LoginAsync(development, name, "anything at all"), $"{name} logged in without a hash");
                Assert.True(HttpStatusCode.Unauthorized == await LoginAsync(development, name.ToUpperInvariant(), "x"), name);
            }
        }

        // 3. Development with a password: only the manager gets a login, as a Manager.
        await using (var first = Host("Development", "first-seed-password-1"))
        {
            using var client = first.CreateClient();
            var seeded = await SeededAsync();
            Assert.NotNull(Assert.Single(seeded, e => e.Username == "manager").PasswordHash);
            Assert.All(seeded.Where(e => e.Username != "manager"), e => Assert.Null(e.PasswordHash));
            var login = await PostJsonAsync(client, "/api/auth/login", new { username = "manager", password = "first-seed-password-1" });
            Assert.Equal(HttpStatusCode.OK, login.Status);
            Assert.Equal("Manager", login.Json.GetProperty("role").GetString());
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/manager/employees")).Status);
            foreach (var name in new[] { "alice", "bob", "carol" })
            {
                Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(first, name, "first-seed-password-1")); // the manager's password opens nothing else
            }
        }

        // 4. A second start with another password: the existing hash stays (the first password still works, the new one does not).
        await using (var second = Host("Development", "second-seed-password-2"))
        {
            using var client = second.CreateClient();
            Assert.Equal(HttpStatusCode.OK, await LoginAsync(second, "manager", "first-seed-password-1"));
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(second, "manager", "second-seed-password-2"));
            Assert.Equal(4, (await SeededAsync()).Count); // still one of each
        }

        // 5. Development with the seed switched off does nothing at all (the default of the test host).
        await using (var off = Host("Development", "third-seed-password-3", seedOnStartup: false))
        {
            using var client = off.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(off, "manager", "third-seed-password-3"));
        }
    }

    [SqlServerFact]
    public async Task A_stranger_cannot_take_over_a_seeded_name_by_signing_up_in_any_case()
    {
        await using var development = Host("Development", null);
        using var client = development.CreateClient();

        foreach (var name in new[] { "manager", "MANAGER", "Alice", "bob", "CAROL" })
        {
            var reply = await PostJsonAsync(client, "/api/auth/signup", new { username = name, displayName = "Intruder", password = Password });
            Assert.True(HttpStatusCode.Conflict == reply.Status, $"{name}: {reply.Status}");
            Assert.Equal("auth.username_taken", reply.Code);
            Assert.Empty(reply.SetCookies);
        }
        Assert.All(await SeededAsync(), e => Assert.NotEqual("Intruder", e.DisplayName));
    }
}
