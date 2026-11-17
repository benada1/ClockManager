---
name: zurich-time-cases
description: Reference for Europe/Zurich time handling in ClockManager - verified DST transition instants, ambiguous/nonexistent local times, overnight and long shifts, time-provider failure modes, and a ready test matrix with exact UTC values. Use when writing or reviewing code or tests that touch punch times, shift durations, day/week grouping, time display, or the external time provider.
---

# zurich-time-cases

## Facts (verified with .NET `TimeZoneInfo` for `Europe/Zurich`)

- Standard time is **CET = UTC+01:00**. Summer time is **CEST = UTC+02:00**.
- DST starts on the **last Sunday of March at 01:00 UTC**. Local time jumps 02:00 → 03:00, so
  **02:00–02:59 does not exist** that day.
- DST ends on the **last Sunday of October at 01:00 UTC**. Local time falls back 03:00 → 02:00, so
  **02:00–02:59 happens twice** (first at +02:00, then at +01:00).

| Year | DST starts (UTC) | DST ends (UTC) |
|---|---|---|
| 2026 | 2026-03-29 01:00Z | 2026-10-25 01:00Z |
| 2027 | 2027-03-28 01:00Z | 2027-10-31 01:00Z |
| 2028 | 2028-03-26 01:00Z | 2028-10-29 01:00Z |

## Rules that follow from this

1. **Store instants, not wall-clock times.** Persist UTC (`datetimeoffset` or `datetime2` UTC) and,
   if useful, the offset seen at punch time. Never store a local `DateTime` with `Kind=Unspecified`.
2. **Duration = `ClockOutUtc - ClockInUtc`.** Never subtract local times.
3. **Grouping** (which day/week a shift belongs to) uses the Zurich local date of `ClockInUtc` by
   default. Weeks are ISO weeks (Monday start) in Zurich time. Record any other choice with
   `record-decision`.
4. **Display** always says or implies Zurich time. Around a DST change, show the offset
   (e.g. `02:30 (UTC+1)`) so that two "02:30" values can be told apart.
5. **User-entered local times** (e.g. a manager correction) must be resolved explicitly:
   - nonexistent (in March): reject with a clear message;
   - ambiguous (in October): require or pick the offset explicitly, and document the choice.
   Use `tz.IsInvalidTime(local)` / `tz.IsAmbiguousTime(local)`.

## .NET snippets

```csharp
// Works on .NET 6+ on Windows (ICU) and Linux. Resolve the zone once and inject it; do not look it up per call.
var zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

DateTimeOffset local = TimeZoneInfo.ConvertTime(utcInstant, zurich);     // for display/grouping
DateOnly workDate   = DateOnly.FromDateTime(local.DateTime);
TimeSpan duration   = clockOutUtc - clockInUtc;                          // always from UTC
```

Tests use a **fake trusted clock** that returns fixed instants. Never call a real provider in unit tests:
```csharp
var clock = new FakeTrustedClock(DateTimeOffset.Parse("2026-10-25T00:30:00Z"));
clock.Set(DateTimeOffset.Parse("2026-10-25T01:15:00Z"));   // move time forward between punches
clock.FailWith(new TimeUnavailableException("timeout"));   // simulate an outage
```

## Frontend

```js
const fmt = new Intl.DateTimeFormat('en-GB', {
  timeZone: 'Europe/Zurich', dateStyle: 'medium', timeStyle: 'short', timeZoneName: 'short',
});
fmt.format(new Date(serverIsoString)); // formatting a server value is fine; new Date() with no args is not
```

## Test matrix (copy the exact values)

| # | Scenario | Clock In (UTC) | Clock Out (UTC) | Expected |
|---|---|---|---|---|
| T1 | Normal day shift (winter) | 2026-01-12T07:00Z | 2026-01-12T15:30Z | 8h30m; local 08:00–16:30 (+01) |
| T2 | Normal day shift (summer) | 2026-07-06T06:00Z | 2026-07-06T14:00Z | 8h; local 08:00–16:00 (+02) |
| T3 | Overnight across midnight | 2026-01-12T21:00Z | 2026-01-13T05:00Z | 8h; belongs to 2026-01-12 (Zurich) |
| T4 | Across DST start | 2026-03-28T21:00Z | 2026-03-29T04:00Z | **7h**, although local shows 22:00–06:00 |
| T5 | Across DST end | 2026-10-24T20:00Z | 2026-10-25T05:00Z | **9h**, although local shows 22:00–06:00 |
| T6 | Inside the repeated hour | 2026-10-25T00:30Z | 2026-10-25T01:15Z | 45m; local 02:30 (+02) → 02:15 (+01). The wall clock looks backwards, but the shift is valid |
| T7 | Punch exactly at transition | 2026-03-29T01:00Z | — | local 03:00 (+02); never shown as 02:00 |
| T8 | Year boundary / ISO week | 2026-12-31T22:00Z | 2027-01-01T06:00Z | belongs to 2026-12-31; ISO week 2026-W53 |
| T9 | Forgotten Clock Out | 2026-01-12T07:00Z | still open at 2026-01-13T07:00Z | flagged as over the max length; **not** closed automatically with a made-up time |
| T10 | Clock Out ≤ Clock In (provider went backwards / skew) | 2026-01-12T07:00:00Z | 2026-01-12T06:59:58Z | rejected (409/422) and logged; nothing saved |
| T11 | Zero-length (double click on in/out) | 2026-01-12T07:00:00Z | 2026-01-12T07:00:00Z | rejected, or idempotent no-op, as decided in DECISIONS.md |
| T12 | Nonexistent local time in correction | local 2026-03-29 02:30 | — | rejected: "time does not exist in Zurich (DST)" |
| T13 | Ambiguous local time in correction | local 2026-10-25 02:30 | — | needs an explicit offset; never guessed silently |

## Time-provider failure modes (each needs a test with a stubbed `HttpMessageHandler`)

- Timeout / slow response (longer than the configured timeout) → fail closed with 503 `time.unavailable`.
- HTTP 5xx, 429 (rate limited), DNS failure → try the secondary provider if there is one, then 503.
- 200 with an unexpected body: missing fields, non-JSON, HTML error page → treated as unavailable.
- Wrong time zone in the response (not `Europe/Zurich`) or a local time **without an offset** → rejected.
  Never assume an offset.
- Time very different from the previous punch or the other provider (e.g. > 2 min skew) → reject or flag,
  per DECISIONS.md.
- Response parsing must be culture-independent (`CultureInfo.InvariantCulture`, ISO 8601).
- Never cache the "current time" in a way that gives two punches the same old value. If caching or
  offset-tracking is used, document the method and its error bound in DECISIONS.md.
