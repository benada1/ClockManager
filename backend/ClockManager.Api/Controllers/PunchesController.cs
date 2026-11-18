using ClockManager.Api.Application.Punches;
using ClockManager.Api.Application.Shifts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClockManager.Api.Controllers;

/// <summary>
/// <c>POST /api/punches</c>: Clock In or Clock Out (plans/03-punch-api.md §4). The body is the intent only; the time
/// comes from the trusted clock on the server. Checks run in this order: login cookie (401, before the body is read) → body (400 <c>request.invalid</c>) → employee
/// active (403) → <c>Idempotency-Key</c> (400) → a known key is replayed (422 for the other intent) → shift state (409) → trusted time (503).
/// </summary>
[ApiController]
[Authorize]
[Route("api/punches")]
public sealed class PunchesController(PunchService punches) : ControllerBase
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <param name="request"><c>{ "intent": "clock-in" | "clock-out" }</c>. Any other member is a 400.</param>
    /// <param name="idempotencyKey">Required: a new UUID per punch, the same one on a retry.</param>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<ShiftView>(StatusCodes.Status201Created, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
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
