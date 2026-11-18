using Microsoft.EntityFrameworkCore;

namespace ClockManager.Api.Infrastructure.Persistence;

/// <summary>
/// D-044: the update lock on the employee's row that serialises every writer of that employee's shifts (punches and
/// manager corrections). Take it as the FIRST statement of the transaction, always Employees row before Shifts.
/// </summary>
public static class EmployeeLock
{
    public static Task AcquireAsync(ClockManagerDbContext db, int employeeId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT [Id] FROM [Employees] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {employeeId}", ct);
}
