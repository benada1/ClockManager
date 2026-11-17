using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Persistence;
using ClockManager.Api.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using static ClockManager.Tests.Persistence.PersistenceTestData;
using static ClockManager.Tests.Persistence.ShiftRows;

namespace ClockManager.Tests.Persistence;

/// <summary>
/// S2 and S10 (plans/02-schema.md §6): races between punches on separate DbContexts and connections, as two tabs, a
/// double click or two server instances would produce. Every task opens its connection BEFORE the gate, so the race
/// is between the INSERT/UPDATE statements and not between connection handshakes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ShiftConcurrencyTests(MigratedDatabaseFixture database) : IClassFixture<MigratedDatabaseFixture>
{
    private const int Racers = 10;

    // ---- S2: parallel Clock Ins ----

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task S2_ten_parallel_clock_ins_for_one_employee_store_exactly_one_open_shift(int round)
    {
        _ = round; // three independent rounds: a race that passes once by luck is not proof
        var employeeId = await AddEmployeeAsync(database);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(Racers);

        var racers = Enumerable.Range(0, Racers).Select(i => Task.Run(async () =>
        {
            await using var db = database.CreateContext();
            await db.Database.OpenConnectionAsync();
            db.Shifts.Add(new Shift(employeeId, Stamp($"2026-01-12T07:00:00.{i:D3}Z")));
            ready.Signal();
            await gate.Task;
            try
            {
                await db.SaveChangesAsync();
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        })).ToArray();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "racers did not get ready");
        gate.SetResult();
        var outcomes = await Task.WhenAll(racers);

        Assert.Single(outcomes, o => o is null);
        var failures = outcomes.Where(o => o is not null).ToList();
        Assert.Equal(Racers - 1, failures.Count);
        foreach (var failure in failures)
        {
            Assert.IsType<DbUpdateException>(failure);
            var sql = FindSqlException(failure!);
            Assert.Contains(sql.Number, new[] { UniqueIndexViolation, UniqueConstraintViolation });
            Assert.Contains(ShiftConfiguration.OneOpenPerEmployeeIndex, sql.Message);
        }

        Assert.Equal(1, await CountShiftsAsync(database.ConnectionString, employeeId));
    }

    // ---- Clock In racing Clock Out of the previous shift ----

    [SqlServerFact]
    public async Task Clock_out_racing_a_new_clock_in_never_leaves_two_open_shifts()
    {
        // Tab A closes the open shift while tab B (which already thinks it is closed) clocks in. Either order is fine:
        // A first → both succeed; B first → B hits the unique index. Never two open shifts, and A never loses.
        for (var round = 0; round < 15; round++)
        {
            var employeeId = await AddEmployeeAsync(database);
            long openId;
            await using (var setup = database.CreateContext())
            {
                var open = new Shift(employeeId, Stamp("2026-01-12T07:00:00Z"));
                setup.Shifts.Add(open);
                await setup.SaveChangesAsync();
                openId = open.Id;
            }

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new CountdownEvent(2);

            var clockOut = Task.Run(async () =>
            {
                await using var db = database.CreateContext();
                await db.Database.OpenConnectionAsync();
                var shift = await db.Shifts.SingleAsync(s => s.Id == openId);
                shift.Close(Stamp("2026-01-12T15:30:00Z"));
                ready.Signal();
                await gate.Task;
                await db.SaveChangesAsync();
            });
            var clockIn = Task.Run(async () =>
            {
                await using var db = database.CreateContext();
                await db.Database.OpenConnectionAsync();
                db.Shifts.Add(new Shift(employeeId, Stamp("2026-01-12T15:30:01Z")));
                ready.Signal();
                await gate.Task;
                try
                {
                    await db.SaveChangesAsync();
                    return (Exception?)null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            });

            Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "racers did not get ready");
            gate.SetResult();
            await clockOut; // must not throw in either order
            var clockInError = await clockIn;

            Assert.True(await CountShiftsAsync(database.ConnectionString, employeeId, openOnly: true) <= 1);
            if (clockInError is null)
            {
                Assert.Equal(2, await CountShiftsAsync(database.ConnectionString, employeeId));
            }
            else
            {
                var sql = FindSqlException(clockInError);
                Assert.Contains(sql.Number, new[] { UniqueIndexViolation, UniqueConstraintViolation });
                Assert.Equal(1, await CountShiftsAsync(database.ConnectionString, employeeId));
            }
        }
    }

    // ---- Two Clock Outs on the same open shift (double click, two tabs) ----

    [SqlServerTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task S10_two_parallel_clock_outs_on_one_open_shift_one_wins_and_one_gets_a_concurrency_error(int round)
    {
        // The UPDATE's WHERE includes RowVersion even though only owned ClockOut_* columns change (EF's table-sharing
        // concurrency token). Without it the second clock-out would silently overwrite the first.
        _ = round;
        var (shiftId, _) = await AddOpenShiftAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(2);

        Task<Exception?> CloseAsync(string clockOutUtc) => Task.Run(async () =>
        {
            await using var db = database.CreateContext();
            await db.Database.OpenConnectionAsync();
            var shift = await db.Shifts.SingleAsync(s => s.Id == shiftId);
            shift.Close(Stamp(clockOutUtc));
            ready.Signal();
            await gate.Task;
            try
            {
                await db.SaveChangesAsync();
                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        var a = CloseAsync("2026-01-12T15:30:00Z");
        var b = CloseAsync("2026-01-12T15:30:02Z");
        Assert.True(ready.Wait(TimeSpan.FromSeconds(30)), "racers did not get ready");
        gate.SetResult();
        var outcomes = await Task.WhenAll(a, b);

        Assert.Single(outcomes, o => o is null);
        Assert.Single(outcomes, o => o is DbUpdateConcurrencyException);

        // The stored clock-out is the winner's, not a mix and not the loser's.
        var winner = outcomes[0] is null ? "2026-01-12T15:30:00Z" : "2026-01-12T15:30:02Z";
        await using var check = database.CreateContext();
        var stored = await check.Shifts.AsNoTracking().SingleAsync(s => s.Id == shiftId);
        Assert.Equal(Utc(winner), stored.ClockOut!.Utc);
    }

    // ---- S10: RowVersion ----

    [SqlServerFact]
    public async Task S10_closing_a_shift_changes_its_RowVersion_in_the_database()
    {
        var (shiftId, before) = await AddOpenShiftAsync();

        await using (var db = database.CreateContext())
        {
            var shift = await db.Shifts.SingleAsync(s => s.Id == shiftId);
            shift.Close(Stamp("2026-01-12T15:30:00Z"));
            await db.SaveChangesAsync();
        }

        Assert.NotEqual(before, await ReadRowVersionAsync(shiftId));
    }

    [SqlServerFact]
    public async Task S10_after_closing_the_tracked_shift_carries_the_new_RowVersion()
    {
        // EF should read the new rowversion back into the entity (store-generated). If it doesn't, any later save of the
        // same tracked Shift in this unit of work (e.g. a correction) fails with a spurious concurrency error, and an API
        // that returns RowVersion as an ETag hands the client a stale one.
        var (shiftId, _) = await AddOpenShiftAsync();

        await using var db = database.CreateContext();
        var shift = await db.Shifts.SingleAsync(s => s.Id == shiftId);
        shift.Close(Stamp("2026-01-12T15:30:00Z"));
        await db.SaveChangesAsync();

        Assert.Equal(await ReadRowVersionAsync(shiftId), shift.RowVersion);
    }

    [SqlServerFact]
    public async Task S10_stale_clock_out_throws_DbUpdateConcurrencyException_and_the_first_clock_out_stays()
    {
        var (shiftId, _) = await AddOpenShiftAsync();

        await using var tabA = database.CreateContext();
        await using var tabB = database.CreateContext();
        var inA = await tabA.Shifts.SingleAsync(s => s.Id == shiftId);
        var inB = await tabB.Shifts.SingleAsync(s => s.Id == shiftId); // B loaded it before A saved: B is stale

        inA.Close(Stamp("2026-01-12T15:30:00Z"));
        await tabA.SaveChangesAsync();

        inB.Close(Stamp("2026-01-12T16:45:00Z"));
        var saveB = await Record.ExceptionAsync(() => tabB.SaveChangesAsync());

        await using var check = database.CreateContext();
        var stored = await check.Shifts.AsNoTracking().SingleAsync(s => s.Id == shiftId);
        Assert.Equal(Utc("2026-01-12T15:30:00Z"), stored.ClockOut!.Utc); // the first clock-out is not overwritten
        Assert.IsType<DbUpdateConcurrencyException>(saveB);
    }

    [SqlServerFact]
    public async Task S10_stale_update_of_an_owner_column_throws_DbUpdateConcurrencyException()
    {
        // The same check when a column of Shift itself (not of the owned punch) changes.
        // Shift has no public setter for that today, so EmployeeId is changed through the change tracker.
        var (shiftId, _) = await AddOpenShiftAsync();
        var otherEmployee = await AddEmployeeAsync(database);

        await using var tabA = database.CreateContext();
        await using var tabB = database.CreateContext();
        var inA = await tabA.Shifts.SingleAsync(s => s.Id == shiftId);
        var inB = await tabB.Shifts.SingleAsync(s => s.Id == shiftId);

        inA.Close(Stamp("2026-01-12T15:30:00Z"));
        await tabA.SaveChangesAsync();

        tabB.Entry(inB).Property(s => s.EmployeeId).CurrentValue = otherEmployee;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => tabB.SaveChangesAsync());
    }

    [SqlServerFact]
    public async Task S10_stale_employee_update_throws_DbUpdateConcurrencyException()
    {
        var employeeId = await AddEmployeeAsync(database);
        await using var tabA = database.CreateContext();
        await using var tabB = database.CreateContext();
        var inA = await tabA.Employees.SingleAsync(e => e.Id == employeeId);
        var inB = await tabB.Employees.SingleAsync(e => e.Id == employeeId);
        var before = inA.RowVersion.ToArray();

        tabA.Entry(inA).Property(e => e.DisplayName).CurrentValue = "Renamed by A";
        await tabA.SaveChangesAsync();
        Assert.NotEqual(before, inA.RowVersion);

        tabB.Entry(inB).Property(e => e.DisplayName).CurrentValue = "Renamed by B";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => tabB.SaveChangesAsync());

        await using var check = database.CreateContext();
        Assert.Equal("Renamed by A", (await check.Employees.SingleAsync(e => e.Id == employeeId)).DisplayName);
    }

    private async Task<(long ShiftId, byte[] RowVersion)> AddOpenShiftAsync()
    {
        var employeeId = await AddEmployeeAsync(database);
        await using var db = database.CreateContext();
        var shift = new Shift(employeeId, Stamp("2026-01-12T07:00:00Z"));
        db.Shifts.Add(shift);
        await db.SaveChangesAsync();
        return (shift.Id, shift.RowVersion.ToArray());
    }

    private async Task<byte[]> ReadRowVersionAsync(long shiftId)
    {
        await using var db = database.CreateContext();
        return await db.Shifts.AsNoTracking().Where(s => s.Id == shiftId).Select(s => s.RowVersion).SingleAsync();
    }
}
