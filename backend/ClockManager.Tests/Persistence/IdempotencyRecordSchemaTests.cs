using ClockManager.Api.Infrastructure.Persistence.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static ClockManager.Tests.Persistence.ShiftRows;

namespace ClockManager.Tests.Persistence;

/// <summary>
/// plans/03-punch-api.md §5 and D-032: the constraints of <c>IdempotencyRecords</c>, tested with raw SQL so the C# code
/// can't refuse a bad row first. Each refusal test starts from a valid row and changes one thing, and asserts the
/// constraint NAME, so a different constraint failing by accident can't make the test pass.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IdempotencyRecordSchemaTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const int NullInNotNullColumn = 515;

    private static Dictionary<string, object?> ValidRecord(int employeeId, long shiftId) => new()
    {
        ["EmployeeId"] = employeeId,
        ["Key"] = Guid.NewGuid(),
        ["Intent"] = "clock-in",
        ["ShiftId"] = shiftId,
        ["ResponseStatus"] = 201,
        ["ResponseBody"] = """{"shiftId":1}""",
        ["CreatedUtc"] = new DateTimeOffset(2026, 1, 12, 7, 0, 0, TimeSpan.Zero),
    };

    [SqlServerFact]
    public async Task Baseline_a_valid_record_is_accepted_and_reads_back_through_EF()
    {
        var (employeeId, shiftId) = await AddShiftAsync();
        var row = ValidRecord(employeeId, shiftId);

        await InsertRecordAsync(row);

        await using var db = database.CreateContext();
        var stored = await db.IdempotencyRecords.AsNoTracking().SingleAsync(r => r.EmployeeId == employeeId);
        Assert.Equal((Guid)row["Key"]!, stored.Key);
        Assert.Equal("clock-in", stored.Intent);
        Assert.Equal(shiftId, stored.ShiftId);
        Assert.Equal(201, stored.ResponseStatus);
        Assert.Equal(PersistenceTestData.Utc("2026-01-12T07:00:00Z"), stored.CreatedUtc);
    }

    // ---- The unique (EmployeeId, Key) index: the idempotency guarantee itself ----

    [SqlServerFact]
    public async Task The_same_key_twice_for_one_employee_violates_the_unique_index()
    {
        var (employeeId, shiftId) = await AddShiftAsync();
        var first = ValidRecord(employeeId, shiftId);
        await InsertRecordAsync(first);
        var second = ValidRecord(employeeId, shiftId);
        second["Key"] = first["Key"];
        second["Intent"] = "clock-out"; // the intent doesn't matter: the key is what is unique

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertRecordAsync(second));

        Assert.Equal(UniqueIndexViolation, sql.Number);
        Assert.Contains(IdempotencyRecordConfiguration.EmployeeKeyIndex, sql.Message);
        Assert.Equal(1, await CountRecordsAsync(employeeId));
    }

    [SqlServerFact]
    public async Task The_same_key_for_two_different_employees_is_allowed()
    {
        var (alice, aliceShift) = await AddShiftAsync();
        var (bob, bobShift) = await AddShiftAsync();
        var key = Guid.NewGuid();
        var forAlice = ValidRecord(alice, aliceShift);
        forAlice["Key"] = key;
        var forBob = ValidRecord(bob, bobShift);
        forBob["Key"] = key;

        await InsertRecordAsync(forAlice);
        await InsertRecordAsync(forBob);

        Assert.Equal(1, await CountRecordsAsync(alice));
        Assert.Equal(1, await CountRecordsAsync(bob));
    }

    // ---- Foreign keys: NO ACTION, shifts and employees are records ----

    [SqlServerFact]
    public async Task Deleting_a_shift_that_has_an_idempotency_record_is_refused_no_cascade()
    {
        var (employeeId, shiftId) = await AddShiftAsync();
        await InsertRecordAsync(ValidRecord(employeeId, shiftId));

        await using var db = database.CreateContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Shifts WHERE Id = {shiftId}"));

        var sql = PersistenceTestData.FindSqlException(ex);
        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains("FK_IdempotencyRecords_Shifts_ShiftId", sql.Message);
        Assert.Equal(1, await CountRecordsAsync(employeeId));
        Assert.Equal(1, await CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task Deleting_an_employee_who_has_idempotency_records_is_refused_and_nothing_cascades()
    {
        var (employeeId, shiftId) = await AddShiftAsync();
        await InsertRecordAsync(ValidRecord(employeeId, shiftId));

        await using var db = database.CreateContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Employees WHERE Id = {employeeId}"));

        // The shift's own FK may be the one SQL Server reports first; either way the delete is refused.
        var sql = PersistenceTestData.FindSqlException(ex);
        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Equal(1, await CountRecordsAsync(employeeId));
        Assert.Equal(1, await CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task A_record_for_an_unknown_shift_or_employee_violates_its_foreign_key()
    {
        var (employeeId, shiftId) = await AddShiftAsync();

        var unknownShift = ValidRecord(employeeId, long.MaxValue);
        var shiftEx = await Assert.ThrowsAsync<SqlException>(() => InsertRecordAsync(unknownShift));
        Assert.Equal(ConstraintViolation, shiftEx.Number);
        Assert.Contains("FK_IdempotencyRecords_Shifts_ShiftId", shiftEx.Message);

        var unknownEmployee = ValidRecord(int.MaxValue, shiftId);
        var employeeEx = await Assert.ThrowsAsync<SqlException>(() => InsertRecordAsync(unknownEmployee));
        Assert.Equal(ConstraintViolation, employeeEx.Number);
        Assert.Contains("FK_IdempotencyRecords_Employees_EmployeeId", employeeEx.Message);
    }

    // ---- CHECK constraints ----

    [SqlServerTheory]
    [InlineData("clock-sideways")]
    [InlineData("")]
    [InlineData("clock")]
    public async Task Intent_other_than_clock_in_or_clock_out_violates_the_intent_check(string intent)
    {
        var (employeeId, shiftId) = await AddShiftAsync();
        var row = ValidRecord(employeeId, shiftId);
        row["Intent"] = intent;

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertRecordAsync(row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains(IdempotencyRecordConfiguration.IntentCheck, sql.Message);
    }

    [SqlServerFact]
    public async Task Both_intents_are_accepted()
    {
        var (employeeId, shiftId) = await AddShiftAsync();
        var inRow = ValidRecord(employeeId, shiftId);
        var outRow = ValidRecord(employeeId, shiftId);
        outRow["Intent"] = "clock-out";

        await InsertRecordAsync(inRow);
        await InsertRecordAsync(outRow);

        Assert.Equal(2, await CountRecordsAsync(employeeId));
    }

    [SqlServerTheory]
    [InlineData(100)]
    [InlineData(199)]
    [InlineData(300)]
    [InlineData(409)]
    [InlineData(503)]
    public async Task Only_successful_responses_can_be_stored(int status)
    {
        var (employeeId, shiftId) = await AddShiftAsync();
        var row = ValidRecord(employeeId, shiftId);
        row["ResponseStatus"] = status;

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertRecordAsync(row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains(IdempotencyRecordConfiguration.ResponseStatusCheck, sql.Message);
    }

    [SqlServerTheory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"shiftId\":")]
    public async Task A_response_body_that_is_not_JSON_violates_the_json_check(string body)
    {
        var (employeeId, shiftId) = await AddShiftAsync();
        var row = ValidRecord(employeeId, shiftId);
        row["ResponseBody"] = body;

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertRecordAsync(row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains(IdempotencyRecordConfiguration.ResponseBodyJsonCheck, sql.Message);
    }

    [SqlServerFact]
    public async Task CreatedUtc_stored_with_a_non_zero_offset_violates_the_utc_offset_check()
    {
        var (employeeId, shiftId) = await AddShiftAsync();
        var row = ValidRecord(employeeId, shiftId);
        row["CreatedUtc"] = new DateTimeOffset(2026, 1, 12, 8, 0, 0, TimeSpan.FromHours(1)); // the same instant, in Zurich time

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertRecordAsync(row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains(IdempotencyRecordConfiguration.CreatedUtcOffsetZeroCheck, sql.Message);
    }

    [SqlServerFact]
    public async Task The_empty_guid_is_not_a_valid_key()
    {
        var (employeeId, shiftId) = await AddShiftAsync();
        var row = ValidRecord(employeeId, shiftId);
        row["Key"] = Guid.Empty;

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertRecordAsync(row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains(IdempotencyRecordConfiguration.KeyNotEmptyCheck, sql.Message);
    }

    [SqlServerTheory]
    [InlineData("EmployeeId")]
    [InlineData("Key")]
    [InlineData("Intent")]
    [InlineData("ShiftId")]
    [InlineData("ResponseStatus")]
    [InlineData("ResponseBody")]
    [InlineData("CreatedUtc")]
    public async Task Every_column_is_NOT_NULL(string column)
    {
        // The CHECKs rely on it: a NULL would be UNKNOWN in them and pass (QA-12).
        var (employeeId, shiftId) = await AddShiftAsync();
        var row = ValidRecord(employeeId, shiftId);
        row[column] = null;

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertRecordAsync(row));

        Assert.Equal(NullInNotNullColumn, sql.Number);
    }

    // ---------- Helpers ----------

    private async Task<(int EmployeeId, long ShiftId)> AddShiftAsync()
    {
        var employeeId = await AddEmployeeAsync(database);
        var shiftId = await InsertAsync(database.ConnectionString, ValidClosed(employeeId));
        return (employeeId, shiftId);
    }

    private async Task InsertRecordAsync(IReadOnlyDictionary<string, object?> row)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var columns = string.Join(", ", row.Keys.Select(k => $"[{k}]"));
        var values = string.Join(", ", row.Keys.Select((_, i) => "@p" + i));
        var index = 0;
        foreach (var value in row.Values) command.Parameters.AddWithValue("@p" + index++, value ?? DBNull.Value);
        command.CommandText = $"INSERT INTO IdempotencyRecords ({columns}) VALUES ({values})";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountRecordsAsync(int employeeId)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM IdempotencyRecords WHERE EmployeeId = @e";
        command.Parameters.AddWithValue("@e", employeeId);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
