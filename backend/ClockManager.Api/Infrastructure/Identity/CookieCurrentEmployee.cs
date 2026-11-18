using System.Globalization;
using ClockManager.Api.Application.Employees;
using ClockManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClockManager.Api.Infrastructure.Identity;

/// <summary>
/// The employee behind the validated login cookie: the <c>sub</c> claim is the id, and the employee is loaded on every request,
/// so a deactivated employee is blocked at once even with a valid token.
/// </summary>
internal sealed class CookieCurrentEmployee(IHttpContextAccessor httpContextAccessor, ClockManagerDbContext db) : ICurrentEmployee
{
    public async Task<CurrentEmployee?> GetAsync(CancellationToken ct)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return null;

        var sub = user.FindFirst(JwtSettings.SubjectClaim)?.Value;
        if (!int.TryParse(sub, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;

        // Employee deleted since the token was issued → null → 401.
        return await db.Employees
            .AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new CurrentEmployee(e.Id, e.Username, e.DisplayName, e.Role, e.IsActive))
            .SingleOrDefaultAsync(ct);
    }
}
