using System.ComponentModel.DataAnnotations;

namespace ClockManager.Api.Application.Shifts;

/// <summary>
/// Configurable shift rules (section <c>ShiftRules</c>). Validated on startup: the host refuses to
/// start with values outside the ranges below.
/// </summary>
public sealed class ShiftRulesOptions
{
    public const string SectionName = "ShiftRules";

    /// <summary>An open shift older than this is flagged as a possible forgotten clock-out. Never auto-closed.</summary>
    [Range(1, 48)]
    public int MaxOpenShiftHours { get; set; } = 16;

    /// <summary>A closed shift shorter than this is flagged as a possible accidental double punch.</summary>
    [Range(1, 3600)]
    public int ShortShiftSeconds { get; set; } = 60;
}
