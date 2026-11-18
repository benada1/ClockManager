namespace ClockManager.Api.Application.History;

/// <summary>A punch in the history: the Zurich time with its offset (<c>+HH:mm</c>).</summary>
public sealed record HistoryPunchView(string Zurich);

/// <summary>One shift in a history day (plans/06-history.md §3).</summary>
/// <param name="EndsNextDay">The clock-out's Zurich date is later than <paramref name="WorkDate"/>.</param>
/// <param name="DurationSeconds">From the UTC instants; null while open.</param>
/// <param name="Corrected">A manager corrected a punch of this shift (plan 07).</param>
/// <param name="RowVersion">Base64 row version, manager views only (omitted for employees).</param>
public sealed record HistoryShiftView(
    long ShiftId,
    string WorkDate,
    HistoryPunchView ClockIn,
    HistoryPunchView? ClockOut,
    long? DurationSeconds,
    bool EndsNextDay,
    bool Open,
    IReadOnlyList<string> Flags,
    bool Corrected,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? RowVersion = null);

/// <summary>A Zurich day that has shifts. <c>TotalSeconds</c> counts closed shifts only.</summary>
public sealed record HistoryDayView(string Date, long TotalSeconds, IReadOnlyList<string> Notes, IReadOnlyList<HistoryShiftView> Shifts);

/// <summary>Response of <c>GET /api/me/shifts</c>. <c>From</c>/<c>To</c> are null when the employee has no shifts at all.</summary>
public sealed record ShiftHistoryView(string? From, string? To, IReadOnlyList<HistoryDayView> Days, long TotalSeconds);

/// <summary>Day notes. Stable: clients rely on them.</summary>
public static class HistoryNotes
{
    /// <summary>The Zurich calendar day is 23 or 25 hours long.</summary>
    public const string DstChange = "dst_change";
}
