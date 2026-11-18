using ClockManager.Api.Application.History;
using ClockManager.Api.Common.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ClockManager.Api.Controllers;

/// <summary>
/// <c>GET /api/me/shifts?from&amp;to</c>: the signed-in employee's shifts by Zurich work date (plans/06-history.md §3).
/// Dates arrive as text so a bad value gets our fixed message, not the framework's (which echoes the input).
/// </summary>
[ApiController]
[Authorize]
[Route("api/me")]
public sealed class MyShiftsController(ShiftHistoryService history) : ControllerBase
{
    [HttpGet("shifts")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<ShiftHistoryView>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    public async Task<IActionResult> GetShifts([FromQuery] string? from, [FromQuery] string? to, CancellationToken ct)
    {
        // Binding turns "?from=" into null; read the raw query so an empty value counts as given (and invalid).
        from = Request.Query.TryGetValue("from", out var f) ? f.ToString() : null;
        to = Request.Query.TryGetValue("to", out var t) ? t.ToString() : null;
        var errors = new Dictionary<string, string>();
        var range = ShiftHistoryService.TryParseRange(from, to, errors);
        if (errors.Count > 0)
        {
            var state = new ModelStateDictionary();
            foreach (var (key, message) in errors) state.AddModelError(key, message);
            return InvalidRequestResponse.Create(new ActionContext(HttpContext, RouteData, ControllerContext.ActionDescriptor, state));
        }
        return Ok(await history.GetAsync(range, ct));
    }
}
