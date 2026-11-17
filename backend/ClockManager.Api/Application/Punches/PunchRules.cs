using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Common.Errors;
using NodaTime;

namespace ClockManager.Api.Application.Punches;

/// <summary>Punch rules that need no database, so they are unit-tested with fixed instants (plan 03 §9).</summary>
public static class PunchRules
{
    /// <summary>
    /// Monotonic guard (D-030): a new punch must be STRICTLY later than the employee's latest punch (their latest
    /// clock-in or clock-out). Equal counts as backwards: a zero-length shift (T11) or two punches at the same instant
    /// can't be told apart. Otherwise → 503 <c>time.went_backwards</c>: our time source is wrong, not the employee.
    /// </summary>
    public static void EnsureLaterThanLastPunch(Instant trustedNow, Instant? lastPunch)
    {
        if (Check(trustedNow, lastPunch, lastPunch) != GuardResult.Ok)
        {
            throw PunchErrors.WentBackwards();
        }
    }

    public enum GuardResult
    {
        Ok,

        /// <summary>Not later than a punch that already existed before the provider call: the time source is wrong (503).</summary>
        WentBackwards,

        /// <summary>
        /// Not later than a punch that was saved DURING our provider call (another tab, a double click): the state changed
        /// under us, so this is a conflict (409), not a time problem. Both requests got nearly the same trusted time.
        /// </summary>
        PunchedMeanwhile,
    }

    /// <param name="trustedNow">The instant from the trusted clock.</param>
    /// <param name="lastPunchBefore">The employee's latest punch, read at the state check (before the provider call).</param>
    /// <param name="lastPunchNow">The employee's latest punch, read again after the provider call.</param>
    public static GuardResult Check(Instant trustedNow, Instant? lastPunchBefore, Instant? lastPunchNow)
    {
        if (lastPunchNow is not { } last || trustedNow > last) return GuardResult.Ok;
        return lastPunchNow == lastPunchBefore ? GuardResult.WentBackwards : GuardResult.PunchedMeanwhile;
    }
}

/// <summary>The punch use case's expected failures, with their stable codes (plan 03 §4).</summary>
public static class PunchErrors
{
    public const string OpenShiftKey = "openShift";

    /// <summary>409 <c>shift.already_open</c>; the body carries the open shift (null if it was closed meanwhile).</summary>
    public static AppException AlreadyOpen(ShiftView? openShift) => new(
        ErrorCodes.ShiftAlreadyOpen,
        StatusCodes.Status409Conflict,
        "You are already clocked in.",
        "A shift is already open. Clock out before clocking in again.",
        new Dictionary<string, object?> { [OpenShiftKey] = openShift });

    public static AppException NotOpen() => new(
        ErrorCodes.ShiftNotOpen,
        StatusCodes.Status409Conflict,
        "You are not clocked in.",
        "There is no open shift to clock out of. It may have been closed in another tab.");

    public static AppException WentBackwards() => new(
        ErrorCodes.TimeWentBackwards,
        StatusCodes.Status503ServiceUnavailable,
        "Trusted time went backwards.",
        "The time source returned a time that is not later than your last punch, so it can't be trusted right now. "
        + "Nothing was recorded. Please try again in a moment.");

    public static AppException KeyMissing() => new(
        ErrorCodes.IdempotencyKeyMissing,
        StatusCodes.Status400BadRequest,
        "Idempotency-Key header missing or invalid.",
        "Send an Idempotency-Key header with a new UUID (e.g. 3f1c0d7e-8a4b-4c55-9a51-0d2b7c1e9f10) for each punch, "
        + "and the same one when you retry that punch.");
}
