using ClockManager.Api.Application.Punches;
using ClockManager.Api.Application.Shifts;
using Microsoft.AspNetCore.Mvc;

namespace ClockManager.Api.Controllers;

/// <summary>
/// <c>POST /api/punches</c>: Clock In or Clock Out (plans/03-punch-api.md §4). The body is the intent only; the time
/// comes from the trusted clock on the server. Checks run in this order: body (400 <c>request.invalid</c>) → who
/// (401 / 403) → <c>Idempotency-Key</c> (400) → a known key is replayed (422 for the other intent) → shift state (409) → trusted time (503).
/// </summary>
[ApiController]
[Route("api/punches")]
[Produces("application/json")]
public sealed class PunchesController(PunchService punches) : ControllerBase
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <param name="request"><c>{ "intent": "clock-in" | "clock-out" }</c>. Any other member is a 400.</param>
    /// <param name="idempotencyKey">Required: a new UUID per punch, the same one on a retry.</param>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<ShiftView>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ShiftView>> Punch(
        [FromBody] PunchRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CancellationToken ct)
    {
        var result = await punches.PunchAsync(request.ToIntent(), idempotencyKey, ct);
        // No GET-by-id endpoint exists yet, so 201 without a Location header. A replay answers with the stored status/body.
        return StatusCode(result.Status, result.Shift);
    }
}
