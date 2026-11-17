using System.Text.Json;
using ClockManager.Api.Application.Time;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// time.now (primary provider, D-006): <c>GET developer/api/timezone/Europe/Zurich</c>.
/// The body gives an explicit UTC instant plus several redundant fields; all of them must agree
/// (plans/01-trusted-clock.md §4.3). A doubtful body is rejected, never repaired.
/// </summary>
internal sealed class TimeNowSource : HttpTimeSource
{
    internal const string Path = "developer/api/timezone/Europe/Zurich";

    private static readonly Duration UnixTimeTolerance = Duration.FromSeconds(1);
    private static readonly OffsetPattern UtcOffsetPattern = OffsetPattern.CreateWithInvariantCulture("+HH:mm");
    private static readonly IReadOnlyCollection<string> Sensitive = ["client_ip"];

    private readonly ZurichTime _zurich;

    /// <summary>
    /// Fixed identity of this provider. It never comes from config: the <c>Primary</c>/<c>Secondary</c> slots only
    /// choose which provider plays which role, so the audit trail always names the provider that really answered.
    /// </summary>
    internal const string ProviderName = "time.now";

    public TimeNowSource(HttpClient http, IElapsedTimer timer, IOptions<TrustedClockOptions> options, ZurichTime zurich)
        : base(http, timer, options, ProviderName)
    {
        _zurich = zurich;
    }

    protected override string RequestPath => Path;

    protected override IReadOnlyCollection<string> SensitiveFields => Sensitive;

    protected override ParseOutcome Parse(string body)
    {
        // A JSON object with distinct keys (a duplicated field has no single meaning, QA-07).
        using var document = ProviderJson.ParseObject(body, out var jsonError);
        if (document is null) return ParseOutcome.Invalid(jsonError!);

        var root = document.RootElement;

        if (!TryGetString(root, "timezone", out var zone)) return Missing("timezone");
        if (!string.Equals(zone, ZurichTime.ZoneId, StringComparison.Ordinal))
        {
            return ParseOutcome.Invalid($"timezone is '{zone}', expected '{ZurichTime.ZoneId}'");
        }

        // The instant: only a strict ISO 8601 UTC value ending in 'Z' is accepted.
        if (!TryGetString(root, "utc_datetime", out var utcText)) return Missing("utc_datetime");
        var utc = InstantPattern.ExtendedIso.Parse(utcText);
        if (!utc.Success) return ParseOutcome.Invalid($"utc_datetime '{utcText}' is not ISO 8601 UTC");
        var instant = utc.Value;

        var year = instant.InUtc().Year;
        if (year is < MinYear or >= MaxYearExclusive)
        {
            return ParseOutcome.Invalid($"year {year} is outside [{MinYear}, {MaxYearExclusive})");
        }

        if (!ProviderJson.TryGetInt64(root, "unixtime", out var unixSeconds)) return Missing("unixtime");
        if (unixSeconds is < -62_135_596_800 or > 253_402_300_799) // keeps FromUnixTimeSeconds in range
        {
            return ParseOutcome.Invalid($"unixtime {unixSeconds} is out of range");
        }
        var unixDelta = instant - Instant.FromUnixTimeSeconds(unixSeconds);
        if (Duration.Max(unixDelta, -unixDelta) > UnixTimeTolerance)
        {
            return ParseOutcome.Invalid($"unixtime {unixSeconds} disagrees with utc_datetime '{utcText}'");
        }

        // The offset must be what tzdb says for Zurich at that instant: we check, we don't adopt.
        var expectedOffset = _zurich.OffsetAt(instant);
        if (!TryGetString(root, "utc_offset", out var offsetText)) return Missing("utc_offset");
        var offset = UtcOffsetPattern.Parse(offsetText);
        if (!offset.Success) return ParseOutcome.Invalid($"utc_offset '{offsetText}' is not +HH:mm");
        if (offset.Value != expectedOffset)
        {
            return ParseOutcome.Invalid($"utc_offset {offsetText} does not match tzdb ({expectedOffset}) at {utcText}");
        }

        // The local datetime must be exactly the instant shown with that offset.
        if (!TryGetString(root, "datetime", out var localText)) return Missing("datetime");
        var local = OffsetDateTimePattern.ExtendedIso.Parse(localText);
        if (!local.Success) return ParseOutcome.Invalid($"datetime '{localText}' is not ISO 8601 with an offset");
        if (local.Value.Offset != expectedOffset || local.Value.ToInstant() != instant)
        {
            return ParseOutcome.Invalid($"datetime '{localText}' is not utc_datetime '{utcText}' at offset {expectedOffset}");
        }

        return ParseOutcome.Ok(instant, expectedOffset);
    }

    private static ParseOutcome Missing(string field) => ParseOutcome.Invalid(ProviderJson.Missing(field));

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        var found = ProviderJson.TryGetString(root, name, out var text);
        value = text ?? "";
        return found;
    }
}
