using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Persistence.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static ClockManager.Tests.Persistence.PersistenceTestData;
using static ClockManager.Tests.Persistence.ShiftRows;

namespace ClockManager.Tests.Persistence;

/// <summary>
/// plans/02-schema.md §6 S3, S4, S6, S7, S8 plus the other CHECKs in <see cref="ShiftConfiguration"/>.
/// Every refused-row test starts from a valid row (<see cref="ShiftRows.ValidClosed"/>) and changes one thing, and
/// asserts the constraint NAME, so a different constraint failing by accident can't make the test pass.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ShiftIntegrityTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    [SqlServerFact]
    public async Task Baseline_valid_closed_and_open_rows_are_accepted()
    {
        // Guards the other tests: if the "valid" row itself were refused, every refusal test would pass for nothing.
        var employeeId = await AddEmployeeAsync(database);
        await InsertAsync(database.ConnectionString, ValidClosed(employeeId));
        await InsertAsync(database.ConnectionString, ValidOpen(employeeId));

        await using var db = database.CreateContext();
        var shifts = await db.Shifts.AsNoTracking().Where(s => s.EmployeeId == employeeId).OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(2, shifts.Count);
        Assert.False(shifts[0].IsOpen);
        Assert.True(shifts[1].IsOpen);
    }

    // ---- S3 / S4: the filtered index only limits OPEN shifts of ONE employee ----

    [SqlServerFact]
    public async Task S3_open_shifts_for_two_different_employees_are_both_allowed()
    {
        var alice = await AddEmployeeAsync(database);
        var bob = await AddEmployeeAsync(database);

        await using (var db = database.CreateContext())
        {
            db.Shifts.Add(new Shift(alice, Stamp("2026-01-12T07:00:00Z")));
            db.Shifts.Add(new Shift(bob, Stamp("2026-01-12T07:00:00Z"))); // same instant, too
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await CountShiftsAsync(database.ConnectionString, alice, openOnly: true));
        Assert.Equal(1, await CountShiftsAsync(database.ConnectionString, bob, openOnly: true));
    }

    [SqlServerFact]
    public async Task S4_closed_shifts_plus_a_new_open_shift_for_the_same_employee_are_allowed()
    {
        var employeeId = await AddEmployeeAsync(database);

        await using (var db = database.CreateContext())
        {
            // Two closed shifts (the index must not cover closed rows at all), then the open one.
            var monday = new Shift(employeeId, Stamp("2026-01-12T07:00:00Z"));
            monday.Close(Stamp("2026-01-12T15:30:00Z"));
            var tuesday = new Shift(employeeId, Stamp("2026-01-13T07:00:00Z"));
            tuesday.Close(Stamp("2026-01-13T15:30:00Z"));
            db.Shifts.AddRange(monday, tuesday);
            await db.SaveChangesAsync();

            db.Shifts.Add(new Shift(employeeId, Stamp("2026-01-14T07:00:00Z")));
            await db.SaveChangesAsync();
        }

        Assert.Equal(3, await CountShiftsAsync(database.ConnectionString, employeeId));
        Assert.Equal(1, await CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));
    }

    [SqlServerFact]
    public async Task S4_closing_the_open_shift_then_clocking_in_again_is_allowed()
    {
        // The everyday cycle through EF: in → out → in. The UPDATE must take the row out of the filtered index.
        var employeeId = await AddEmployeeAsync(database);
        await using var db = database.CreateContext();
        var first = new Shift(employeeId, Stamp("2026-01-12T07:00:00Z"));
        db.Shifts.Add(first);
        await db.SaveChangesAsync();

        first.Close(Stamp("2026-01-12T12:00:00Z"));
        await db.SaveChangesAsync();

        db.Shifts.Add(new Shift(employeeId, Stamp("2026-01-12T13:00:00Z")));
        await db.SaveChangesAsync();

        Assert.Equal(1, await CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true));
    }

    // ---- S6: a clock-out is fully recorded or not at all ----

    [SqlServerTheory]
    [InlineData("ClockOut_ZurichOffsetMinutes")]
    [InlineData("ClockOut_ZurichDate")]
    [InlineData("ClockOut_Provider")]
    [InlineData("ClockOut_RoundTripMs")]
    [InlineData("ClockOut_LowPrecision")]
    [InlineData("ClockOut_RawPayload")]
    public async Task S6_clock_out_time_without_one_of_its_required_columns_violates_the_complete_check(string missingColumn)
    {
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidClosed(employeeId);
        row[missingColumn] = null;

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(database.ConnectionString, row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains(ShiftConfiguration.ClockOutCompleteCheck, sql.Message);
        Assert.Equal(0, await CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerTheory]
    [InlineData("ClockOut_ZurichOffsetMinutes")]
    [InlineData("ClockOut_ZurichDate")]
    [InlineData("ClockOut_Provider")]
    [InlineData("ClockOut_RoundTripMs")]
    [InlineData("ClockOut_CrossCheckedBy")]
    [InlineData("ClockOut_LowPrecision")]
    [InlineData("ClockOut_RawPayload")]
    public async Task S6_clock_out_evidence_without_a_clock_out_time_violates_the_complete_check(string presentColumn)
    {
        // The other half: ClockOut_Utc is NULL ("open") but a piece of a clock-out was written anyway.
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidOpen(employeeId);
        row[presentColumn] = ValidClosed(employeeId)[presentColumn];
        if (presentColumn == "ClockOut_CrossCheckedBy") row["ClockOut_CrossCheckDeltaMs"] = 9; // keep the pair check happy

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(database.ConnectionString, row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains(ShiftConfiguration.ClockOutCompleteCheck, sql.Message);
    }

    [SqlServerFact]
    public async Task S6_clock_out_without_the_optional_cross_check_pair_is_accepted()
    {
        // D-009: a single-source clock-out is complete. Only the required columns are required.
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidClosed(employeeId);
        row["ClockOut_CrossCheckedBy"] = null;
        row["ClockOut_CrossCheckDeltaMs"] = null;

        await InsertAsync(database.ConnectionString, row);
        Assert.Equal(1, await CountShiftsAsync(database.ConnectionString, employeeId));
    }

    // ---- S7: only +01:00 / +02:00 exist in Zurich ----

    [SqlServerTheory]
    [InlineData("ClockIn", 0)]
    [InlineData("ClockIn", 180)]
    [InlineData("ClockIn", -60)]
    [InlineData("ClockIn", 90)]
    [InlineData("ClockOut", 0)]
    [InlineData("ClockOut", 180)]
    [InlineData("ClockOut", -60)]
    [InlineData("ClockOut", 90)]
    public async Task S7_offset_other_than_60_or_120_violates_the_offset_check(string punch, short offsetMinutes)
    {
        // 07:00Z and 15:30Z stay on 2026-01-12 for every offset above, so the ZurichDate check can't be what fails.
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidClosed(employeeId);
        row[$"{punch}_ZurichOffsetMinutes"] = offsetMinutes;

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(database.ConnectionString, row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains($"CK_Shifts_{punch}Offset", sql.Message);
    }

    // ---- S8: shifts are records; deleting their employee must fail ----

    [SqlServerFact]
    public async Task S8_deleting_an_employee_who_has_shifts_violates_the_foreign_key_and_the_shifts_survive()
    {
        var employeeId = await AddEmployeeAsync(database);
        await using (var db = database.CreateContext())
        {
            var closed = new Shift(employeeId, Stamp("2026-01-12T07:00:00Z"));
            closed.Close(Stamp("2026-01-12T15:30:00Z"));
            db.Shifts.AddRange(closed, new Shift(employeeId, Stamp("2026-01-13T07:00:00Z")));
            await db.SaveChangesAsync();
        }

        // A fresh context that never loaded the shifts, as an admin "delete employee" endpoint would.
        await using (var db = database.CreateContext())
        {
            var employee = await db.Employees.SingleAsync(e => e.Id == employeeId);
            db.Employees.Remove(employee);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            var sql = FindSqlException(ex);
            Assert.Equal(ConstraintViolation, sql.Number);
            Assert.Contains("FK_Shifts_Employees_EmployeeId", sql.Message);
        }

        await using var check = database.CreateContext();
        Assert.True(await check.Employees.AnyAsync(e => e.Id == employeeId));
        Assert.Equal(2, await check.Shifts.CountAsync(s => s.EmployeeId == employeeId));
    }

    [SqlServerFact]
    public async Task S8_raw_sql_delete_of_an_employee_with_shifts_is_refused_too()
    {
        // No cascade at the DB level either (ON DELETE NO ACTION), not only in EF's model.
        var employeeId = await AddEmployeeAsync(database);
        await InsertAsync(database.ConnectionString, ValidClosed(employeeId));

        await using var db = database.CreateContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Employees WHERE Id = {employeeId}"));

        var sql = FindSqlException(ex);
        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains("FK_Shifts_Employees_EmployeeId", sql.Message);
        Assert.Equal(1, await CountShiftsAsync(database.ConnectionString, employeeId));
    }

    [SqlServerFact]
    public async Task S8_deleting_an_employee_without_shifts_is_allowed()
    {
        var employeeId = await AddEmployeeAsync(database);
        await using var db = database.CreateContext();
        db.Employees.Remove(await db.Employees.SingleAsync(e => e.Id == employeeId));
        await db.SaveChangesAsync();

        Assert.False(await db.Employees.AnyAsync(e => e.Id == employeeId));
    }

    [SqlServerFact]
    public async Task Shift_for_an_unknown_employee_violates_the_foreign_key()
    {
        await using var db = database.CreateContext();
        db.Shifts.Add(new Shift(int.MaxValue, Stamp("2026-01-12T07:00:00Z")));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var sql = FindSqlException(ex);
        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains("FK_Shifts_Employees_EmployeeId", sql.Message);
    }

    // ---- The other CHECKs in ShiftConfiguration ----

    [SqlServerTheory]
    [InlineData("ClockIn")]
    [InlineData("ClockOut")]
    public async Task Utc_column_stored_with_a_non_zero_offset_violates_the_utc_offset_check(string punch)
    {
        // Same instant, written as Zurich local time (+01:00). Instants are stored at +00:00 only.
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidClosed(employeeId);
        var utc = (DateTimeOffset)row[$"{punch}_Utc"]!;
        row[$"{punch}_Utc"] = utc.ToOffset(TimeSpan.FromHours(1));

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(database.ConnectionString, row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains($"CK_Shifts_{punch}UtcOffsetZero", sql.Message);
    }

    [SqlServerFact]
    public async Task T3_overnight_clock_in_with_the_UTC_date_instead_of_the_Zurich_date_violates_the_date_check()
    {
        // 2026-01-12T23:30Z is 00:30 on 2026-01-13 in Zurich. Storing the UTC date (12th) is the classic bug.
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidOpen(employeeId);
        row["ClockIn_Utc"] = new DateTimeOffset(2026, 1, 12, 23, 30, 0, TimeSpan.Zero);
        row["ClockIn_ZurichDate"] = new DateTime(2026, 1, 12);

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(database.ConnectionString, row));
        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains("CK_Shifts_ClockInZurichDate", sql.Message);

        // And the right date is accepted.
        row["ClockIn_ZurichDate"] = new DateTime(2026, 1, 13);
        await InsertAsync(database.ConnectionString, row);
    }

    [SqlServerFact]
    public async Task T8_year_end_clock_out_with_the_UTC_date_violates_the_clock_out_date_check()
    {
        // In 2026-12-31T22:00Z (23:00 local), out 2027-01-01T06:00Z (07:00 local, 2027-01-01). Out date given as 2026-12-31.
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidClosed(employeeId);
        row["ClockIn_Utc"] = new DateTimeOffset(2026, 12, 31, 22, 0, 0, TimeSpan.Zero);
        row["ClockIn_ZurichDate"] = new DateTime(2026, 12, 31);
        row["ClockOut_Utc"] = new DateTimeOffset(2027, 1, 1, 6, 0, 0, TimeSpan.Zero);
        row["ClockOut_ZurichDate"] = new DateTime(2026, 12, 31);

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(database.ConnectionString, row));
        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains("CK_Shifts_ClockOutZurichDate", sql.Message);
    }

    [SqlServerTheory]
    [InlineData("ClockIn", null, 12)]            // a delta from nobody
    [InlineData("ClockIn", "timeapi.io", -1)]    // a negative delta
    [InlineData("ClockOut", null, 12)]
    [InlineData("ClockOut", "timeapi.io", -1)]
    public async Task Cross_check_pair_that_is_half_set_or_negative_violates_the_cross_check(string punch, string? by, int? deltaMs)
    {
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidClosed(employeeId);
        row[$"{punch}_CrossCheckedBy"] = by;
        row[$"{punch}_CrossCheckDeltaMs"] = deltaMs;

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(database.ConnectionString, row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains($"CK_Shifts_{punch}CrossCheck", sql.Message);
    }

    [SqlServerTheory]
    [InlineData("ClockIn")]
    [InlineData("ClockOut")]
    public async Task QA12_cross_checked_by_a_provider_with_a_NULL_delta_violates_the_cross_check(string punch)
    {
        // "Cross-checked by timeapi.io, delta unknown" is half the evidence D-009 asks for, and PunchStamp.From refuses
        // it. The CHECK's second branch is `[By] IS NOT NULL AND [Delta] >= 0`: with Delta NULL that is UNKNOWN, and a
        // CHECK only rejects FALSE, so the row is stored. Fix idea: `... AND [Delta] IS NOT NULL AND [Delta] >= 0`.
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidClosed(employeeId);
        row[$"{punch}_CrossCheckedBy"] = "timeapi.io";
        row[$"{punch}_CrossCheckDeltaMs"] = null;

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(database.ConnectionString, row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains($"CK_Shifts_{punch}CrossCheck", sql.Message);
    }

    [SqlServerTheory]
    [InlineData("ClockIn")]
    [InlineData("ClockOut")]
    public async Task Negative_round_trip_violates_the_round_trip_check(string punch)
    {
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidClosed(employeeId);
        row[$"{punch}_RoundTripMs"] = -1;

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(database.ConnectionString, row));

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains($"CK_Shifts_{punch}RoundTrip", sql.Message);
    }

    [Theory(Skip = "QA-13 open: no CHECK refuses a blank provider name. Fixed in a following fix(db) commit.")]
    [InlineData("ClockIn", "")]
    [InlineData("ClockIn", "   ")]
    [InlineData("ClockOut", "")]
    [InlineData("ClockOut", "   ")]
    [Trait("Status", "KnownBug")] // QA-13: no CHECK on the provider name; the DB stores a punch with no time source.
    public async Task QA13_blank_provider_name_is_refused_by_the_database(string punch, string provider)
    {
        // PunchStamp.From refuses a blank provider, but CLAUDE.md rule 2 says the DB must refuse it too: a punch whose
        // time source is "" can't be audited against rule 1. NOT NULL alone lets '' and '   ' through.
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidClosed(employeeId);
        row[$"{punch}_Provider"] = provider;

        var sql = await Assert.ThrowsAsync<SqlException>(() => InsertAsync(database.ConnectionString, row));
        Assert.Equal(ConstraintViolation, sql.Number);
    }

    [SqlServerFact]
    public async Task Documented_gap_offset_that_disagrees_with_tzdb_is_accepted_by_the_database()
    {
        // CHARACTERIZATION, not a wish: a January instant stored with +02:00 (and the date that +02:00 gives) passes every
        // CHECK, because the DB knows the allowed offsets but not the DST calendar. ShiftConfiguration says tzdb is checked
        // in C# (PunchStamp.From). If someone adds a DB-side tzdb check, this test should be flipped, not deleted.
        var employeeId = await AddEmployeeAsync(database);
        var row = ValidClosed(employeeId);
        row["ClockIn_ZurichOffsetMinutes"] = (short)120;
        row["ClockOut_ZurichOffsetMinutes"] = (short)120;

        await InsertAsync(database.ConnectionString, row);

        await using var db = database.CreateContext();
        var shift = await db.Shifts.AsNoTracking().SingleAsync(s => s.EmployeeId == employeeId);
        Assert.NotEqual(Zurich.OffsetAt(shift.ClockIn.Utc), shift.ClockIn.ZurichOffset);
    }
}
