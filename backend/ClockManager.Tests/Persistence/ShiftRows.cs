using System.Text;
using Microsoft.Data.SqlClient;

namespace ClockManager.Tests.Persistence;

/// <summary>
/// Raw-SQL writers for the persistence tests. The C# domain (<c>PunchStamp.From</c>, <c>Shift.Close</c>) refuses most bad
/// shapes before they reach SQL Server, so constraint tests write rows by hand: start from a valid row and change one
/// column, so the only thing that can fail is the constraint under test.
/// </summary>
internal static class ShiftRows
{
    public const int UniqueIndexViolation = 2601;
    public const int UniqueConstraintViolation = 2627;
    public const int ConstraintViolation = 547;

    /// <summary>
    /// T1 (winter, +01:00): in 2026-01-12T07:00Z, out 15:30Z. Every column is valid, cross-check pair set on both punches.
    /// </summary>
    public static Dictionary<string, object?> ValidClosed(int employeeId) => new()
    {
        ["EmployeeId"] = employeeId,
        ["ClockIn_Utc"] = new DateTimeOffset(2026, 1, 12, 7, 0, 0, TimeSpan.Zero),
        ["ClockIn_ZurichOffsetMinutes"] = (short)60,
        ["ClockIn_ZurichDate"] = new DateTime(2026, 1, 12),
        ["ClockIn_Provider"] = "time.now",
        ["ClockIn_RoundTripMs"] = 80,
        ["ClockIn_CrossCheckedBy"] = "timeapi.io",
        ["ClockIn_CrossCheckDeltaMs"] = 12,
        ["ClockIn_LowPrecision"] = false,
        ["ClockIn_RawPayload"] = "{}",
        ["ClockOut_Utc"] = new DateTimeOffset(2026, 1, 12, 15, 30, 0, TimeSpan.Zero),
        ["ClockOut_ZurichOffsetMinutes"] = (short)60,
        ["ClockOut_ZurichDate"] = new DateTime(2026, 1, 12),
        ["ClockOut_Provider"] = "time.now",
        ["ClockOut_RoundTripMs"] = 80,
        ["ClockOut_CrossCheckedBy"] = "timeapi.io",
        ["ClockOut_CrossCheckDeltaMs"] = 9,
        ["ClockOut_LowPrecision"] = false,
        ["ClockOut_RawPayload"] = "{}",
    };

    /// <summary>The same row with every <c>ClockOut_*</c> column NULL: a valid open shift.</summary>
    public static Dictionary<string, object?> ValidOpen(int employeeId)
    {
        var row = ValidClosed(employeeId);
        foreach (var key in row.Keys.Where(k => k.StartsWith("ClockOut_", StringComparison.Ordinal)).ToList()) row[key] = null;
        return row;
    }

    /// <summary>INSERTs the row and returns its Id. A refused row surfaces as the raw <see cref="SqlException"/>.</summary>
    public static async Task<long> InsertAsync(string connectionString, IReadOnlyDictionary<string, object?> row)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        var columns = new StringBuilder();
        var values = new StringBuilder();
        var i = 0;
        foreach (var (column, value) in row)
        {
            if (i > 0)
            {
                columns.Append(", ");
                values.Append(", ");
            }

            columns.Append('[').Append(column).Append(']');
            values.Append("@p").Append(i);
            command.Parameters.AddWithValue("@p" + i, value ?? DBNull.Value);
            i++;
        }

        command.CommandText = $"INSERT INTO Shifts ({columns}) OUTPUT INSERTED.Id VALUES ({values})";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<int> CountShiftsAsync(string connectionString, int employeeId, bool openOnly = false)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Shifts WHERE EmployeeId = @e"
            + (openOnly ? " AND ClockOut_Utc IS NULL" : string.Empty);
        command.Parameters.AddWithValue("@e", employeeId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    public static async Task<int> AddEmployeeAsync(MigratedDatabaseFixture database)
    {
        await using var db = database.CreateContext();
        var employee = PersistenceTestData.NewEmployee();
        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }
}
