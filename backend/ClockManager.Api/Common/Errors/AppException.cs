namespace ClockManager.Api.Common.Errors;

/// <summary>
/// An expected failure (broken business rule, untrusted time, ...) that application services
/// throw and <see cref="AppExceptionHandler"/> turns into an RFC 7807 ProblemDetails response.
/// <see cref="Exception.Message"/> is the <see cref="Title"/>; <see cref="Detail"/> is shown to the client,
/// so never put internal information (stack traces, SQL, provider secrets) in either.
/// </summary>
public class AppException : Exception
{
    public AppException(
        string code,
        int statusCode,
        string title,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? extensions = null,
        Exception? innerException = null)
        : base(title, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (statusCode is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "Must be a 4xx or 5xx status.");
        }

        Code = code;
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        Extensions = extensions ?? new Dictionary<string, object?>();
    }

    /// <summary>Stable error code, one of <see cref="ErrorCodes"/>.</summary>
    public string Code { get; }

    public int StatusCode { get; }

    public string Title { get; }

    public string? Detail { get; }

    /// <summary>Extra members added to the ProblemDetails body (e.g. the current shift state on a 409).</summary>
    public IReadOnlyDictionary<string, object?> Extensions { get; }
}
