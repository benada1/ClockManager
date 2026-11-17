using ClockManager.Api.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static ClockManager.Tests.Persistence.PersistenceTestData;
using static ClockManager.Tests.Persistence.ShiftRows;

namespace ClockManager.Tests.Persistence;

/// <summary>Constraints on <c>Employees</c> (EmployeeConfiguration): unique username, role CHECK, IsActive default.</summary>
[Trait("Category", "Integration")]
public sealed class EmployeeSchemaTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    [SqlServerFact]
    public async Task Username_that_differs_only_in_case_violates_the_unique_index()
    {
        // Employee.Username documents "case-insensitive under the database's default collation". Phase 4 logins rely on it.
        var name = "case" + Guid.NewGuid().ToString("N")[..8];
        await using (var db = database.CreateContext())
        {
            db.Employees.Add(new Employee(name.ToLowerInvariant(), "Lower", EmployeeRole.Employee));
            await db.SaveChangesAsync();
        }

        await using var second = database.CreateContext();
        second.Employees.Add(new Employee(name.ToUpperInvariant(), "Upper", EmployeeRole.Employee));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());

        var sql = FindSqlException(ex);
        Assert.Contains(sql.Number, new[] { UniqueIndexViolation, UniqueConstraintViolation });
        Assert.Contains("UX_Employees_Username", sql.Message);
    }

    [SqlServerTheory]
    [InlineData("Admin")]
    [InlineData("")]
    [InlineData("1")]
    public async Task Unknown_role_violates_the_role_check(string role)
    {
        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertEmployeeAsync(role));
        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains("CK_Employees_Role", sql.Message);
    }

    [SqlServerTheory]
    [InlineData("manager")]
    [InlineData("MANAGER")]
    [InlineData("Manager ")]
    public async Task Role_written_in_another_case_or_with_trailing_space_is_still_readable_as_a_known_role(string role)
    {
        // Under a case-insensitive collation the CHECK lets 'manager' / 'Manager ' through (SQL ignores trailing spaces in
        // comparisons). Whatever the DB accepts, EF must be able to read, or one hand-edited row breaks every employee list.
        var id = await InsertEmployeeAsync(role);

        await using var db = database.CreateContext();
        var employee = await db.Employees.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.Equal(EmployeeRole.Manager, employee.Role);
    }

    [SqlServerFact]
    public async Task Deactivated_employee_saved_through_EF_stays_deactivated()
    {
        // EmployeeConfiguration: ValueGeneratedNever, so false (the CLR default) is sent and DEFAULT 1 can't override it.
        await using (var db = database.CreateContext())
        {
            var employee = NewEmployee();
            db.Employees.Add(employee);
            db.Entry(employee).Property(e => e.IsActive).CurrentValue = false;
            await db.SaveChangesAsync();

            await using var read = database.CreateContext();
            Assert.False((await read.Employees.AsNoTracking().SingleAsync(e => e.Id == employee.Id)).IsActive);
        }
    }

    [SqlServerFact]
    public async Task Employee_inserted_by_hand_without_IsActive_defaults_to_active()
    {
        var id = await InsertEmployeeAsync("Employee", includeIsActive: false);
        await using var db = database.CreateContext();
        Assert.True((await db.Employees.AsNoTracking().SingleAsync(e => e.Id == id)).IsActive);
    }

    private async Task<int> InsertEmployeeAsync(string role, bool includeIsActive = true)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = includeIsActive
            ? "INSERT INTO Employees (Username, DisplayName, Role, IsActive) OUTPUT INSERTED.Id VALUES (@u, N'Raw', @r, 1)"
            : "INSERT INTO Employees (Username, DisplayName, Role) OUTPUT INSERTED.Id VALUES (@u, N'Raw', @r)";
        command.Parameters.AddWithValue("@u", "raw" + Guid.NewGuid().ToString("N")[..12]);
        command.Parameters.AddWithValue("@r", role);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
