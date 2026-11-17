using NodaTime;

namespace ClockManager.Api.Domain;

/// <summary>
/// The stored answer of a SUCCESSFUL punch, keyed by (employee, <c>Idempotency-Key</c>) (plan 03 §5, D-032). A retry with the
/// same key gets <see cref="ResponseBody"/> back instead of punching again. Failed punches (409, 503) are never stored,
/// so a retry after an outage really retries.
/// </summary>
public sealed class IdempotencyRecord
{
    public IdempotencyRecord(int employeeId, Guid key, string intent, long shiftId, int responseStatus, string responseBody, Instant createdUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intent);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseBody);
        EmployeeId = employeeId;
        Key = key;
        Intent = intent;
        ShiftId = shiftId;
        ResponseStatus = responseStatus;
        ResponseBody = responseBody;
        CreatedUtc = createdUtc;
    }

    /// <summary>For EF Core materialization only.</summary>
    private IdempotencyRecord()
    {
    }

    public long Id { get; private set; }

    public int EmployeeId { get; private set; }

    /// <summary>The client's <c>Idempotency-Key</c>. Unique per employee, so two employees can't collide.</summary>
    public Guid Key { get; private set; }

    /// <summary><c>clock-in</c> or <c>clock-out</c> (the wire values), so the same key with the other intent is detectable.</summary>
    public string Intent { get; private set; } = null!;

    public long ShiftId { get; private set; }

    public int ResponseStatus { get; private set; }

    /// <summary>The JSON the API returned, as it was at the time (flags and durations included).</summary>
    public string ResponseBody { get; private set; } = null!;

    /// <summary>The punch's TRUSTED instant (never a local clock), stored at +00:00.</summary>
    public Instant CreatedUtc { get; private set; }
}
