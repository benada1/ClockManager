using ClockManager.Api.Application.Time;
using ClockManager.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace ClockManager.Api.Controllers.Dev;

/// <summary>
/// <c>GET /api/dev/time-lab</c>, Development only (D-013): asks both real providers once, through the punch path's
/// orchestrator, and shows every provider result plus the decision. Always 200, also when the decision is a
/// failure: it is a diagnostic, not a punch. Never cached.
/// </summary>
[ApiController]
[DevelopmentOnly]
[Route("api/dev/time-lab")]
[Produces("application/json")]
public sealed class TimeLabController(ITimeLab lab) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<TimeLabResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TimeLabResponse>> Get(CancellationToken ct) => Ok(await lab.RunAsync(ct));
}
