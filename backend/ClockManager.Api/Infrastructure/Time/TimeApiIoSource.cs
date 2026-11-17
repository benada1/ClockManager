using System.Text.Json;
using ClockManager.Api.Application.Time;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// timeapi.io (secondary provider, D-006): <c>GET api/time/current/zone?timeZone=Europe%2FZurich</c>.
/// The body gives a Zurich wall-clock time WITHOUT an offset, plus <c>dstActive</c> and the same time split into
/// fields. The offset is derived from <c>dstActive</c> and then checked against tzdb (D-011); every redundant field
/// must agree. A doubtful body is rejected, never repaired (plans/01-trusted-clock.md §4.3).
/// </summary>
internal sealed class TimeApiIoSource : HttpTimeSource
{
    /// <summary>Fixed identity of this provider, never taken from the Primary/Secondary config slot (see QA-03).</summary>
    internal const string ProviderName = "timeapi.io";

    internal const string Path = "api/time/current/zone?timeZone=Europe%2FZurich";

    private static readonly Offset StandardOffset = Offset.FromHours(1); // CET
    private static readonly Offset SummerOffset = Offset.FromHours(2);   // CEST

    /// <summary>
    /// The documented shape, e.g. <c>2026-11-17T14:18:43.8658902</c>: seconds always present, 0–7 fraction digits,
    /// no offset and no <c>Z</c>. Invariant culture, so the server's locale can't change how it is read.
    /// </summary>
    private static readonly LocalDateTimePattern DateTimePattern =
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF");

    private readonly ZurichTime _zurich;

    public TimeApiIoSource(HttpClient http, IElapsedTimer timer, IOptions<TrustedClockOptions> options, ZurichTime zurich)
        : base(http, timer, options, ProviderName)
    {
        _zurich = zurich;
    }

    protected override string RequestPath => Path;

    protected override ParseOutcome Parse(string body)
    {
        using var document = ProviderJson.ParseObject(body, out var jsonError);
        if (document is null) return ParseOutcome.Invalid(jsonError!);
        var root = document.RootElement;

        if (!ProviderJson.TryGetString(root, "timeZone", out var zone)) return Missing("timeZone");
        if (!string.Equals(zone, ZurichTime.ZoneId, StringComparison.Ordinal))
        {
            return ParseOutcome.Invalid($"timeZone is '{zone}', expected '{ZurichTime.ZoneId}'");
        }

        if (!ProviderJson.TryGetString(root, "dateTime", out var localText)) return Missing("dateTime");
        var parsed = DateTimePattern.Parse(localText);
        if (!parsed.Success)
        {
            return ParseOutcome.Invalid($"dateTime '{localText}' is not an offset-less ISO 8601 local date-time");
        }
        var local = parsed.Value;

        if (!ProviderJson.TryGetBoolean(root, "dstActive", out var dstActive)) return Missing("dstActive");

        // The split-out fields must describe exactly the same wall-clock time. milliSeconds is the TRUNCATED
        // millisecond part (the real provider sends 865 for .8658902), which is what LocalDateTime.Millisecond gives.
        var mismatch =
            Check(root, "year", local.Year)
            ?? Check(root, "month", local.Month)
            ?? Check(root, "day", local.Day)
            ?? Check(root, "hour", local.Hour)
            ?? Check(root, "minute", local.Minute)
            ?? Check(root, "seconds", local.Second)
            ?? Check(root, "milliSeconds", local.Millisecond);
        if (mismatch is not null) return ParseOutcome.Invalid(mismatch);

        // The local time has no offset; dstActive picks it. tzdb must confirm that this local time exists in Zurich
        // and that the chosen offset is one of its valid offsets (two in the repeated October hour, none in the
        // March gap). This resolves the repeated hour without guessing and rejects a dstActive that contradicts tzdb.
        var offset = dstActive ? SummerOffset : StandardOffset;
        var mapping = _zurich.Zone.MapLocal(local);
        if (mapping.Count == 0)
        {
            return ParseOutcome.Invalid($"dateTime '{localText}' does not exist in {ZurichTime.ZoneId} (DST gap)");
        }
        if (mapping.EarlyInterval.WallOffset != offset && mapping.LateInterval.WallOffset != offset)
        {
            return ParseOutcome.Invalid(
                $"dstActive={(dstActive ? "true" : "false")} implies {offset}, which tzdb does not allow for '{localText}' in {ZurichTime.ZoneId}");
        }

        var instant = local.WithOffset(offset).ToInstant();

        var year = instant.InUtc().Year;
        if (year is < MinYear or >= MaxYearExclusive)
        {
            return ParseOutcome.Invalid($"UTC year {year} is outside [{MinYear}, {MaxYearExclusive})");
        }

        return ParseOutcome.Ok(instant, offset);
    }

    private static ParseOutcome Missing(string field) => ParseOutcome.Invalid(ProviderJson.Missing(field));

    /// <returns>null if <paramref name="field"/> is an integer equal to <paramref name="expected"/>, else the error.</returns>
    private static string? Check(JsonElement root, string field, int expected)
    {
        if (!ProviderJson.TryGetInt32(root, field, out var actual)) return ProviderJson.Missing(field);
        return actual == expected ? null : $"'{field}' is {actual} but dateTime says {expected}";
    }
}
