using ClockManager.Api.Application.Employees;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using Microsoft.AspNetCore.Authorization;

namespace ClockManager.Api.Infrastructure.Identity;

/// <summary>
/// <c>[Authorize(Policy = ManagerPolicy.Name)]</c>: the employee's CURRENT database role must be Manager (plan 07 §3.1).
/// Runs before model binding, so a non-manager gets 403 before any body is validated.
/// </summary>
public static class ManagerPolicy
{
    public const string Name = "Manager";

    public sealed class Requirement : IAuthorizationRequirement;

    /// <summary>Loads the employee from the database; the JWT role claim is ignored on purpose (it can be stale).</summary>
    internal sealed class Handler(ICurrentEmployee current, IHttpContextAccessor http) : AuthorizationHandler<Requirement>
    {
        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, Requirement requirement)
        {
            if (context.User.Identity?.IsAuthenticated != true) return; // anonymous: the policy's own challenge answers 401
            var ct = http.HttpContext?.RequestAborted ?? CancellationToken.None;
            // Nobody (deleted employee, bad sub) is "not signed in": 401 like /api/me/*, not 403 (QA-45).
            var employee = await current.GetAsync(ct)
                ?? throw new AppException(ErrorCodes.Unauthenticated, StatusCodes.Status401Unauthorized, "You are not signed in.", "Sign in to continue. Your session may have expired.");
            if (employee is { IsActive: true, Role: EmployeeRole.Manager }) context.Succeed(requirement);
        }
    }
}
