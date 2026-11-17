using ClockManager.Api.Application.Time;
using Microsoft.AspNetCore.Mvc;

namespace ClockManager.Api.Controllers;

/// <summary>
/// <c>GET /health/time</c>: can a punch get a trusted time right now? (plans/01-trusted-clock.md §4.7, D-012).
/// Public, all environments. 200 when yes, 503 when no; the body has the same shape either way and contains
/// short outcome codes only. Served from a 10 s cache.
/// </summary>
[ApiController]
[Route("health/time")]
[Produces("application/json")]
public sealed class TimeHealthController(ITimeHealth health) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<TimeHealthReport>(StatusCodes.Status200OK)]
    [ProducesResponseType<TimeHealthReport>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<TimeHealthReport>> Get(CancellationToken ct)
    {
        var report = await health.GetAsync(ct);
        return StatusCode(report.CanPunch ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, report);
    }
}
