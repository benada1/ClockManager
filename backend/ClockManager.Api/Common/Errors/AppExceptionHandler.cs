using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ClockManager.Api.Common.Errors;

/// <summary>
/// Maps exceptions to ProblemDetails. <see cref="AppException"/> keeps its status, title, detail and code.
/// Anything else becomes a generic 500 <c>server.error</c>: the exception is logged, never sent to the client.
/// </summary>
public sealed class AppExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to send a body to.
            logger.LogInformation("Request {Method} {Path} was cancelled by the client.", httpContext.Request.Method, httpContext.Request.Path);
            return true;
        }

        ProblemDetails problem;
        if (exception is AppException app)
        {
            var level = app.StatusCode >= 500 ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, exception, "Request failed with {Code} ({StatusCode}).", app.Code, app.StatusCode);

            problem = new ProblemDetails
            {
                Status = app.StatusCode,
                Title = app.Title,
                Detail = app.Detail,
            };
            foreach (var (key, value) in app.Extensions)
            {
                problem.Extensions[key] = value;
            }
            problem.Extensions[ProblemDetailsSetup.CodeKey] = app.Code;
        }
        else
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);

            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Detail = "The request could not be completed. Quote the traceId when reporting this problem.",
            };
            problem.Extensions[ProblemDetailsSetup.CodeKey] = ErrorCodes.ServerError;
        }

        httpContext.Response.StatusCode = problem.Status.Value;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            // Deliberately not setting Exception: keeps exception details out of every writer.
        });
    }
}
