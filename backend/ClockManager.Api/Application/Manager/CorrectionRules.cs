using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using NodaTime;

namespace ClockManager.Api.Application.Manager;

/// <summary>The correction rules that need no database (plan 07 §3), unit-tested with fixed instants.</summary>
public static class CorrectionRules
{
    /// <summary>
    /// Checks, in order: unchanged, in the future, out after in (on the effective values). Then the service checks
    /// overlap (needs the other shifts) and <see cref="EnsureMayClose"/>.
    /// </summary>
    /// <returns>The effective (clock-in, clock-out) after the change.</returns>
    public static (Instant In, Instant? Out) Check(Shift shift, ParsedCorrection change, Instant trustedNow)
    {
        ArgumentNullException.ThrowIfNull(shift);
        ArgumentNullException.ThrowIfNull(change);

        var current = change.Field == ShiftCorrectionField.ClockIn ? shift.ClockIn.Utc : shift.ClockOut?.Utc;
        if (current == change.NewUtc) throw ManagerErrors.Unchanged();
        if (change.NewUtc > trustedNow) throw ManagerErrors.InFuture();

        var newIn = change.Field == ShiftCorrectionField.ClockIn ? change.NewUtc : shift.ClockIn.Utc;
        var newOut = change.Field == ShiftCorrectionField.ClockOut ? change.NewUtc : shift.ClockOut?.Utc;
        if (newOut is { } o && o <= newIn) throw ManagerErrors.InvalidOrder();
        return (newIn, newOut);
    }

    /// <summary>Closing an open shift is allowed only when it is forgotten (open longer than MaxOpenShiftHours, strict).</summary>
    public static void EnsureMayClose(Shift shift, ParsedCorrection change, Instant trustedNow, ShiftRulesOptions rules)
    {
        if (change.Field == ShiftCorrectionField.ClockOut && shift.IsOpen && !ShiftFlags.IsForgotten(shift, trustedNow, rules))
            throw ManagerErrors.StillRunning(rules.MaxOpenShiftHours);
    }
}

/// <summary>Manager use cases' expected failures, with their stable codes (plan 07 §5).</summary>
public static class ManagerErrors
{
    public const string ConflictingShiftKey = "conflictingShiftId";

    public static AppException ShiftNotFound() => new(
        ErrorCodes.RequestNotFound, StatusCodes.Status404NotFound, "Shift not found.", "There is no shift with this id.");

    public static AppException EmployeeNotFound() => new(
        ErrorCodes.RequestNotFound, StatusCodes.Status404NotFound, "Employee not found.", "There is no employee with this id.");

    public static AppException Stale() => new(
        ErrorCodes.CorrectionStale, StatusCodes.Status409Conflict, "The shift was changed meanwhile.",
        "Someone changed this shift after you loaded it. Reload it and try again. Nothing was saved.");

    public static AppException Unchanged() => new(
        ErrorCodes.CorrectionUnchanged, StatusCodes.Status422UnprocessableEntity, "The new time is the current time.",
        "The new time is the same as the current one. Nothing was saved.");

    public static AppException InFuture() => new(
        ErrorCodes.CorrectionInFuture, StatusCodes.Status422UnprocessableEntity, "The new time is in the future.",
        "A correction can't set a time later than now (trusted clock). Nothing was saved.");

    public static AppException InvalidOrder() => new(
        ErrorCodes.CorrectionInvalidOrder, StatusCodes.Status422UnprocessableEntity, "Clock out must be after clock in.",
        "With this change the clock out would not be later than the clock in. Nothing was saved.");

    public static AppException Overlap(long otherShiftId) => new(
        ErrorCodes.CorrectionOverlap, StatusCodes.Status409Conflict, "The shift would overlap another shift.",
        "With this change the shift would overlap or touch another shift of the same employee. Nothing was saved.",
        new Dictionary<string, object?> { [ConflictingShiftKey] = otherShiftId });

    public static AppException StillRunning(int maxOpenHours) => new(
        ErrorCodes.CorrectionShiftStillRunning, StatusCodes.Status409Conflict, "The shift may still be running.",
        $"Only a shift open for more than {maxOpenHours} hours (a forgotten clock-out) can be closed by a manager. Nothing was saved.");
}
