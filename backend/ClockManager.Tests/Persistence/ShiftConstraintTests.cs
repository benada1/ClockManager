using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Persistence;
using ClockManager.Api.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Persistence;

/// <summary>
/// The database itself refuses invalid shifts (CLAUDE.md rule 2), plans/02-schema.md §6. Bad rows are written
/// with raw SQL where the C# domain would already refuse them: the point is that SQL Server refuses them too.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ShiftConstraintTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int ConstraintViolation = 547;

    [SqlServerFact]
    public async Task S1_second_open_shift_for_the_same_employee_violates_the_filtered_unique_index()
    {
        var employeeId = await AddEmployeeAsync();
        await using (var first = database.CreateContext())
        {
            first.Shifts.Add(new Shift(employeeId, Stamp("2026-01-12T07:00:00Z")));
            await first.SaveChangesAsync();
        }

        // A second request (other tab, other server) that didn't see the first shift.
        await using var second = database.CreateContext();
        second.Shifts.Add(new Shift(employeeId, Stamp("2026-01-12T07:00:01Z")));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());

        var sql = FindSqlException(ex);
        Assert.Contains(sql.Number, new[] { UniqueIndexViolation, UniqueConstraintViolation });
        Assert.Contains(ShiftConfiguration.OneOpenPerEmployeeIndex, sql.Message);

        await using var check = database.CreateContext();
        Assert.Equal(1, await check.Shifts.CountAsync(s => s.EmployeeId == employeeId));
    }

    [SqlServerTheory]
    [InlineData("2026-01-12T07:00:00Z", "T11 zero-length")]
    [InlineData("2026-01-12T06:59:58Z", "T10 two seconds before clock-in")]
    public async Task S5_clock_out_not_after_clock_in_violates_the_check(string clockOutUtc, string scenario)
    {
        _ = scenario;
        var employeeId = await AddEmployeeAsync();
        long shiftId;
        await using (var db = database.CreateContext())
        {
            var shift = new Shift(employeeId, Stamp("2026-01-12T07:00:00Z"));
            db.Shifts.Add(shift);
            await db.SaveChangesAsync();
            shiftId = shift.Id;
        }

        // Every other ClockOut column is valid (complete, +01:00, matching date), so only the ordering check can fail.
        var clockOut = Utc(clockOutUtc).ToDateTimeOffset();
        var date = new DateOnly(2026, 1, 12);
        const string payload = "{}";
        await using var raw = database.CreateContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => raw.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE Shifts SET
                ClockOut_Utc = {clockOut}, ClockOut_ZurichOffsetMinutes = 60, ClockOut_ZurichDate = {date},
                ClockOut_Provider = 'time.now', ClockOut_RoundTripMs = 80, ClockOut_LowPrecision = 0,
                ClockOut_RawPayload = {payload}
            WHERE Id = {shiftId}
            """));

        var sql = FindSqlException(ex);
        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains(ShiftConfiguration.ClockOutAfterClockInCheck, sql.Message);

        await using var check = database.CreateContext();
        Assert.True((await check.Shifts.SingleAsync(s => s.Id == shiftId)).IsOpen);
    }

    private async Task<int> AddEmployeeAsync()
    {
        await using var db = database.CreateContext();
        var employee = NewEmployee();
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }
}
