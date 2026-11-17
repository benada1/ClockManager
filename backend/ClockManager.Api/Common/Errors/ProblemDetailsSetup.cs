using System.Diagnostics;

namespace ClockManager.Api.Common.Errors;

/// <summary>
/// Registers RFC 7807 ProblemDetails for every error path: <see cref="AppException"/>s, unhandled exceptions,
/// empty-body status codes (404, 405, ...) and MVC model validation (400). Every body gets
/// <c>code</c> (stable error code) and <c>traceId</c> (to correlate with the logs).
/// </summary>
public static class ProblemDetailsSetup
{
    public const string CodeKey = "code";
    public const string TraceIdKey = "traceId";

    public static IServiceCollection AddAppProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = ctx =>
            {
                var problem = ctx.ProblemDetails;
                var http = ctx.HttpContext;

                problem.Status ??= http.Response.StatusCode;
                problem.Instance ??= http.Request.Path;

                problem.Extensions[TraceIdKey] = Activity.Current?.Id ?? http.TraceIdentifier;

                if (!problem.Extensions.TryGetValue(CodeKey, out var code) || code is not string { Length: > 0 })
                {
                    problem.Extensions[CodeKey] = ErrorCodes.ForStatus(problem.Status ?? StatusCodes.Status500InternalServerError);
                }
            };
        });
        services.AddExceptionHandler<AppExceptionHandler>();
        return services;
    }

    /// <summary>Exception handler first, so it wraps everything; then status-code pages for empty error responses.</summary>
    public static IApplicationBuilder UseAppProblemDetails(this IApplicationBuilder app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }
}
