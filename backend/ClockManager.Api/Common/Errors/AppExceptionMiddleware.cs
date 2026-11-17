using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Net.Http.Headers;

namespace ClockManager.Api.Common.Errors;

/// <summary>
/// Catches <see cref="AppException"/> (handled business refusals: 409, 422, 503, 403, 400) one step inside the framework's
/// <c>ExceptionHandlerMiddleware</c>, so the framework never sees it and never logs its "unhandled exception" Error.
/// It does not build a body itself: it hands the exception to <see cref="AppExceptionHandler"/>, the one place that
/// produces the ProblemDetails (and logs at Information/Warning). Anything else is rethrown to the framework middleware.
/// </summary>
public sealed class AppExceptionMiddleware(RequestDelegate next, IEnumerable<IExceptionHandler> handlers)
{
    private readonly AppExceptionHandler _handler = handlers.OfType<AppExceptionHandler>().Single();

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException ex) when (!context.Response.HasStarted)
        {
            // Same reset the framework middleware does before it writes an error response.
            context.Response.Clear();
            context.Response.Headers.CacheControl = "no-cache,no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Remove(HeaderNames.ETag);

            if (!await _handler.TryHandleAsync(context, ex, context.RequestAborted))
            {
                throw;
            }
        }
    }
}
