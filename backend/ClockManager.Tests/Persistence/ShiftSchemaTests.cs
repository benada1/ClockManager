using ClockManager.Api.Infrastructure.Persistence.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClockManager.Tests.Persistence;

/// <summary>Schema facts that EF's model doesn't track, read from the migrated database's catalog.</summary>
[Trait("Category", "Integration")]
public sealed class ShiftSchemaTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    [SqlServerFact]
    public async Task Employee_work_date_index_exists()
    {
        // Created with raw SQL in InitialSchema, so the model differ (S12) would not notice if it went missing.
        var columns = await QueryStringsAsync("""
            SELECT c.name FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('dbo.Shifts') AND i.name = @p
            ORDER BY ic.key_ordinal
            """, ShiftConfiguration.EmployeeWorkDateIndex);

        Assert.Equal(["EmployeeId", "ClockIn_ZurichDate"], columns);
    }

    [SqlServerFact]
    public async Task Migrations_leave_nothing_pending()
    {
        await using var db = database.CreateContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("_InitialSchema", StringComparison.Ordinal));
    }

    private async Task<List<string>> QueryStringsAsync(string sql, string parameter)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@p", parameter);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }
}
