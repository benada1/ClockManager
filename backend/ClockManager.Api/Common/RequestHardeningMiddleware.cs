using ClockManager.Api.Common.Errors;
using Microsoft.Net.Http.Headers;

namespace ClockManager.Api.Common;

/// <summary>Adds <c>X-Content-Type-Options: nosniff</c> to every response (QA-44); set at send time so error resets keep it.</summary>
public sealed class NoSniffMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            ((HttpContext)state).Response.Headers.XContentTypeOptions = "nosniff";
            return Task.CompletedTask;
        }, context);
        return next(context);
    }
}

/// <summary>
/// A JSON body may declare no charset or utf-8 only (QA-42). utf-16 and an empty <c>charset=</c> make the JSON reader throw
/// (a 500 and an Error log); every other charset is already a 415, so all of them are the same clean 415 here.
/// </summary>
public sealed class JsonCharsetMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var raw = context.Request.ContentType;
        if (raw is not null && raw.Contains("json", StringComparison.OrdinalIgnoreCase) && !CharsetIsOk(raw))
        {
            throw new AppException(ErrorCodes.UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType,
                "Unsupported media type.", "Send JSON as application/json, UTF-8 (or without a charset).");
        }
        return next(context);
    }

    private static bool CharsetIsOk(string contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            return !contentType.Contains("charset", StringComparison.OrdinalIgnoreCase); // "charset=" alone may not parse
        }
        if (parsed.Charset.Length == 0)
        {
            return !contentType.Contains("charset", StringComparison.OrdinalIgnoreCase); // empty charset=
        }
        return string.Equals(parsed.Charset.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase); // quoted form too
    }
}
