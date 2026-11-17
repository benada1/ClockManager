using ClockManager.Api.Application.Status;
using Microsoft.AspNetCore.Mvc;

namespace ClockManager.Api.Controllers;

/// <summary>
/// <c>GET /api/me/status</c>: the signed-in employee's state for the UI (plans/03-punch-api.md §4). Who → 401 / 403 as for
/// punches. A time-provider problem is NOT an error here: <c>serverNow</c> is null and the time-based flags are left out.
/// </summary>
[ApiController]
[Route("api/me")]
[Produces("application/json")]
public sealed class MeController(StatusService status) : ControllerBase
{
    /// <summary>
    /// Open shift, last closed shift, flags, and a labeled "server now" (display only, cached up to 10 s, with its age).
    /// </summary>
    [HttpGet("status")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)] // per-employee, changes with every punch
    [ProducesResponseType<StatusView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<StatusView>> GetStatus(CancellationToken ct) => await status.GetAsync(ct);
}
