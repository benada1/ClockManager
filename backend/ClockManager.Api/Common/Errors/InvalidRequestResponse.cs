using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ClockManager.Api.Common.Errors;

/// <summary>
/// Builds the 400 <c>request.invalid</c> body for a request MVC could not bind or validate (D-040, QA-19).
/// <para>
/// The framework's own messages carry CLR type names (<c>...Punches.PunchRequest</c>), parser positions
/// (<c>LineNumber | BytePositionInLine</c>) and JSON paths. Those are an information leak and are not a stable contract, so
/// every message that comes from the JSON reader is replaced by a FIXED sentence here. Messages written by us (the
/// <c>ErrorMessage</c> of a validation attribute) pass through unchanged. The status, the <c>code</c>, the
/// <c>application/problem+json</c> media type and the <c>errors</c> shape are the same as before: this only decides the text.
/// </para>
/// </summary>
public static partial class InvalidRequestResponse
{
    /// <summary>Key used when the whole body is the problem (not JSON, wrong top-level type, missing).</summary>
    public const string BodyKey = "body";

    public const string BodyMessage = "must be a JSON object";
    public const string WrongTypeMessage = "has the wrong type or an invalid value";
    public const int MaxFieldNameLength = 40;

    public static IActionResult Create(ActionContext context)
    {
        var sanitized = Sanitize(context.ModelState);
        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(context.HttpContext, sanitized);
        // Same media types the framework's default factory declares (D-039).
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json", "application/problem+xml" } };
    }

    public static ModelStateDictionary Sanitize(ModelStateDictionary source)
    {
        var result = new ModelStateDictionary();
        var hasJsonError = source.Any(e => e.Value is not null && e.Value.Errors.Any(er => IsJsonError(e.Key, er)));
        var bodyAdded = false;
        void AddBody()
        {
            if (bodyAdded) return;
            bodyAdded = true;
            result.TryAddModelError(BodyKey, BodyMessage);
        }

        foreach (var (key, entry) in source)
        {
            foreach (var error in entry.Errors)
            {
                if (IsJsonError(key, error))
                {
                    var field = FieldName(key);
                    if (field is null)
                    {
                        AddBody();
                    }
                    else if (IsUnmappedMember(error))
                    {
                        result.TryAddModelError(field, $"unknown field '{field}' is not allowed; the server sets the time");
                    }
                    else
                    {
                        result.TryAddModelError(field, WrongTypeMessage);
                    }
                }
                else if (key.Length == 0 || key == "request")
                {
                    // "A non-empty request body is required." / "The request field is required.": the body itself is the
                    // problem. When the JSON reader already complained, this entry is noise and is dropped.
                    if (!hasJsonError) AddBody();
                }
                else
                {
                    result.TryAddModelError(CamelCase(Clean(key)), error.ErrorMessage);
                }
            }
        }
        if (result.ErrorCount == 0) AddBody(); // never an empty errors object
        return result;
    }

    // The JSON input formatter stores a JsonException (or InputFormatterException wrapping one) and a path key "$" / "$.x".
    private static bool IsJsonError(string key, ModelError error) =>
        key.StartsWith('$')
        || error.Exception is JsonException or Microsoft.AspNetCore.Mvc.Formatters.InputFormatterException
        || error.ErrorMessage.Contains("LineNumber", StringComparison.Ordinal)
        || error.ErrorMessage.Contains("could not be converted", StringComparison.Ordinal);

    private static bool IsUnmappedMember(ModelError error) =>
        UnmappedMember().IsMatch(error.ErrorMessage) || (error.Exception is not null && UnmappedMember().IsMatch(error.Exception.Message));

    /// <summary>"$.time" → "time", "$" → null (whole body). Echoes the client's own name, cleaned and capped.</summary>
    private static string? FieldName(string key)
    {
        var name = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key == "$" ? "" : key;
        name = Clean(name);
        return name.Length == 0 ? null : name;
    }

    /// <summary>Removes control characters, quotes and path/bracket syntax; caps the length. Client text is never trusted.</summary>
    private static string Clean(string value)
    {
        var sb = new StringBuilder(Math.Min(value.Length, MaxFieldNameLength));
        foreach (var c in value)
        {
            if (sb.Length >= MaxFieldNameLength) break;
            if (char.IsControl(c) || c is '\'' or '"' or '$') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string CamelCase(string name) => name.Length == 0 ? name : JsonNamingPolicy.CamelCase.ConvertName(name);

    [GeneratedRegex(@"could not be mapped to any \.NET member", RegexOptions.CultureInvariant)]
    private static partial Regex UnmappedMember();
}
