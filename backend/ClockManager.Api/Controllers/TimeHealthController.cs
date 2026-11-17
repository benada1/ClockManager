using ClockManager.Api.Application.Time;
using ClockManager.Api.Common.Errors;
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
        TimeHealthReport report;
        try
        {
            report = await health.GetAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The caller is still here but the check was cancelled: the app is shutting down (the refresh runs on
            // the stopping token). A punch can't get a time right now, which is a 503, not a server bug (QA-10).
            report = new TimeHealthReport(false, [], TimeHealthReport.NotChecked, null, ErrorCodes.TimeUnavailable);
        }
        return StatusCode(report.CanPunch ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, report);
    }
}
