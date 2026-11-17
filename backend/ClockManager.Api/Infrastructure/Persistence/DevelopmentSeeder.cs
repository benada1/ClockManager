using ClockManager.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClockManager.Api.Infrastructure.Persistence;

/// <summary>
/// Demo employees for local development: one manager and three employees. Idempotent by username, so it runs on
/// every Development start without duplicating anyone. No shifts are seeded: punches need a trusted time, and a
/// shift invented here would be a record with a made-up time. No passwords yet (Phase 4).
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

    public static async Task SeedAsync(ClockManagerDbContext db, CancellationToken cancellationToken = default)
    {
        var usernames = Employees.Select(e => e.Username).ToList();
        var existing = await db.Employees
            .Where(e => usernames.Contains(e.Username))
            .Select(e => e.Username)
            .ToListAsync(cancellationToken);

        foreach (var (username, displayName, role) in Employees)
        {
            if (!existing.Contains(username, StringComparer.OrdinalIgnoreCase))
            {
                db.Employees.Add(new Employee(username, displayName, role));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
