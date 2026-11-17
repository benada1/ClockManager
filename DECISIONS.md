# DECISIONS

Main decisions and why they were made, edge cases and how each is handled, what was deliberately
left out, and known limitations. Entries are added in the same change as the code they describe.

## Decisions

### D-001 — Ban local-clock APIs at compile time, plus a regex guard
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** tooling
- **Context:** CLAUDE.md rule 1 forbids the local clock for punch records. A rule that only lives in a document gets broken.
- **Options:** (a) code review only · (b) regex scan in `verify.ps1` · (c) Roslyn `BannedApiAnalyzers` (RS0030 as error) · (d) a unit test scanning source text.
- **Decision:** (c) for C# via `backend/Directory.Build.props` + `BannedSymbols.txt`, **and** keep (b) as a second layer that also covers the frontend (`new Date()`, `Date.now()`). CI runs both.
- **Why:** The analyzer works on symbols, not text, so it also catches `using static System.DateTime; Now`, aliases and qualified names that a regex misses. It fails the build in the IDE, so the feedback is immediate.
- **Trade-offs:** Package 3.3.4 is pinned because newer majors need a newer Roslyn than the .NET 8 SDK ships. A legitimate non-record use needs two markers (`#pragma` + `clock-ok:`). Test projects are exempt.
- **Refs:** `backend/Directory.Build.props` · `.claude/skills/verify/verify.ps1` · `.github/workflows/ci.yml` (job `clock-guard`)

### D-002 — Plain xUnit `Assert` instead of FluentAssertions
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** tests
- **Context:** The master plan listed FluentAssertions. Since v8 it needs a paid commercial license.
- **Options:** (a) FluentAssertions 8 (commercial) · (b) pin FluentAssertions 7.x (frozen, no updates) · (c) Shouldly · (d) plain xUnit `Assert`.
- **Decision:** (d).
- **Why:** No license question, no extra dependency, and xUnit's messages are good enough for these tests.
- **Trade-offs:** Assertions on collections and JSON are a bit more verbose.
- **Refs:** `backend/ClockManager.Tests/ClockManager.Tests.csproj`

### D-003 — Errors: typed `AppException` + one `IExceptionHandler`, mapped to ProblemDetails
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** Every error must be RFC 7807 with a stable `code` (master plan D9), including 404/405 and model-validation 400s.
- **Options:** (a) `Result<T>` from services, mapped in controllers · (b) services throw `AppException(code, status, title, detail)`, and one `IExceptionHandler` maps it · (c) ad-hoc `Problem(...)` in controllers.
- **Decision:** (b). A `CustomizeProblemDetails` hook always adds `traceId` and derives `code` from the status when there isn't one (`ErrorCodes.ForStatus`). `UseStatusCodePages()` covers empty-body errors.
- **Why:** Controllers stay thin, and one place guarantees the response shape. Rule violations are rare, so the cost of exceptions doesn't matter.
- **Trade-offs:** Expected failures are less explicit in method signatures than with Result types.
- **Refs:** `Common/Errors/` · `ProblemDetailsTests`

### D-004 — Unhandled exceptions never leak details, in any environment
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** A 500 could expose SQL, stack traces or provider details.
- **Options:** (a) include exception details in Development · (b) always generic.
- **Decision:** (b). The response is 500 `server.error` with a generic title, detail and `traceId`. The exception is logged at Error. A request cancelled by the client writes no body.
- **Why:** The same behaviour everywhere is easy to test. `traceId` links the response to the full log entry.
- **Trade-offs:** Developers read the logs instead of the response body.
- **Refs:** `ProblemDetailsTests.Unhandled_exception_returns_500_server_error_without_leaking_the_message`

### D-005 — Shift-rule options validated on startup
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** A bad `MaxOpenShiftHours` would silently change which shifts get flagged.
- **Options:** (a) validate on first use · (b) `ValidateDataAnnotations().ValidateOnStart()`.
- **Decision:** (b). `MaxOpenShiftHours` must be 1–48 (default 16) and `ShortShiftSeconds` 1–3600 (default 60). Otherwise the host refuses to start.
- **Why:** Bad config is caught at deploy time, not during someone's punch.
- **Trade-offs:** A config typo takes the whole API down instead of one feature.
- **Refs:** `OptionsValidationTests`

### D-006 — Time providers: time.now primary, timeapi.io secondary
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** Rule 1 requires an external Europe/Zurich time for every punch. We need a provider that gives an unambiguous instant, needs no secret, and has a fallback.
- **Options:** (a) worldtimeapi.org (master plan's candidate) · (b) time.now · (c) timeapi.io · (d) timezonedb.com.
- **Decision:** (b) primary, (c) secondary (client added in plan 01 step 2). (a) rejected: connection failed on 4 tries over 2026-11-17 (https and http). (d) rejected: needs an API key, so a secret to manage for no gain.
- **Why:** time.now sends `utc_datetime` ending in `Z` plus redundant `unixtime`, `datetime` with offset and `utc_offset`, so we can cross-validate one response without guessing. timeapi.io sends a local time without an offset; it's still useful as an independent second source.
- **Trade-offs:** Both are free services with no SLA and no published rate limits. Strict validation means a format change on their side reads as an outage until we adapt (the opt-in live test `TimeNowLiveTests` catches it).
- **Refs:** plans/01-trusted-clock.md §3, §4.3 · `TimeNowSourceTests` · fixtures in `backend/ClockManager.Tests/TestData/TimeNow/`

### D-007 — NodaTime (bundled tzdb) instead of `TimeZoneInfo`
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** Validating provider offsets, DST transitions and Zurich-day grouping all depend on time-zone rules. Plan 01 Q3.
- **Options:** (a) .NET `TimeZoneInfo` (OS data: Windows registry on the dev machine, ICU/tzdata on Linux CI) · (b) NodaTime 3.3.7 with its embedded tzdb.
- **Decision:** (b). `Europe/Zurich` is resolved once into a singleton `ZurichTime` (`OffsetAt`, `ToLocal`, `LocalDate`). `NodaTime.SystemClock` is added to `BannedSymbols.txt`, so NodaTime's own "now" fails the build (RS0030).
- **Why:** The same rules on every OS and in CI, independent of OS updates. `Instant` / `LocalDateTime` / `ZonedDateTime` make "is this UTC or local?" a compile-time question.
- **Trade-offs:** One more dependency; tzdb updates arrive with NodaTime releases (Zurich rules are stable). EF Core mapping of NodaTime types is a Phase 2 question for the db agent.
- **Refs:** `Application/Time/ZurichTime.cs` · `ZurichTimeTests` · `backend/BannedSymbols.txt`

### D-010 — Provider timeout 3 s, low-precision flag above 1.5 s
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** A punch waits on the provider. Measured on 2026-11-17 (~15 samples each): typically 0.33–0.42 s, spikes 0.5–1.1 s, and one burst where **both** providers took ~4 s at the same time.
- **Options:** (a) 2 s (master plan guess) · (b) 3 s · (c) 5 s+.
- **Decision:** (b) `TrustedClock:TimeoutMs = 3000` (allowed 500–10000), `LowPrecisionThresholdMs = 1500` (must be < TimeoutMs, checked on startup). The timeout is a linked `CancellationTokenSource` per request covering headers and body; `HttpClient.Timeout` (timeout + 1 s) is only a backstop. The round trip is measured with a monotonic `IElapsedTimer` (Stopwatch), never the wall clock.
- **Why:** 3 s covers every spike seen except the 4 s burst, which no sensible timeout would save; 2 s would have cut real 1.1 s+ tails close. 1.5 s marks readings whose uncertainty is worth recording.
- **Trade-offs:** In the worst case a punch waits ~3 s before a 503. During a ~4 s burst like the one observed, punches fail closed.
- **Refs:** plans/01-trusted-clock.md §3, §4.5 · `TrustedClockOptionsTests` · `TimeNowSourceTests.Slow_provider_is_reported_as_timeout_not_thrown`

## Edge cases

| Scenario | Handling | Covered by |
|---|---|---|
| time.now body missing `timezone`/`utc_datetime`/`unixtime`/`utc_offset`/`datetime`, or wrong JSON type (E7) | Provider result = `invalid_response`; nothing is filled in | `TimeNowSourceTests.Rejects_missing_field_E7`, `Rejects_field_with_wrong_type_E7` |
| Wrong zone in the response, e.g. `Europe/London`, `Europe/Berlin`, `europe/zurich` (E8) | `invalid_response`; exact match on `Europe/Zurich` only | `TimeNowSourceTests.Rejects_wrong_zone_E8` |
| `utc_offset` ≠ tzdb offset at that instant, e.g. `+02:00` in January (E9) | `invalid_response`; we check the provider's offset, never adopt it | `TimeNowSourceTests.Rejects_offset_not_matching_tzdb_E9` |
| `utc_datetime` without `Z` / culture-formatted | `invalid_response`; only strict ISO 8601 UTC is read, never an assumed offset | `TimeNowSourceTests.Rejects_utc_datetime_that_is_not_strict_iso_utc` |
| `unixtime` disagrees with `utc_datetime` by > 1 s | `invalid_response` | `TimeNowSourceTests.Rejects_unixtime_mismatch` |
| `datetime` ≠ instant shown at the tzdb offset | `invalid_response` | `TimeNowSourceTests.Rejects_datetime_not_equal_to_instant_plus_offset` |
| Punch exactly at DST start 2026-03-29T01:00Z (E12, T7) | Accepted at +02:00, local 03:00; a body claiming 02:00+01:00 is rejected | `TimeNowSourceTests.Dst_start_instant_maps_to_0300_E12` |
| Repeated October hour (T6) from time.now | Instant + offset make both 02:30 (+02) and 02:15 (+01) unambiguous | `TimeNowSourceTests.Accepts_both_sides_of_the_repeated_hour_T6` |
| Implausible year, e.g. 1970 / 2150 / 2025 (E14) | `invalid_response`; fixed range [2026, 2100) on the UTC year, not compared with the server clock | `TimeNowSourceTests.Rejects_implausible_year_E14` |
| 200 with HTML, empty, non-object, non-UTF-8 or > 16 KB body | `invalid_response` | `TimeNowSourceTests.Rejects_html_error_page_with_status_200` and neighbours |
| Provider returns 404/5xx/other non-200, or 429 | `http_error` (with status) / `rate_limited` | `TimeNowSourceTests.Non_200_status_is_http_error`, `Status_429_is_rate_limited` |
| Provider slower than `TimeoutMs` (headers or body) | `timeout` result, not an exception | `TimeNowSourceTests.Slow_provider_is_reported_as_timeout_not_thrown`, `Body_that_never_finishes_is_a_timeout` |
| Caller cancels (client disconnects) during the provider call (E17, provider layer) | `OperationCanceledException` propagates; never reported as `timeout` | `TimeNowSourceTests.Propagates_caller_cancellation_E17` |
| `client_ip` (our server's public IP) in the time.now body (E18) | Removed before storing `RawPayload`; payload capped at 1024 chars | `TimeNowSourceTests.Sanitizes_client_ip_E18`, `Truncates_raw_payload_to_1_KB` |

## Not done (out of scope)

_None yet._

## Corrections to Claude's output

Where Claude Code's approach was wrong or had to be changed. Full story in `docs/claude-worklog.md`.
From Phase 1 on, each correction to committed code is its own `fix:` commit.

| Date | What Claude did | What was wrong | Fix | Commit |
|---|---|---|---|---|
| 2026-11-17 | Master plan listed phases without owners | The agents from CLAUDE.md §7 were never assigned to work | Added the "Who builds what" table | `fcd944e` |
| 2026-11-17 | Master plan picked FluentAssertions | v8+ needs a commercial license; not checked | Plain xUnit `Assert` (D-002), caught before any code | `6845736` |
| 2026-11-17 | Clock guard was a regex scan only | Misses `using static` tricks; only runs if someone runs `verify` | Compile-time analyzer, RS0030 as an error (D-001) | `890849b` |
| 2026-11-17 | Committed one file per tiny change, without showing each commit first | Too granular, and the user didn't review each commit before it landed | Commit convention in CLAUDE.md §6.1: one commit per task, user reviews every commit (later relaxed by the user for the deadline: autonomous commits, review at phase end, `59346cf`) | `7b60d51` |
| 2026-11-17 | `TimeNowSource` took its name from the `Primary` config slot | With the roles swapped, time.now readings would be credited to "timeapi.io" in the audit trail. Found by the qa agent (QA-03) while writing step-2 tests | Fixed name `TimeNowSource.ProviderName = "time.now"`; regression test `Name_is_always_time_now_even_when_it_plays_the_secondary_role_QA03` | this commit |

## Known limitations

- **Generic error codes come from the HTTP status.** Errors produced only by a status (`request.not_found`, `request.method_not_allowed`, ...) get a code mapped from the status. Specific codes come only from `AppException`.
