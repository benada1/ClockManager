using ClockManager.Api.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Persistence;

/// <summary>
/// S9 (plans/02-schema.md §6): what goes into SQL Server comes back exactly, including the repeated October hour (T6),
/// the DST-start instant (T7) and sub-millisecond precision (time.now sends µs, timeapi.io 100 ns).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PunchRoundTripTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    [SqlServerFact]
    public async Task S9_T6_shift_inside_the_repeated_hour_round_trips_exactly()
    {
        // Local 02:30 (+02:00) → 02:15 (+01:00): the wall clock goes backwards, the instants don't. 45 minutes.
        var clockIn = Stamp("2026-10-25T00:30:00Z");
        var clockOut = Stamp("2026-10-25T01:15:00Z", crossCheckedBy: null);

        var loaded = await SaveAndReloadAsync(clockIn, clockOut);

        AssertSameStamp(clockIn, loaded.ClockIn);
        Assert.NotNull(loaded.ClockOut);
        AssertSameStamp(clockOut, loaded.ClockOut);
        Assert.Equal(Offset.FromHours(2), loaded.ClockIn.ZurichOffset);
        Assert.Equal(Offset.FromHours(1), loaded.ClockOut.ZurichOffset);
        Assert.Equal(new LocalDate(2026, 10, 25), loaded.ClockIn.ZurichDate);
        Assert.Equal(new LocalDate(2026, 10, 25), loaded.ClockOut.ZurichDate);
        Assert.Equal(Duration.FromMinutes(45), loaded.ClockOut.Utc - loaded.ClockIn.Utc);
        Assert.Null(loaded.ClockOut.CrossCheckedBy);
        Assert.Null(loaded.ClockOut.CrossCheckDeltaMs);
    }

    [SqlServerFact]
    public async Task S9_T7_punch_exactly_at_DST_start_round_trips_as_03_00_plus_02()
    {
        var clockIn = Stamp("2026-03-29T01:00:00Z");

        var loaded = await SaveAndReloadAsync(clockIn, clockOut: null);

        AssertSameStamp(clockIn, loaded.ClockIn);
        Assert.Equal(Offset.FromHours(2), loaded.ClockIn.ZurichOffset);
        Assert.Equal(new LocalDate(2026, 3, 29), loaded.ClockIn.ZurichDate);
        Assert.True(loaded.IsOpen); // all-NULL ClockOut columns are read back as "no clock-out"
    }

    [SqlServerTheory]
    [InlineData("2026-01-12T07:00:00.123456Z")]  // microseconds (time.now)
    [InlineData("2026-01-12T07:00:00.1234567Z")] // 100 ns, the SQL datetimeoffset(7) limit (timeapi.io)
    public async Task S9_sub_millisecond_instants_round_trip_exactly(string isoUtc)
    {
        var clockIn = Stamp(isoUtc);
        var clockOut = Stamp("2026-01-12T15:30:00.0000001Z");

        var loaded = await SaveAndReloadAsync(clockIn, clockOut);

        Assert.Equal(Utc(isoUtc), loaded.ClockIn.Utc);
        Assert.Equal(Utc("2026-01-12T15:30:00.0000001Z"), loaded.ClockOut!.Utc);
    }

    private async Task<Shift> SaveAndReloadAsync(PunchStamp clockIn, PunchStamp? clockOut)
    {
        long id;
        await using (var db = database.CreateContext())
        {
            var employee = NewEmployee();
            db.Employees.Add(employee);
            await db.SaveChangesAsync();

            var shift = new Shift(employee.Id, clockIn);
            if (clockOut is not null) shift.Close(clockOut);
            db.Shifts.Add(shift);
            await db.SaveChangesAsync();
            id = shift.Id;
        }

        // A fresh context: values come from SQL Server, not from EF's change tracker.
        await using var read = database.CreateContext();
        return await read.Shifts.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private static void AssertSameStamp(PunchStamp expected, PunchStamp actual)
    {
        Assert.Equal(expected.Utc, actual.Utc);
        Assert.Equal(expected.ZurichOffset, actual.ZurichOffset);
        Assert.Equal(expected.ZurichDate, actual.ZurichDate);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.RoundTripMs, actual.RoundTripMs);
        Assert.Equal(expected.CrossCheckedBy, actual.CrossCheckedBy);
        Assert.Equal(expected.CrossCheckDeltaMs, actual.CrossCheckDeltaMs);
        Assert.Equal(expected.LowPrecision, actual.LowPrecision);
        Assert.Equal(expected.RawPayload, actual.RawPayload);
    }
}
