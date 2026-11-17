namespace ClockManager.Api.Domain;

/// <summary>
/// A work shift: opened by a Clock In, closed by a Clock Out. <see cref="ClockOut"/> null means the shift is open.
/// The database also enforces the core rules (one open shift per employee, out after in, a complete clock-out),
/// so a bug or a race here can't store an invalid shift. Phase 3 adds the punch use cases on top.
/// </summary>
public sealed class Shift
{
    public Shift(int employeeId, PunchStamp clockIn)
    {
        ArgumentNullException.ThrowIfNull(clockIn);
        EmployeeId = employeeId;
        ClockIn = clockIn;
    }

    /// <summary>For EF Core materialization only.</summary>
    private Shift()
    {
    }

    public long Id { get; private set; }

    public int EmployeeId { get; private set; }

    public PunchStamp ClockIn { get; private set; } = null!;

    public PunchStamp? ClockOut { get; private set; }

    /// <summary>SQL Server <c>rowversion</c>: a stale Clock Out vs a manager correction fails instead of overwriting.</summary>
    public byte[] RowVersion { get; private set; } = null!;

    public bool IsOpen => ClockOut is null;

    /// <summary>Closes the shift. Both checks are repeated by the database (<c>CK_Shifts_ClockOutAfterClockIn</c>).</summary>
    public void Close(PunchStamp clockOut)
    {
        ArgumentNullException.ThrowIfNull(clockOut);
        if (!IsOpen) throw new InvalidOperationException("The shift is already closed.");
        if (clockOut.Utc <= ClockIn.Utc)
            throw new InvalidOperationException($"Clock Out ({clockOut.Utc}) must be later than Clock In ({ClockIn.Utc}).");
        ClockOut = clockOut;
    }
}
