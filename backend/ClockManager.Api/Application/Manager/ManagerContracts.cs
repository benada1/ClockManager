using ClockManager.Api.Application.History;

namespace ClockManager.Api.Application.Manager;

/// <summary>One row of <c>GET /api/manager/employees</c> (plans/07-manager.md §5).</summary>
/// <param name="Status"><see cref="ManagerStatus"/> values.</param>
/// <param name="Since">Clock-in of the open shift, null when clocked out.</param>
/// <param name="Flags"><c>forgotten_clock_out</c> (open shift) and <c>long_shift</c> (a closed shift today); only with trusted time.</param>
public sealed record ManagerEmployeeView(
    int Id, string Username, string DisplayName, string Role, bool IsActive, string Status, HistoryPunchView? Since, IReadOnlyList<string> Flags);

public static class ManagerStatus
{
    public const string ClockedIn = "clocked_in";
    public const string ClockedOut = "clocked_out";
}

/// <summary>One row of <c>GET /api/manager/anomalies</c>. <c>Kind</c> is a <see cref="Shifts.ShiftFlags"/> name.</summary>
public sealed record AnomalyView(
    long ShiftId, int EmployeeId, string DisplayName, string WorkDate, string Kind,
    HistoryPunchView ClockIn, HistoryPunchView? ClockOut, long? DurationSeconds);

public sealed record EmployeeRefView(int Id, string DisplayName);

/// <summary>One audit row. <c>Old</c> is null when an open shift was closed. Zurich offsets from tzdb at each instant.</summary>
public sealed record CorrectionView(
    long Id, string Field, HistoryPunchView? Old, HistoryPunchView New, string Reason, EmployeeRefView By, HistoryPunchView At);

/// <summary><c>GET /api/manager/shifts/{id}</c> and the 201 of a correction. The shift has the history shape plus rowVersion.</summary>
public sealed record ShiftDetailView(HistoryShiftView Shift, EmployeeRefView Employee, IReadOnlyList<CorrectionView> Corrections);

/// <summary>Body of <c>POST /api/manager/shifts/{id}/corrections</c>. All text: parsed by us with fixed messages.</summary>
public sealed class CorrectionRequest
{
    /// <summary><c>"clockIn"</c> or <c>"clockOut"</c>.</summary>
    public string? Field { get; init; }

    /// <summary>ISO 8601 with the Zurich offset in force at that instant, e.g. <c>2026-11-17T17:12:00+01:00</c>.</summary>
    public string? NewTime { get; init; }

    /// <summary>5 to 500 characters after trimming.</summary>
    public string? Reason { get; init; }

    /// <summary>The shift's <c>rowVersion</c> as last read (base64).</summary>
    public string? RowVersion { get; init; }
}

public static class CorrectionFields
{
    public const string ClockIn = "clockIn";
    public const string ClockOut = "clockOut";
}

/// <summary><c>GET /api/manager/employees/{id}/shifts</c>: the <c>/api/me/shifts</c> body plus whose shifts they are.</summary>
public sealed record ManagerShiftHistoryView(
    string? From, string? To, IReadOnlyList<HistoryDayView> Days, long TotalSeconds, EmployeeRefView Employee);
