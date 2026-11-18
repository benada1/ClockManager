using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ClockManager.Api.Application.Shifts;

namespace ClockManager.Api.Application.Punches;

public enum PunchIntent
{
    ClockIn,
    ClockOut,
}

/// <summary>What a punch answers: the status (201; a replay returns the stored one) and the shift body.</summary>
public sealed record PunchResult(int Status, ShiftView Shift);

/// <summary>
/// Body of <c>POST /api/punches</c>: the intent only, never a time (CLAUDE.md rule 1).
/// <para>
/// Read STRICTLY on purpose (D-029): any member other than <c>intent</c> (e.g. <c>"time"</c>, <c>"clockInUtc"</c>) makes the
/// body invalid, so the request is a 400 <c>request.invalid</c> before any service code runs: nothing is saved and no
/// provider is asked. A member that appears twice is a 400 as well (QA-17, D-041): <see cref="PunchRequestJsonConverter"/>
/// reads this type, keeps the strict rule and rejects duplicates. It applies to this type only; the rest of the API keeps the
/// default JSON settings.
/// </para>
/// </summary>
[JsonConverter(typeof(PunchRequestJsonConverter))]
public sealed class PunchRequest
{
    public const string ClockIn = "clock-in";
    public const string ClockOut = "clock-out";

    /// <summary>Client-facing text, shown under the <c>intent</c> key of <c>errors</c> (D-040).</summary>
    public const string IntentMessage = "must be \"clock-in\" or \"clock-out\"";

    /// <summary><c>"clock-in"</c> or <c>"clock-out"</c>, exact and lower case.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = IntentMessage)]
    [RegularExpression("^(clock-in|clock-out)$", ErrorMessage = IntentMessage)]
    public string? Intent { get; init; }

    /// <summary>The validated intent. Call only after model validation passed.</summary>
    public PunchIntent ToIntent() => Intent switch
    {
        ClockIn => PunchIntent.ClockIn,
        ClockOut => PunchIntent.ClockOut,
        _ => throw new InvalidOperationException($"Unvalidated intent '{Intent}'."),
    };
}
