using ClockManager.Api.Application.History;
using ClockManager.Api.Application.Manager;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ClockManager.Api.Controllers;

/// <summary>
/// Manager view and corrections (plans/07-manager.md §5). The policy checks the CURRENT database role before the body is
/// read: anonymous 401, non-manager 403 <c>auth.forbidden</c>. The services check it again.
/// </summary>
[ApiController]
[Authorize(Policy = ManagerPolicy.Name)]
[Route("api/manager")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
public sealed class ManagerController(ManagerService manager, ShiftCorrectionService corrections, ZurichTime zurich) : ControllerBase
{
    [HttpGet("employees")]
    [ProducesResponseType<IReadOnlyList<ManagerEmployeeView>>(StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<IReadOnlyList<ManagerEmployeeView>>> Employees(CancellationToken ct) =>
        Ok(await manager.ListEmployeesAsync(ct));

    [HttpGet("anomalies")]
    [ProducesResponseType<IReadOnlyList<AnomalyView>>(StatusCodes.Status200OK, "application/json")]
    public async Task<ActionResult<IReadOnlyList<AnomalyView>>> Anomalies(CancellationToken ct) =>
        Ok(await manager.ListAnomaliesAsync(ct));

    /// <summary>The <c>GET /api/me/shifts</c> body for that employee (shifts with <c>rowVersion</c>) plus <c>employee</c>.</summary>
    [HttpGet("employees/{id:int}/shifts")]
    [ProducesResponseType<ManagerShiftHistoryView>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> EmployeeShifts(int id, CancellationToken ct)
    {
        // Raw query text, as in MyShiftsController: an empty value counts as given (and invalid).
        var from = Request.Query.TryGetValue("from", out var f) ? f.ToString() : null;
        var to = Request.Query.TryGetValue("to", out var t) ? t.ToString() : null;
        var errors = new Dictionary<string, string>();
        var range = ShiftHistoryService.TryParseRange(from, to, errors);
        if (errors.Count > 0) return Invalid(errors);
        return Ok(await manager.GetEmployeeShiftsAsync(id, range, ct));
    }

    [HttpGet("shifts/{id:long}")]
    [ProducesResponseType<ShiftDetailView>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<ShiftDetailView>> Shift(long id, CancellationToken ct) => Ok(await manager.GetShiftAsync(id, ct));

    /// <summary>Corrects one punch. The time is typed by the manager with its Zurich offset; the audit time is trusted.</summary>
    [HttpPost("shifts/{id:long}/corrections")]
    [RequestSizeLimit(16384)] // 16 KB: a correction is a few short fields (QA-48)
    [Consumes("application/json")]
    [ProducesResponseType<ShiftDetailView>(StatusCodes.Status201Created, "application/json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    public async Task<IActionResult> Correct(long id, [FromBody] CorrectionRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        var change = CorrectionInput.TryParse(request, zurich, errors);
        if (change is null) return Invalid(errors);
        var detail = await corrections.CorrectAsync(id, change, ct);
        return Created($"/api/manager/shifts/{id}", detail);
    }

    private IActionResult Invalid(Dictionary<string, string> errors)
    {
        var state = new ModelStateDictionary();
        foreach (var (key, message) in errors) state.AddModelError(key, message);
        return InvalidRequestResponse.Create(new ActionContext(HttpContext, RouteData, ControllerContext.ActionDescriptor, state));
    }
}
