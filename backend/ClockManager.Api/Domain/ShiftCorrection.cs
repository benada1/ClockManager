using NodaTime;

namespace ClockManager.Api.Domain;

/// <summary>Which punch of a shift a manager corrected. Stored as text, restricted by <c>CK_ShiftCorrections_Field</c>.</summary>
public enum ShiftCorrectionField
{
    ClockIn,
    ClockOut,
}

/// <summary>
/// One manager correction of a shift punch (plans/07-manager.md §4). Insert-only: there is no code path that updates
/// or deletes a row, so the first row of a field keeps the original value.
/// </summary>
public sealed class ShiftCorrection
{
    public const int ReasonMinLength = 5;
    public const int ReasonMaxLength = 500;

    public ShiftCorrection(
        long shiftId, int managerId, ShiftCorrectionField field, Instant? oldUtc, Instant newUtc, string reason,
        Instant createdUtc, string createdProvider)
    {
        if (!Enum.IsDefined(field)) throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown field.");
        if (oldUtc is null && field != ShiftCorrectionField.ClockOut)
            throw new ArgumentException("Only a clock-out can have no previous value.", nameof(oldUtc));
        var trimmed = reason?.Trim() ?? throw new ArgumentNullException(nameof(reason));
        if (trimmed.Length is < ReasonMinLength or > ReasonMaxLength)
            throw new ArgumentException($"Reason must be {ReasonMinLength}..{ReasonMaxLength} characters.", nameof(reason));
        if (string.IsNullOrWhiteSpace(createdProvider) || createdProvider.Length > PunchStamp.ProviderMaxLength)
            throw new ArgumentException($"Provider must be 1..{PunchStamp.ProviderMaxLength} characters.", nameof(createdProvider));

        ShiftId = shiftId;
        ManagerId = managerId;
        Field = field;
        OldUtc = oldUtc;
        NewUtc = newUtc;
        Reason = trimmed;
        CreatedUtc = createdUtc;
        CreatedProvider = createdProvider;
    }

    /// <summary>For EF Core materialization only.</summary>
    private ShiftCorrection()
    {
    }

    public long Id { get; private set; }

    public long ShiftId { get; private set; }

    public int ManagerId { get; private set; }

    public ShiftCorrectionField Field { get; private set; }

    /// <summary>The effective value before this correction; null only when an open shift was closed.</summary>
    public Instant? OldUtc { get; private set; }

    public Instant NewUtc { get; private set; }

    public string Reason { get; private set; } = null!;

    /// <summary>When the correction was made, from the trusted clock (never the server clock).</summary>
    public Instant CreatedUtc { get; private set; }

    /// <summary>The time provider that gave <see cref="CreatedUtc"/>.</summary>
    public string CreatedProvider { get; private set; } = null!;
}
