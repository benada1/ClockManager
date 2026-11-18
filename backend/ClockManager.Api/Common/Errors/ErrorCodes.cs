namespace ClockManager.Api.Common.Errors;

/// <summary>
/// Stable, machine-readable error codes returned in the <c>code</c> extension of every
/// ProblemDetails response. Clients and tests rely on these values, so never rename one;
/// add a new code instead. See master plan D9.
/// </summary>
public static class ErrorCodes
{
    // Shift rules
    public const string ShiftAlreadyOpen = "shift.already_open";
    public const string ShiftNotOpen = "shift.not_open";

    // Trusted time
    public const string TimeUnavailable = "time.unavailable";
    public const string TimeInvalidResponse = "time.invalid_response";
    public const string TimeProvidersDisagree = "time.providers_disagree";
    public const string TimeWentBackwards = "time.went_backwards";

    // Idempotency
    public const string IdempotencyKeyReused = "idempotency.key_reused";
    public const string IdempotencyKeyMissing = "idempotency.key_missing";

    // Manager corrections (plans/07-manager.md §5)
    public const string CorrectionShiftStillRunning = "correction.shift_still_running";
    public const string CorrectionOverlap = "correction.overlap";
    public const string CorrectionStale = "correction.stale";
    public const string CorrectionInvalidOrder = "correction.invalid_order";
    public const string CorrectionInFuture = "correction.in_future";
    public const string CorrectionUnchanged = "correction.unchanged";

    // Employees
    public const string EmployeeInactive = "employee.inactive";

    // Auth (the generic Unauthenticated / Forbidden codes are below)
    public const string AuthInvalidCredentials = "auth.invalid_credentials";
    public const string AuthUsernameTaken = "auth.username_taken";

    // Generic, derived from the HTTP status when nothing more specific is known
    public const string RequestInvalid = "request.invalid";
    public const string Unauthenticated = "auth.unauthenticated";
    public const string Forbidden = "auth.forbidden";
    public const string RequestNotFound = "request.not_found";
    public const string MethodNotAllowed = "request.method_not_allowed";
    public const string RequestConflict = "request.conflict";
    public const string UnsupportedMediaType = "request.unsupported_media_type";
    public const string RateLimited = "request.rate_limited";
    public const string RequestError = "request.error";
    public const string ServerError = "server.error";
    public const string ServiceUnavailable = "server.unavailable";

    /// <summary>Fallback code for a response that has a status but no specific code.</summary>
    public static string ForStatus(int statusCode) => statusCode switch
    {
        400 => RequestInvalid,
        401 => Unauthenticated,
        403 => Forbidden,
        404 => RequestNotFound,
        405 => MethodNotAllowed,
        409 => RequestConflict,
        415 => UnsupportedMediaType,
        429 => RateLimited,
        503 => ServiceUnavailable,
        >= 500 => ServerError,
        _ => RequestError,
    };
}
