using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;

namespace ClockManager.Api.Application.Employees;

/// <summary>The employee making the current request, as loaded from the database.</summary>
public sealed record CurrentEmployee(int Id, string Username, string DisplayName, EmployeeRole Role, bool IsActive);

/// <summary>
/// Who is making the request (plans/03-punch-api.md §3, D-027). Phase 3: the Development-only
/// <c>X-Dev-Employee</c> header; Phase 4 replaces it with cookie login. Services depend only on this interface,
/// so the swap doesn't touch them.
/// </summary>
public interface ICurrentEmployee
{
    /// <summary>The signed-in employee, or null when nobody is signed in (→ 401 <c>auth.unauthenticated</c>).</summary>
    Task<CurrentEmployee?> GetAsync(CancellationToken ct);
}

public static class CurrentEmployeeExtensions
{
    /// <summary>
    /// The signed-in, active employee. Nobody (or an unknown user) → 401 <c>auth.unauthenticated</c>;
    /// a deactivated employee → 403 <c>employee.inactive</c>.
    /// </summary>
    public static async Task<CurrentEmployee> RequireActiveAsync(this ICurrentEmployee current, CancellationToken ct)
    {
        var employee = await current.GetAsync(ct)
            ?? throw new AppException(
                ErrorCodes.Unauthenticated,
                StatusCodes.Status401Unauthorized,
                "You are not signed in.",
                "Sign in before clocking in or out.");

        if (!employee.IsActive)
        {
            throw new AppException(
                ErrorCodes.EmployeeInactive,
                StatusCodes.Status403Forbidden,
                "This employee account is deactivated.",
                "A deactivated employee can't clock in or out. Ask a manager to reactivate the account.");
        }

        return employee;
    }
}
