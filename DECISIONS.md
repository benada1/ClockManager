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
- **Decision:** (b) primary, (c) secondary (`TimeApiIoSource`, plan 01 step 2). The config's `Primary.Name` / `Secondary.Name` pick which built-in client plays which role by exact name; an unknown name stops the host on startup. (a) rejected: connection failed on 4 tries over 2026-11-17 (https and http). (d) rejected: needs an API key, so a secret to manage for no gain.
- **Why:** time.now sends `utc_datetime` ending in `Z` plus redundant `unixtime`, `datetime` with offset and `utc_offset`, so we can cross-validate one response without guessing. timeapi.io sends a local time without an offset; it's still useful as an independent second source.
- **Trade-offs:** Both are free services with no SLA and no published rate limits. Strict validation means a format change on their side reads as an outage until we adapt (the opt-in live test `TimeNowLiveTests` catches it).
- **Refs:** plans/01-trusted-clock.md §3, §4.3 · `TimeNowSourceTests` · `TimeApiIoSourceTests` · `TrustedClockRegistrationTests` · fixtures in `backend/ClockManager.Tests/TestData/`

### D-007 — NodaTime (bundled tzdb) instead of `TimeZoneInfo`
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** Validating provider offsets, DST transitions and Zurich-day grouping all depend on time-zone rules. Plan 01 Q3.
- **Options:** (a) .NET `TimeZoneInfo` (OS data: Windows registry on the dev machine, ICU/tzdata on Linux CI) · (b) NodaTime 3.3.7 with its embedded tzdb.
- **Decision:** (b). `Europe/Zurich` is resolved once into a singleton `ZurichTime` (`OffsetAt`, `ToLocal`, `LocalDate`). `NodaTime.SystemClock` is added to `BannedSymbols.txt`, so NodaTime's own "now" fails the build (RS0030).
- **Why:** The same rules on every OS and in CI, independent of OS updates. `Instant` / `LocalDateTime` / `ZonedDateTime` make "is this UTC or local?" a compile-time question.
- **Trade-offs:** One more dependency; tzdb updates arrive with NodaTime releases (Zurich rules are stable). EF Core mapping of NodaTime types is a Phase 2 question for the db agent.
- **Refs:** `Application/Time/ZurichTime.cs` · `ZurichTimeTests` · `backend/BannedSymbols.txt`

### D-008 — Both providers on every punch, in parallel, cross-checked at the round-trip midpoints
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** A provider can be up but wrong. Plan 01 Q1. The two requests never land at the same moment, and a slow answer is less precise.
- **Options:** (a) secondary only when the primary fails · (b) both on every punch, sequentially · (c) both on every punch, in parallel.
- **Decision:** (c). Both fetches start before either is awaited. The primary's value is used, never an average. Delta = `(B.Instant − A.Instant) − ((B.Started + B.Rtt/2) − (A.Started + A.Rtt/2))`; monotonic timestamps are converted only through `IElapsedTimer` (QA-01: Stopwatch ticks are 1 ns on Linux, 100 ns on Windows). Agree if `|delta|` rounded to whole ms is `≤ CrossCheckToleranceMs` (2000, inclusive) **or** `< (RttA + RttB) / 2` (QA-04); the same rounded value is compared and reported as `CrossCheckDeltaMs` (QA-05). Disagree → 503 `time.providers_disagree`.
- **Why:** (a) never catches a wrong-but-alive provider. (c) costs the slower call (~0.4 s typical), not the sum. Each reading was taken somewhere inside its own round trip, so two correct clocks can look up to `(RttA + RttB) / 2` apart; with two ~2.8 s answers a fixed 2 s tolerance would reject correct clocks. Reaching the bound exactly needs zero network delay one way, so the bound is exclusive.
- **Trade-offs:** Two outbound calls per punch. With very slow answers (near the 3 s timeout) the effective tolerance grows to ~3 s; those readings are already flagged `LowPrecision`.
- **Refs:** plans/01-trusted-clock.md §4.2 · `TrustedClockTests` (midpoint, tolerance, parallel) · `TrustedClockFindingsTests` (QA-04, QA-05)

### D-009 — Single-source punches are accepted, marked `CrossCheckedBy = null`
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** Plan 01 Q2. When only one provider gives a valid answer, we can't cross-check.
- **Options:** (a) refuse the punch (503) · (b) accept it and record that it wasn't cross-checked.
- **Decision:** (b). Primary failed → secondary's value; secondary failed → primary's value; both with `CrossCheckedBy = null`, `CrossCheckDeltaMs = null`, logged at Warning. A source that **throws** (anything but the caller's cancellation) counts as that source failing with outcome `error` and is logged at Error with the exception (QA-06). Both failed → 503 `time.invalid_response` if either sent a bad body, else `time.unavailable`.
- **Why:** (a) doubles the chance of a 503 with two free providers. A single source is still an external Europe/Zurich time, which is what rule 1 requires, and the stored `CrossCheckedBy = null` keeps it visible in the audit trail.
- **Trade-offs:** A single provider that is wrong while the other is down goes unnoticed for that punch.
- **Refs:** plans/01-trusted-clock.md §4.2, §8 · `TrustedClockTests.Falls_back_to_secondary_when_primary_fails_E3_E4`, `Accepts_primary_single_source_when_secondary_fails` · `TrustedClockFindingsTests` (QA-06)

### D-010 — Provider timeout 3 s, low-precision flag above 1.5 s
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** A punch waits on the provider. Measured on 2026-11-17 (~15 samples each): typically 0.33–0.42 s, spikes 0.5–1.1 s, and one burst where **both** providers took ~4 s at the same time.
- **Options:** (a) 2 s (master plan guess) · (b) 3 s · (c) 5 s+.
- **Decision:** (b) `TrustedClock:TimeoutMs = 3000` (allowed 500–10000), `LowPrecisionThresholdMs = 1500` (must be < TimeoutMs, checked on startup). The timeout is a linked `CancellationTokenSource` per request covering headers and body; `HttpClient.Timeout` (timeout + 1 s) is only a backstop. The round trip is measured with a monotonic `IElapsedTimer` (Stopwatch), never the wall clock.
- **Why:** 3 s covers every spike seen except the 4 s burst, which no sensible timeout would save; 2 s would have cut real 1.1 s+ tails close. 1.5 s marks readings whose uncertainty is worth recording.
- **Trade-offs:** In the worst case a punch waits ~3 s before a 503. During a ~4 s burst like the one observed, punches fail closed.
- **Refs:** plans/01-trusted-clock.md §3, §4.5 · `TrustedClockOptionsTests` · `TimeNowSourceTests.Slow_provider_is_reported_as_timeout_not_thrown`

### D-011 — timeapi.io: derive the offset from `dstActive`, then require tzdb to allow it
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** timeapi.io sends `dateTime` without an offset (`2026-11-17T14:18:43.8658902`). In the repeated October hour, `02:30` is two different instants.
- **Options:** (a) resolve with tzdb alone (pick the earlier or later mapping) · (b) take the offset from `dstActive` (`true` → +02:00, `false` → +01:00) and require it to be one of tzdb's valid offsets for that local time (`MapLocal`) · (c) drop timeapi.io.
- **Decision:** (b). No valid offset (the March gap) or an offset tzdb doesn't allow (e.g. `dstActive=true` in January, or `03:00 +01` on 2026-03-29) → `invalid_response`. Also required and checked: `timeZone` exactly `Europe/Zurich`; `dateTime` with seconds, 0–7 fraction digits, no offset/`Z`, invariant culture; `year…milliSeconds` equal to `dateTime`'s parts, where **`milliSeconds` is the truncated fraction** (the real provider sends 865 for `.8658902`, so it truncates; a rounded 866 means the fields don't describe the same time); UTC year in [2026, 2100).
- **Why:** (a) would guess in the repeated hour, exactly what plan §4.3 forbids. The provider's own flag disambiguates; tzdb independently checks it. (c) would leave no cross-check.
- **Trade-offs:** If timeapi.io ever changes the shape (offset added, milliseconds rounded), it reads as invalid until we adapt; time.now keeps serving (D-009).
- **Refs:** plans/01-trusted-clock.md §4.3 · `TimeApiIoSourceTests` (`Resolves_repeated_hour_with_dst_flag_E11`, `Applies_tzdb_at_the_dst_boundaries`, `Rejects_milliSeconds_that_is_not_the_truncated_fraction`)

### D-012 — `/health/time`: 10 s cache on a monotonic timer, single-flight, never on the punch path
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** `GET /health/time` is public and unauthenticated (until Phase 4) and will be polled by the UI banner (Phase 5). Every uncached call costs two outbound requests to free providers with unpublished rate limits.
- **Options:** (a) no cache · (b) `IMemoryCache` with absolute expiration (reads the local clock, needs a `clock-ok` exemption) · (c) own cache, freshness measured on the monotonic `IElapsedTimer` (Stopwatch).
- **Decision:** (c). The singleton `TimeHealthCheck` keeps the last report for 10 s, counted from when the answer **arrived**; ok and failed reports alike. Concurrent callers during a refresh share one in-flight run (no stampede). The refresh runs on the app's stopping token, so one disconnecting caller can't cancel it for the others. `ITrustedClock.GetNowAsync` never reads this cache: punches always ask both providers. 200 when a punch would succeed, else 503, same body shape; only outcome codes, RTTs, delta and the `time.*` code: never `Detail`, raw bodies or provider times.
- **Why:** (a) lets any client multiply provider traffic and risks 429s that would then hit real punches. (c) needs no banned API at all (nothing to exempt from the clock guard) and is testable with the fake timer. Caching failures matters most: during an outage, polling must not multiply calls.
- **Trade-offs:** The report can be up to 10 s (+ one round trip) old, so a recovered provider shows as down for up to 10 s, and vice versa. A punch can fail with 503 while the health check still says ok (or the reverse); the punch's own answer is the authority.
- **Refs:** plans/01-trusted-clock.md §4.6, §4.7 · `Infrastructure/Time/TimeHealthCheck.cs` · `TimeHealthCheckTests` · `TimeHealthEndpointTests`

### D-013 — Development-only Time lab (`GET /api/dev/time-lab`), allowed to show provider details
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** The developer asked to see the providers live: what each one sent, how long it took, whether they agreed and what a punch would get (plan 01 step 3b). `/health/time` deliberately hides all of that.
- **Options:** (a) read the logs only · (b) put the details into `/health/time` · (c) a separate diagnostic endpoint, mapped only in Development.
- **Decision:** (c). One uncached run of the real orchestrator (`TrustedClock.RunAsync`) per request; always 200, also when the decision is a failure. Per provider: role, outcome, RTT, HTTP status, `detail`, sanitized raw payload, and UTC / Zurich local / offset **computed from the instant with tzdb**, not copied from the provider. Plus the cross-check numbers and the decision (accepted provider or the 503 code). Outside Development the controller is removed from MVC discovery (`[DevelopmentOnly]` + an `IApplicationFeatureProvider`) and `ITimeLab` is not registered: the route is a 404 and absent from Swagger.
- **Why:** (a) is slow to read and doesn't show the decision as one picture. (b) would expose internal error text and payloads on a public endpoint. On a developer's machine, `detail` (e.g. `HTTP 429`, `no complete response within 3000 ms`) is exactly what is useful, and the payload is already sanitized (no `client_ip`).
- **Trade-offs:** Each click is two real outbound calls (uncached by design); fine for one developer. A misconfigured `ASPNETCORE_ENVIRONMENT=Development` in production would expose it; the same is true of Swagger.
- **Refs:** plans/01-trusted-clock.md §6 step 3b · `Controllers/Dev/TimeLabController.cs` · `Infrastructure/Time/TimeLabMapper.cs` · `Common/DevelopmentOnly.cs` · `TimeLabTests`

### D-014 — Hash routing instead of a router library
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** frontend
- **Context:** The app needs a few screens (home, Time lab, later punch / history / manager).
- **Options:** (a) react-router · (b) hash routing with `useSyncExternalStore` on `hashchange` · (c) History API paths.
- **Decision:** (b), ~15 lines in `frontend/src/router.js`.
- **Why:** No dependency, and deep links work without a server-side fallback route.
- **Trade-offs:** URLs contain `#/`; no nested routes or loaders. Easy to swap for react-router if the app grows.
- **Refs:** `frontend/src/router.js` · `frontend/src/App.jsx`

### D-015 — Busy buttons use `aria-disabled` + an in-flight guard, not `disabled`
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** frontend
- **Context:** Buttons that start a request must not double-submit (Time lab now, the punch button in Phase 5).
- **Options:** (a) the `disabled` attribute · (b) `aria-disabled="true"` + a ref guard that ignores clicks while busy.
- **Decision:** (b), with a visible busy style.
- **Why:** A `disabled` button loses keyboard focus in most browsers, so keyboard and screen-reader users are dropped onto `<body>` when the request starts. (b) keeps focus and still blocks double clicks, even two in the same tick.
- **Trade-offs:** Every click handler must check the guard; the server-side idempotency (Phase 3) is still the real protection.
- **Refs:** `frontend/src/pages/TimeLab.jsx`

### D-016 — The UI shows Zurich time from server strings, never from the browser clock or zone
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** frontend
- **Context:** CLAUDE.md rule 1 and the repeated October hour (T6).
- **Options:** (a) parse into `Date` and format in the browser's zone · (b) format server ISO instants with `Intl.DateTimeFormat({ timeZone: 'Europe/Zurich' })` · (c) show the server's `zurichLocal` + `zurichOffset` text.
- **Decision:** (c) for the Time lab (fractions cut to milliseconds, CET/CEST added for +01:00/+02:00); (b) remains the plan for ordinary screens. Never (a).
- **Why:** The browser's clock and zone play no part, and the offset is always visible, so the two "02:30" of October can be told apart.
- **Trade-offs:** Depends on the server sending both strings; the punch DTOs (Phase 3) should keep them.
- **Refs:** `frontend/src/lib/serverTime.js` · `frontend/src/pages/time-lab/ZurichTime.jsx`

### D-017 — Dev-only mock API and an explicit "backend unreachable" marker in the Vite proxy
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** frontend
- **Context:** UI error states (provider down, disagreement, API not running) must be checkable without breaking real providers.
- **Options:** (a) test only against the real backend · (b) a Vite plugin serving fixed scenarios, only in `npm run dev:mock`.
- **Decision:** (b), scenario chosen with `?mock=<name>` (accepted, single, lowprecision, dst, disagree, down, invalid, notdev, servererror, unreachable). The `/api` proxy answers a down backend with 502 + `X-Dev-Proxy-Error: backend-unreachable`, which the client turns into a `NetworkError`.
- **Why:** Vite's bare 502 looks like a server error; every state can be shown on demand.
- **Trade-offs:** The mock fixtures can drift from the contract (`TimeLabResponse` is the source of truth). Never part of the production bundle.
- **Refs:** `frontend/dev/mockApi.js` · `frontend/vite.config.js`

### D-018 — Startup-validation tests read the host's startup log, not only what `CreateClient()` throws
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** tests
- **Context:** The `Host_fails_to_start_*` tests flaked under CPU load (23 of 24 runs failed under stress, 1 failure in normal use). With minimal hosting, `ValidateOnStart` throws on the `Program` thread and `app.Run()` disposes the host. If `WebApplicationFactory`'s `DeferredHost.StartAsync` runs after that, it throws `ObjectDisposedException` instead of the validation error. Production validation was always correct; only the tests could miss it.
- **Options:** (a) retry · (b) turn off xUnit parallelism · (c) validate options on a plain `ServiceCollection` · (d) also capture the host's "Hosting failed to start" log.
- **Decision:** (d), in a shared helper `HostStartup.FindStartupValidationFailure`.
- **Why:** The test stays at the host level, so it still proves `ValidateOnStart` is wired, and the race is gone (21 stress runs clean).
- **Trade-offs:** Relies on the generic host logging the startup failure, which it always has.
- **Refs:** `backend/ClockManager.Tests/Infrastructure/HostStartup.cs`

### D-019 — SQL Server Express as the dev database (deviation from CLAUDE.md's LocalDB)
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** db
- **Context:** LocalDB isn't installed on the dev machine; SQL Server 2025 Express runs as `.\SQLEXPRESS` (Windows auth, sysadmin); no Docker. Plan 02 Q1.
- **Options:** (a) install LocalDB · (b) use the existing SQL Express · (c) Docker SQL Server.
- **Decision:** (b). `appsettings.Development.json` holds a Windows-auth connection string (no password, allowed by rule 3). README documents LocalDB and Docker via user-secrets.
- **Why:** Works now with no install; same engine family as CI's SQL Server 2022 container.
- **Trade-offs:** Another instance name needs a user-secret override; 2025 locally vs 2022 in CI (only long-standing features used; CI is the referee).
- **Refs:** plans/02-schema.md §3, §5 · README "Database setup"

### D-020 — Punch evidence as owned `ClockIn_*` / `ClockOut_*` columns on `Shifts`, not a `Punches` table
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** db
- **Options:** (a) EF owned type `PunchStamp` mapped twice onto `Shifts` · (b) a `Punches` table, one row per punch · (c) EF 8 complex types.
- **Decision:** (a). ClockOut is an optional owned type: all 9 columns NULL = open shift. Plan 02 Q3.
- **Why:** "One open shift" (filtered unique index) and "out after in" (CHECK) stay plain single-row SQL constraints; (b) would need triggers or app logic; (c) can't be optional in EF 8.
- **Trade-offs:** A wide row (21 columns). EF can't model an index mixing owner and owned columns, so `IX_Shifts_EmployeeId_ClockIn_ZurichDate` is raw SQL in the migration, guarded by `ShiftSchemaTests`.
- **Refs:** `Infrastructure/Persistence/Configurations/ShiftConfiguration.cs` · `Migrations/*_InitialSchema.cs`

### D-021 — Instants as `datetimeoffset(7)` at +00:00, plus the Zurich offset and work date stored as seen
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** db
- **Options:** (a) `datetime2(7)` UTC · (b) `datetimeoffset` holding the local Zurich offset · (c) `datetimeoffset` at +00:00 with offset (smallint minutes) and date in their own columns.
- **Decision:** (c), enforced in SQL: `*UtcOffsetZero`, offset `IN (60,120)`, and `ZurichDate = CAST(SWITCHOFFSET(Utc, offset) AS date)` (catches a date computed in the server's zone). Precision 7: timeapi.io sends 100 ns.
- **Why:** SQL compares `datetimeoffset` by instant; nobody reads a wall-clock time as the truth; stored offset and date keep reports stable if tz rules change.
- **Trade-offs:** Sub-100 ns precision truncated (no provider sends it). No DB defaults on any time column.
- **Refs:** `PunchRoundTripTests` (S9) · `NodaTimeConverters.cs`

### D-022 — Three own NodaTime value converters instead of a NodaTime EF package
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** db
- **Decision:** Instant ↔ DateTimeOffset (UTC), Offset ↔ smallint minutes, LocalDate ↔ DateOnly, via `ConfigureConventions`, instead of `SimplerSoftware.EntityFrameworkCore.SqlServer.NodaTime`.
- **Why:** ~20 lines, no third-party dependency to keep in step with EF, plain SQL types.
- **Trade-offs:** No server-side NodaTime methods in LINQ; Phase 6 filters on the stored `ClockIn_ZurichDate` or instant ranges.

### D-023 — Migrate and seed on startup only in Development; fail fast without a connection string
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** db
- **Options:** (a) migrate on startup everywhere · (b) never · (c) Development only, behind `Database:MigrateAndSeedOnStartup`.
- **Decision:** (c). Production never migrates itself (`dotnet ef database update` or a bundle). A missing `ConnectionStrings:ClockManager` stops startup with a clear message. Seed: 1 manager + 3 employees, idempotent, **no shifts** (a seeded shift would be a record with a made-up time).
- **Why:** Clone-and-run in dev without racing schema changes in production; test hosts turn the flag off and never touch the dev DB.
- **Trade-offs:** No demo shifts until real punches exist (Phase 3).
- **Refs:** `PersistenceExtensions.cs` · `DevelopmentSeederTests` (S11) · `NoDatabaseHostSettings.cs`

### D-024 — Integration tests: one throwaway database per test class, built by the real migrations
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** tests
- **Options:** (a) one shared test DB · (b) a DB per test · (c) a DB per test class · (d) Testcontainers (no Docker locally).
- **Decision:** (c): `ClockManager_Test_<guid>` via `MigrateAsync`, dropped on dispose; stale ones (> 1 h) dropped at run start. Server from `CLOCKMANAGER_TEST_SQL` (default `.\SQLEXPRESS`). Unreachable → skipped locally with a reason, **failed** when `CI=true`.
- **Why:** Isolation without a migration per test (~1–2 s per class), and the migrations themselves are tested.
- **Trade-offs:** Tests in one class share a DB, so each creates its own employees.
- **Refs:** `MigratedDatabaseFixture.cs` · `TestSqlServer.cs`

### D-025 — `IdempotencyRecord` and `ShiftCorrection` tables move to Phases 3 and 7
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** db
- **Decision:** Not in `InitialSchema`; each is designed with the feature that uses it and arrives in its own migration (plan 02 Q2). `PasswordHash` comes with Phase 4.

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
| Both providers answer and agree (E1) | Primary's value, `CrossCheckedBy` = secondary, `CrossCheckDeltaMs` = midpoint-corrected delta | `TrustedClockTests.Accepts_primary_when_both_agree_E1` |
| Providers disagree, e.g. 5 s apart (E2) | 503 `time.providers_disagree`, both outcomes `ok`; tolerance 2000 ms inclusive | `TrustedClockTests.Fails_closed_when_providers_disagree_E2`, `Tolerance_is_inclusive_at_2000_ms` |
| One provider times out / 5xx / 429 / bad body (E3, E4) | The other provider's value, `CrossCheckedBy = null`; no retry (D-009) | `TrustedClockTests.Falls_back_to_secondary_when_primary_fails_E3_E4` |
| Both fail (E5, E6) | 503 `time.invalid_response` if either sent a bad body, else `time.unavailable`; `providers` lists short codes in role order | `TrustedClockTests.Fails_closed_when_both_fail_E5_E6` |
| Slow answer within the timeout, e.g. 1.8 s (E15) | Accepted, `LowPrecision = true` (RTT > 1500 ms) | `TrustedClockTests.Flags_slow_primary_response_as_low_precision_E15` |
| Caller cancels while providers are pending (E17, orchestrator) | `OperationCanceledException`, even if one provider already answered; both tasks observed | `TrustedClockTests.Propagates_caller_cancellation_*_E17` |
| Requests leave at different times / different RTTs | Compared at each round trip's midpoint, timestamps converted only via `IElapsedTimer` (QA-01, Linux ns ticks) | `TrustedClockTests.Corrects_for_*`, `Converts_timestamps_through_the_elapsed_timer_not_as_TimeSpan_ticks` |
| Both answers slow, e.g. RTT 2.8 s each and 2.5 s apart (QA-04) | Agree: effective tolerance `max(2000, (RttA+RttB)/2)`, RTT bound exclusive. RTT 0.4 s each and 2.5 s apart → disagree | `TrustedClockFindingsTests.*_QA04` |
| Delta just around the tolerance, e.g. 2000.5 ms (QA-05) | The rounded value (2001) is both compared and reported | `TrustedClockFindingsTests.Compares_the_same_rounded_delta_it_reports_QA05` |
| A source throws instead of returning a result (QA-06) | That source = `error` (logged at Error with the exception); the other one still serves; both → `time.unavailable` | `TrustedClockFindingsTests.*_QA06` |
| Body with a duplicated key, e.g. two `timeZone` values (QA-07) | `invalid_response` for both providers, any duplicated top-level key, even with equal values | `DuplicateKeyTests` |
| timeapi.io repeated October hour: `02:30` with `dstActive` true/false (E11, T6) | 00:30Z (+02) / 01:30Z (+01), one hour apart, never guessed (D-011) | `TimeApiIoSourceTests.Resolves_repeated_hour_with_dst_flag_E11` |
| timeapi.io local time in the March gap, 2026-03-29 02:30 (E13) | `invalid_response`, whatever `dstActive` says | `TimeApiIoSourceTests.Rejects_nonexistent_local_time_E13`, `Applies_tzdb_at_the_dst_boundaries` |
| timeapi.io `dstActive` contradicts tzdb, e.g. `true` in January (E10) | `invalid_response` | `TimeApiIoSourceTests.Rejects_dst_flag_true_in_January_E10` |
| timeapi.io redundant fields disagree with `dateTime`, or `milliSeconds` rounded instead of truncated | `invalid_response` | `TimeApiIoSourceTests.Rejects_redundant_field_that_disagrees_with_dateTime`, `Rejects_milliSeconds_that_is_not_the_truncated_fraction` |
| timeapi.io `dateTime` with an offset, `Z`, 8 fraction digits or a culture format | `invalid_response`; only offset-less ISO is read | `TimeApiIoSourceTests.Rejects_dateTime_that_is_not_offset_less_iso` |
| `/health/time` when providers disagree or are all down | 503 with `canPunch: false`, the punch's `time.*` code, per-provider outcome codes; same shape as 200 (D-012) | `TimeHealthEndpointTests.Returns_503_when_the_providers_disagree`, `Returns_503_when_all_providers_are_down` |
| `/health/time` polled repeatedly, or many callers at once | Served from a 10 s cache (monotonic timer, from when the answer arrived), failures cached too; one in-flight refresh shared by all callers | `TimeHealthCheckTests.Second_call_within_10_s_*`, `A_failed_report_is_cached_too_*`, `Concurrent_callers_during_a_refresh_share_one_provider_call` |
| A health-check caller disconnects during a refresh | Only that caller stops waiting; the shared refresh finishes for the others | `TimeHealthCheckTests.A_caller_that_gives_up_does_not_cancel_the_refresh_others_wait_for` |
| Punch right after a cached health check | Punch path asks both providers every time; never reads the health cache | `TimeHealthEndpointTests.Punch_path_never_uses_the_health_cache` |
| Provider `detail`, raw body or parsed time in the public health check | Never included: names, outcome codes, RTTs, delta, code only | `TimeHealthEndpointTests.Never_exposes_provider_detail_payload_or_parsed_time` |
| Time lab requested outside Development (Production, Staging, Testing) | 404; controller not discovered, `ITimeLab` not registered, no provider call | `TimeLabTests.Does_not_exist_outside_Development` |
| Time lab when a punch would fail (disagree, all down) | Still 200; `decision.result = failed`, `code`, `httpStatus: 503` | `TimeLabTests.Development_returns_200_even_when_a_punch_would_get_503`, `Disagreement_is_a_failed_decision_*` |
| Second open shift for one employee (two tabs / two servers) (S1) | The DB refuses it: `UX_Shifts_OneOpenPerEmployee`, SQL error 2601 | `ShiftConstraintTests.S1_*` |
| Clock-out equal to (T11) or before (T10) clock-in (S5) | The DB refuses it: `CK_Shifts_ClockOutAfterClockIn`, SQL error 547; `Shift.Close` refuses it first | `ShiftConstraintTests.S5_*`, `PunchStampTests.Close_refuses_*` |
| Repeated October hour (T6), DST-start instant (T7), µs / 100 ns instants (S9) | Stored as a UTC instant + the offset as seen; round-trips exactly | `PunchRoundTripTests.S9_*` |
| Overnight (T3) / year-end (T8) shift: which work date? | The Zurich date of clock-in (2026-01-12; 2026-12-31), from tzdb, checked again in SQL | `PunchStampTests` · `CK_Shifts_ClockInZurichDate` |
| `/health/time` during graceful shutdown (stopping token cancelled, client still connected) (QA-10) | 503 with `canPunch: false`, `code: time.unavailable`; was 500 `server.error`. A client that disconnects still gets no body | `TrustedClockStep4QaTests.QA10_health_time_during_shutdown_is_503_not_500_server_error` |
| A provider throws instead of returning a result, seen from `/health/time` and the 503 body | Only the outcome code `error`, never exception text; the Time lab (dev only) shows the detail | `TrustedClockStep4QaTests.Health_time_shows_error_outcome_*`, `Punch_503_body_for_two_throwing_providers_has_no_exception_text` |
| Server process in another time zone or culture (E16) | No effect: NodaTime tzdb + invariant parsing. Full suite identical under Pacific/Kiritimati + th-TH, Pacific/Pago_Pago + ar-SA, Asia/Kathmandu + fa-IR | `HostEnvironmentOverrideTests` (env `CLOCKMANAGER_TEST_TZ` / `CLOCKMANAGER_TEST_CULTURE`), CI run with `TZ` set |
| Time lab around DST (T6 repeated hour, T7 DST start) | Local time and offset from tzdb at the instant (02:30 +02:00 / 02:15 +01:00; 03:00 +02:00), even if the provider's offset says otherwise | `TimeLabTests.Local_time_and_offset_come_from_tzdb_around_DST` |
| 10 simultaneous Clock Ins, same employee, separate connections (S2) | `UX_Shifts_OneOpenPerEmployee`: exactly 1 row, the other 9 get SQL 2601 | `ShiftConcurrencyTests.S2_ten_parallel_clock_ins_*` (3 rounds) |
| Clock Out of shift A racing a new Clock In (tab A closes, tab B clocks in) | Never 2 open shifts: the clock-in either lands after the close or gets 2601; the clock-out always wins | `ShiftConcurrencyTests.Clock_out_racing_a_new_clock_in_never_leaves_two_open_shifts` (15 rounds) |
| Two Clock Outs on one open shift (double click / two tabs) (S10) | `RowVersion`: one wins, the other gets `DbUpdateConcurrencyException`, the first value stays | `ShiftConcurrencyTests.S10_two_parallel_clock_outs_*`, `S10_stale_clock_out_*` |
| Half-written Clock Out, either direction (S6) | `CK_Shifts_ClockOutComplete` refuses it, one test case per column; the optional cross-check pair may be null (D-009) | `ShiftIntegrityTests.S6_*` |
| UTC date stored instead of the Zurich work date (T3 overnight, T8 year end) | `CK_Shifts_*ZurichDate` refuses it | `ShiftIntegrityTests.T3_*`, `T8_*` |
| Cross-check provider stored with a NULL delta (QA-12) | Should be refused by `CK_Shifts_*CrossCheck`; accepted today because `NULL >= 0` is UNKNOWN | open (`ShiftIntegrityTests.QA12_*`, skipped until the fix) |
| Blank provider name in a stored punch (QA-13) | Should be refused by a CHECK; accepted today (NOT NULL only) | open (`ShiftIntegrityTests.QA13_*`, skipped until the fix) |

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
| 2026-11-17 | `TimeNowSource` took its name from the `Primary` config slot | With the roles swapped, time.now readings would be credited to "timeapi.io" in the audit trail. Found by the qa agent (QA-03) while writing step-2 tests | Fixed name `TimeNowSource.ProviderName = "time.now"`; regression test `Name_is_always_time_now_even_when_it_plays_the_secondary_role_QA03` | `4544b1d` |
| 2026-11-17 | Startup-validation tests from Phase 1 steps 1–2 | Racy under CPU load (`WebApplicationFactory` disposed the host first → `ObjectDisposedException`), so they could fail without a bug. Found by the qa agent in step 4 | `HostStartup` helper reads the host's startup log (D-018) | `b4fec47` |
| 2026-11-17 | `/health/time` refresh runs on the app's stopping token | During shutdown the cancellation surfaced as an unhandled error: 500 `server.error` instead of 503. Found by the qa agent (QA-10) | The controller maps that cancellation to 503 `time.unavailable` | `6feb0bb` |

## Known limitations

- **The first provider call after a restart or an idle minute is slow (QA-11).** A cold DNS + TCP + TLS setup costs
  ~0.8–0.9 s per provider (vs 0.13–0.22 s warm); end-to-end right after startup we measured ~2.5 s. That punch is still
  correct, but it is flagged `LowPrecision`, and in the worst case it could become single-source or a 503. Planned
  for Phase 3 (punch path): a startup warm-up whose result is never used for a record, and `SocketsHttpHandler`
  pool settings (longer idle timeout, `PooledConnectionLifetime`, short `ConnectTimeout`, no redirects).

- **A provider that answers with a WRONG time blocks all punches; a DEAD one does not.** If one provider is up but
  off by more than the tolerance, every punch fails with 503 `time.providers_disagree` (we can't tell which one is
  right, D-008), while a provider that is down only drops the cross-check (D-009). Mitigation: the `providers`
  extension and the log name both providers, and `/health/time` shows `crossCheck: "disagree"` (D-012). There is no config
  switch to drop the bad provider (swapping roles doesn't help: both are still asked). Not built: a third provider
  to break the tie, or a "single provider" mode.
- **The database checks the Zurich offset is +01:00 or +02:00 and that the work date matches it, but not that the
  offset is the right one for the instant (no DST calendar in SQL).** A raw-SQL write could store a winter punch as
  +02:00. Mitigation: `PunchStamp.From` checks the offset against tzdb before anything is saved;
  `ShiftIntegrityTests.Documented_gap_offset_that_disagrees_with_tzdb_is_accepted_by_the_database` records the gap.
- **Generic error codes come from the HTTP status.** Errors produced only by a status (`request.not_found`, `request.method_not_allowed`, ...) get a code mapped from the status. Specific codes come only from `AppException`.
