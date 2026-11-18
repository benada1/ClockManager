using ClockManager.Api.Application.Auth;
using ClockManager.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClockManager.Api.Infrastructure.Persistence;

/// <summary>
/// Demo employees for local development: one manager and three employees. Idempotent by username, so it runs on
/// every Development start without duplicating anyone. No shifts are seeded: punches need a trusted time, and a
/// shift invented here would be a record with a made-up time. No passwords, except the manager's if Seed:ManagerPassword is set.
/// </summary>
public static class DevelopmentSeeder
{
    public static readonly IReadOnlyList<(string Username, string DisplayName, EmployeeRole Role)> Employees =
    [
        ("manager", "Maria Manager", EmployeeRole.Manager),
        ("alice", "Alice Example", EmployeeRole.Employee),
        ("bob", "Bob Example", EmployeeRole.Employee),
        ("carol", "Carol Example", EmployeeRole.Employee),
    ];

    public const string ManagerUsername = "manager";
    public const string ManagerPasswordKey = "Seed:ManagerPassword";

    /// <summary>
    /// <paramref name="managerPassword"/> (from user-secrets or env, never from git) gives the seeded manager a login, only
    /// while that manager has no hash yet. Other seeded users never get a password.
    /// </summary>
    public static async Task SeedAsync(
        ClockManagerDbContext db,
        CancellationToken cancellationToken = default,
        PasswordService? passwords = null,
        string? managerPassword = null)
    {
        var usernames = Employees.Select(e => e.Username).ToList();
        var existing = await db.Employees
            .Where(e => usernames.Contains(e.Username))
            .ToListAsync(cancellationToken);

        foreach (var (username, displayName, role) in Employees)
        {
            if (!existing.Any(e => string.Equals(e.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                var created = new Employee(username, displayName, role);
                db.Employees.Add(created);
                existing.Add(created);
            }
        }

        if (passwords is not null && !string.IsNullOrWhiteSpace(managerPassword))
        {
            var manager = existing.Single(e => string.Equals(e.Username, ManagerUsername, StringComparison.OrdinalIgnoreCase));
            if (manager.PasswordHash is null) manager.SetPasswordHash(passwords.Hash(manager, managerPassword));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
