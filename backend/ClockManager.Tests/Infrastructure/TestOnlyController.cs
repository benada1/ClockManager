using System.ComponentModel.DataAnnotations;
using ClockManager.Api.Common.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ClockManager.Tests.Infrastructure;

/// <summary>Endpoints that exist only in the test host, used to exercise the error pipeline.</summary>
[ApiController]
[Route("test-only")]
public sealed class TestOnlyController : ControllerBase
{
    public const string SecretMessage = "secret-internal-detail-6f1c";

    [HttpGet("app-error")]
    public IActionResult AppError() =>
        throw new AppException(
            ErrorCodes.ShiftAlreadyOpen,
            StatusCodes.Status409Conflict,
            "A shift is already open.",
            "Clock out before clocking in again.",
            new Dictionary<string, object?> { ["openShiftId"] = 42 });

    [HttpGet("unhandled")]
    public IActionResult Unhandled() => throw new InvalidOperationException(SecretMessage);

    [HttpPost("validate")]
    public IActionResult Validate(ValidatedBody body) => Ok(body);

    public sealed class ValidatedBody
    {
        [Required]
        public string? Name { get; set; }
    }
}
