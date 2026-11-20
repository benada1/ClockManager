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
- **Date:** 2026-11-17 · **Status:** Superseded by D-087 (removed 2026-11-20) · **Area:** backend
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
- **Decision:** (c) for the Time lab (fractions cut to milliseconds, CET/CEST added for +01:00/+02:00); (b) remains the plan for ordinary screens. Never (a). *(2026-11-20: the Time lab and `serverTime.js` are removed (D-087); every screen now uses (b).)*
- **Why:** The browser's clock and zone play no part, and the offset is always visible, so the two "02:30" of October can be told apart.
- **Trade-offs:** Depends on the server sending both strings; the punch DTOs (Phase 3) should keep them.
- **Refs:** `frontend/src/lib/serverTime.js` · `frontend/src/pages/time-lab/ZurichTime.jsx`

### D-017 — Dev-only mock API and an explicit "backend unreachable" marker in the Vite proxy
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** frontend
- **Context:** UI error states (provider down, disagreement, API not running) must be checkable without breaking real providers.
- **Options:** (a) test only against the real backend · (b) a Vite plugin serving fixed scenarios, only in `npm run dev:mock`.
- **Decision:** (b), scenario chosen with `?mock=<name>` (accepted, single, lowprecision, dst, disagree, down, invalid, notdev, servererror, unreachable). The `/api` proxy answers a down backend with 502 + `X-Dev-Proxy-Error: backend-unreachable`, which the client turns into a `NetworkError`.
- **Why:** Vite's bare 502 looks like a server error; every state can be shown on demand.
- **Trade-offs:** The mock fixtures can drift from the contract (`TimeLabResponse` is the source of truth). Never part of the production bundle. *(2026-11-20: the Time lab scenarios are removed with the page (D-087); the auth/status/punch/history/manager scenarios in `dev/mockAuth.js` and the proxy marker stay.)*
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

### D-026 — CI runs the integration tests against a SQL Server 2022 container with a per-run password
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** ci
- **Options:** (a) keep skipping `Category=Integration` in CI · (b) a `services:` container with the sa password in a GitHub secret · (c) a `services:` container with a fixed password in the YAML · (d) `docker run` in a step with a password generated by `openssl rand` in the run.
- **Decision:** (d). The password is masked (`::add-mask::`) and passed to the tests via `CLOCKMANAGER_TEST_SQL`; a wait loop polls `sqlcmd` until the server accepts logins. Both test runs (normal and the E16 exotic-time-zone run) now include the integration tests, and `CI=true` turns an unreachable server into a failure, not a skip.
- **Why:** The constraint and race tests are the proof of CLAUDE.md rule 2; they must run on every push. (d) stores no secret anywhere (CLAUDE.md rule 3), and a `services:` container can't take a value computed during the run. (c) would put a password in git, even a throwaway one.
- **Trade-offs:** About 20–40 s extra per run for the image pull and boot (partly hidden behind restore/build). CI uses SQL Server 2022 on Linux, the dev machine SQL Server 2025 Express on Windows; we only use long-standing features, and CI is the referee (plan 02 §8).
- **Refs:** `.github/workflows/ci.yml` · `TestSqlServer.cs` · D-024

### D-027 — Phase 3 "who is punching": a Development-only `X-Dev-Employee` header behind `ICurrentEmployee` (superseded by D-049)
- **Date:** 2026-11-17 · **Status:** Superseded by D-049 (the header and its class were deleted in Phase 4) · **Area:** backend
- **Context:** Punches need an employee, but login is Phase 4. The user chose "punch first, login later" (plan 03 §3).
- **Options:** (a) build cookie login now · (b) `employeeId` in the punch body · (c) a dev-only header read by an `ICurrentEmployee` implementation registered only in Development.
- **Decision:** (c). `X-Dev-Employee: <username>` loads the employee from the DB. Missing, blank, repeated or unknown → 401 `auth.unauthenticated`; `IsActive = false` → 403 `employee.inactive`. Outside Development the registered implementation always returns "nobody", so every punch is a 401 and the header is never read.
- **Why:** `PunchService` depends only on `ICurrentEmployee`, so Phase 4 swaps the implementation without touching it. (b) would make "who" part of the client's payload, the kind of trust rule 2 forbids, and would leak into the contract.
- **Trade-offs:** In Development anyone can act as anyone. Acceptable for a local dev stand-in; P17 proves Production ignores the header.
- **Refs:** plans/03-punch-api.md §3 · plans/04-auth.md (replaced it) · `PunchApiTests.P17_the_old_X_Dev_Employee_header_is_ignored_in_every_environment_and_answers_401`

### D-028 — Punch order: state check, then the provider call, then the write; no transaction around the provider call
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** A provider call can take up to 3 s (D-010). Rule 2 needs the one-open-shift and out-after-in rules to hold under races.
- **Options:** (a) open a transaction (or lock the employee row), call the provider, write · (b) call the provider with no transaction, then write, and let the DB constraints catch races.
- **Decision:** (b). Order: who → `Idempotency-Key` format → cheap state check (409 without asking a provider) → `ITrustedClock` → monotonic guard (D-030) → `SaveChanges`. A lost clock-in race (SQL 2601 on `UX_Shifts_OneOpenPerEmployee`) → 409 `shift.already_open` with the winner's shift; a lost clock-out race (`DbUpdateConcurrencyException` on `RowVersion`) → 409 `shift.not_open`.
- **Why:** (a) holds locks for the length of an HTTP call and blocks every other punch for that employee (or more). The constraints already guarantee correctness; the state check is only a fast answer.
- **Trade-offs:** In a race the losers have already used a provider request. Step 2 puts the shift and the idempotency record in one transaction, still after the provider call.
- **Refs:** plans/03-punch-api.md §5 · `PunchService.cs` · `PunchApiTests.P5_*` (10 parallel clock-ins over HTTP, 3 rounds), `P12_*`

### D-029 — The punch body is read strictly: only `intent`, exact values
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** Rule 1: the client never sends a punch time. With default JSON settings, `{"intent":"clock-in","time":"..."}` would be accepted and the extra field silently ignored.
- **Options:** (a) ignore unknown fields (the default) · (b) reject unknown fields for the whole API (global `UnmappedMemberHandling.Disallow`) · (c) reject them for the punch body only (`[JsonUnmappedMemberHandling(Disallow)]` on `PunchRequest`).
- **Decision:** (c). Any extra member, an intent other than exactly `clock-in` / `clock-out` (case-sensitive, no spaces), null, a number, `{}`, `[]`, bad JSON or no body → 400 `request.invalid`, before any service code runs: no provider call, nothing saved.
- **Why:** A client that tries to send a time learns at once that it can't, instead of believing its time was used. (c) keeps the strictness visible on the one type where it matters, without changing other endpoints.
- **Trade-offs:** Adding a field to the punch body later is a breaking change for old servers (fine: one deployable). The body is validated before authentication, so an invalid body gets 400 even without a signed-in employee.
- **Refs:** `Application/Punches/PunchContracts.cs` · `PunchApiTests.P15_*` (12 bodies)

### D-030 — A trusted time not strictly later than the employee's last punch is a 503 `time.went_backwards`
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** Providers can drift or be skewed (zurich-time-cases T10/T11). The DB only stops `ClockOut <= ClockIn` within one shift, not a new clock-in before the previous clock-out.
- **Options:** status: (a) 409 · (b) 422 · (c) 503 (plan 03 Q2, user picked (c)). Reference: (i) the open shift's clock-in only · (ii) the latest of all the employee's clock-ins and clock-outs. Comparison: `<` or `<=`.
- **Decision:** (c) with (ii) and `<=`: the new instant must be strictly later than `max(latest ClockIn, latest ClockOut)`, read after the provider call. Otherwise 503, nothing saved, a Warning log with both instants. **Exception:** if that latest punch was saved *during* our provider call (it differs from the snapshot taken at the state check: another tab, a double click), the state changed under us, so the answer is the race's 409 (`shift.already_open` with the open shift / `shift.not_open`), not a 503.
- **Found by:** P5 under full-suite load: a loser that reached the guard after the winner had saved the same instant got 503 `time.went_backwards`. Regression tests: `PunchApiTests.Clock_in_with_the_same_time_as_a_clock_in_saved_during_its_provider_call_*`, `Clock_out_with_the_same_time_*` (both failed before the fix).
- **Why:** The employee did nothing wrong; our time source is untrustworthy right now, the same as the other `time.*` 503s, so the UI shows the same "time problem, retry" banner. (ii) also stops a clock-in that would overlap the previous shift. Equal is refused: a zero-length shift (T11) is meaningless and the DB would refuse it anyway.
- **Trade-offs:** If a provider stays behind, punches keep failing until it catches up (visible in the log). The repeated October hour is not affected: the guard compares instants, not wall-clock times (T6 passes).
- **Refs:** `PunchRules.Check` · `PunchRulesTests.P11_*`, `Guard_tells_a_backwards_clock_from_a_punch_saved_during_the_provider_call` · `PunchApiTests.P11_*`

### D-031 — Shift flags are computed on every read, not stored
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** A closed shift is flagged `long_shift` (> `MaxOpenShiftHours`) or `short_shift` (< `ShortShiftSeconds`); step 3 adds `forgotten_clock_out` for open shifts.
- **Options:** (a) store flags as columns when the shift is closed · (b) compute them from the UTC instants and the current `ShiftRules` options each time a shift is returned.
- **Decision:** (b), in `ShiftFlags.For`. Both bounds are strict: exactly 16 h is not long, exactly 60 s is not short. `durationSeconds` is the UTC difference truncated to whole seconds; flags use the exact duration.
- **Why:** Flags are a view of facts (two instants) plus config, not facts themselves. Stored flags go stale when an admin changes the thresholds or a manager corrects a punch (Phase 7). `forgotten_clock_out` depends on "now" anyway, so it can't be stored.
- **Trade-offs:** No index on flags: "all long shifts" is a query over the duration, not a column filter (fine at this scale; Phase 6 reports compute it in SQL).
- **Refs:** `Application/Shifts/ShiftView.cs` · `PunchRulesTests.P13_*`, `P14_*`, `Flags_are_computed_from_the_UTC_duration_not_the_wall_clock_across_DST_end`

### D-032 — Idempotent punches: only successes are stored, per employee, and a replay returns the stored body
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend, db
- **Context:** A lost response or a double click makes the client send the same punch again. Without help the retry gets 409 `shift.already_open` for a clock-in that actually worked.
- **Options:** (a) store every outcome, including 409/503 · (b) store only successful punches · (c) no table: treat "same key" as "the employee's latest shift". Scope: global key vs per employee. Replay: rebuild the answer from the shift now vs return what was answered then.
- **Decision:** (b), scoped per employee: table `IdempotencyRecords`, unique index `UX_IdempotencyRecords_EmployeeId_Key`. Same key + same intent → the stored status (201) and body, with no provider call and no new row. Same key + the other intent → 422 `idempotency.key_reused`, nothing changes. The body is the same `ShiftView` the controller returns, serialized with the API's own JSON options, so a replay is identical in content, including the flags and durations **as they were then** (a clock-in key still replays an open shift after the shift was closed). `CreatedUtc` is the punch's trusted instant, never a local clock. The table's CHECKs (intent, status 2xx, body is JSON, offset +00:00, non-empty key) and NO ACTION foreign keys are in `IdempotencyRecordConfiguration`.
- **Why:** (a) would make a retry after a 503 replay the 503 forever, which defeats the Phase 5 "Retry" button. (c) can't tell a retry from a genuine new punch and gives no 422. A global key would let one employee read another's answer. Returning the old body keeps "same request, same answer" true even when thresholds change; rebuilding it would not.
- **Trade-offs:** The body is stored as JSON text, so a later change of the `ShiftView` shape leaves old replays in the old shape (acceptable: keys are meant for seconds to minutes, not months). Records are never deleted (see Known limitations).
- **Refs:** plans/03-punch-api.md §5 · `Domain/IdempotencyRecord.cs` · `PunchIdempotencyTests` (P6, P8, P10 and the rest) · `IdempotencyRecordSchemaTests`

### D-033 — The shift and its idempotency record are written in one transaction, still after the provider call
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Options:** (a) save the shift, then the record, in two separate commits · (b) one explicit transaction around both writes · (c) the record first, then the shift.
- **Decision:** (b). `BeginTransaction` → save the shift (INSERT or closing UPDATE) → build the response from the saved row → insert the record → commit. The provider call and the state checks stay before it, so no lock is held during an HTTP call (D-028).
- **Why:** With (a), a crash between the commits leaves a shift without a record, and the retry then gets 409 instead of the replay. (c) is impossible: the record holds the response, which holds the new shift's Id. The explicit transaction can't hit EF's execution-strategy restriction because no retry strategy is configured.
- **Trade-offs:** The transaction holds the shift's lock for two statements (microseconds). Two SaveChanges calls instead of one.
- **Refs:** `PunchService.SaveShiftAndRecordAsync` · `PunchIdempotencyTests.The_shift_and_its_record_are_one_transaction_*` (a trigger makes the record insert fail; the shift is rolled back)

### D-034 — Parallel requests with the same key: the loser replays the winner's answer
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** Two requests with one key can both pass the replay lookup (no record yet) and the state check, then race to save.
- **Options:** (a) the loser answers 409 like any lost race · (b) the loser looks up the key again and replays the winner's stored answer · (c) lock per key before the provider call.
- **Decision:** (b). Every lost-race signal is checked against the key before it becomes a 409: `shift.already_open` / `shift.not_open` from the state check, from the monotonic guard, from SQL 2601 on `UX_Shifts_OneOpenPerEmployee` and from `DbUpdateConcurrencyException`; a 2601 on `UX_IdempotencyRecords_EmployeeId_Key` (told apart from the shift index by the index name in the message) is a replay too. A twin that left no record yet (it failed or is still in flight) leaves the original 409. Races with DIFFERENT keys are unchanged (P5, P12).
- **Why:** For the client, the second request is a retry of the first and must get the same answer; a 409 "you are already clocked in" for a clock-in that just succeeded is the confusion idempotency exists to prevent. (c) would hold a lock over an HTTP call (D-028).
- **Trade-offs:** The losers each cost a provider call, as in D-028. A loser that fails before the winner commits can still see a 409 once; its next retry replays.
- **Refs:** `PunchService.PunchAsync` · `PunchIdempotencyTests.P7_*` (10 parallel clock-ins, 3 rounds; 5 parallel clock-outs, 3 rounds)

### D-035 — The status endpoint's "server now": a short cache in its own class that cannot be used for a record
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** The UI needs a trusted "now" to show elapsed time without the browser clock (CLAUDE.md rule 1), and the status endpoint needs it for `forgotten_clock_out`. A status call per page load or poll must not hit the two free providers each time (D-010, D-012). A punch must still get a fresh time (master plan D1).
- **Options:** (a) a fresh provider call on every status request · (b) cache inside `ITrustedClock` / reuse `TimeHealthCheck`'s cache · (c) a separate `ServerNowProvider` that wraps `ITrustedClock` with a short cache, used only by the status endpoint · (d) no server time (the UI would need the browser clock).
- **Decision:** (c). `IServerNowProvider.GetAsync` returns a `ServerNowReading(Utc, ZurichOffset, Age)` or null. Success cached for `ServerNow:CacheSeconds` (default 10, validated 1–30). One in-flight fetch shared by concurrent callers (no stampede), run on the app's stopping token so a disconnecting caller doesn't cancel it for the others. The fetched instant is returned **as-is** (no server-side extrapolation) with `ageMs` from the monotonic `IElapsedTimer` (never a wall clock); the client adds its own elapsed time. **Failures are cached for 1 s only** (`ServerNowProvider.FailureTtl`): enough that a polling UI does not hit two providers on every request while they are down, short enough that recovery shows within a second; an expired success is never served stale when the provider is down now.
- **Why it is a separate class:** it must be impossible to use for a record. It does not implement `ITrustedClock`; it returns a `ServerNowReading`, not a `TrustedInstant`, which `PunchStamp.From` requires; `PunchService` does not receive it and always asks `ITrustedClock` afresh. (b) would put a stale value one injection away from a punch. `TimeHealthCheck`'s cache holds a health report, not an instant, and is not reused.
- **Trade-offs:** The displayed time is up to 10 s old (error bound = TTL, shown as `ageMs`); the `forgotten_clock_out` flag can lag by the same amount (D-036). With the cache cold, one status call waits for a provider call (up to the 3 s timeout). A UI that shows elapsed time must use `utc + ageMs + its own elapsed counter`; using `utc` alone right after a punch shows a time a few seconds before the clock-in.
- **Refs:** plans/03-punch-api.md §4, Q3 · `Infrastructure/Time/ServerNowProvider.cs` · `ServerNowProviderTests` · `StatusApiTests.D1_*`, `Parallel_status_calls_share_one_provider_call`

### D-036 — Status endpoint: flags computed on read from the trusted now, omitted when there is none; "last closed" = latest clock-out
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** `GET /api/me/status` returns the open shift, the last closed shift and `forgotten_clock_out` (D-031 said it depends on "now"). The provider can be down (P20).
- **Options:** flag: (a) compute with the server's local clock when the provider is down · (b) compute only from the trusted now and omit it otherwise · (c) store it. Provider down: (i) 503 like a punch · (ii) 200 with `serverNow: null`. Last closed: latest clock-out vs latest clock-in vs highest Id.
- **Decision:** (b) and (ii). `forgotten_clock_out` is in the top-level `flags` when the open shift started **strictly** more than `MaxOpenShiftHours` before the reported `serverNow.utc` (exactly 16 h is not flagged; 15 h 59 is not), duration from the UTC instants (a DST change doesn't matter). No trusted now → no time-based flags and `serverNow: null`, status still 200; the open shift is never auto-closed. A provider problem is never a 5xx, and no exception text reaches the body (only codes are logged). `lastClosedShift` = the closed shift with the latest clock-out instant (ties: highest Id). The open shift's own `flags` stay empty; the forgotten flag lives only at the top level so there is one place for it.
- **Why:** (a) breaks rule 1's spirit (a guess presented as fact) and a wrong flag is worse than none; the status screen must still work during an outage so the employee can see their state. Latest clock-out is "the shift I most recently finished", which matters when corrections (Phase 7) make shifts overlap or reorder; clock-in order would not.
- **Trade-offs:** The flag uses the cached instant without adding `ageMs`, so it lags by up to the TTL (10 s); the UI can also compute the age from `serverNow` and the open shift if it wants it sooner. A stale or cold cache with the providers down hides a forgotten clock-out until the providers return.
- **Refs:** `Application/Status/StatusService.cs` · `ShiftFlags.IsForgotten` · `ForgottenClockOutRuleTests` · `StatusApiTests.P13_*`, `P20_*`, `The_last_closed_shift_is_the_one_with_the_latest_clock_out_*`

### D-037 — Cold first provider call (QA-11): warm connection pool, one-shot startup warm-up that can never be a time
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** The first call per provider after a restart or an idle spell costs ~0.8-0.9 s (DNS + TCP + TLS) against 0.13-0.22 s warm (Phase 1 measurement). That punch gets `LowPrecision`, and in the worst case becomes single-source or a 503 (D-008, D-009, D-010).
- **Options:** pool: (a) .NET defaults (idle timeout 100 s, no lifetime, no connect timeout, follows redirects) · (b) tuned `SocketsHttpHandler`. Warm-up: (i) none · (ii) one request per provider after startup · (iii) a periodic ping every few minutes · (iv) warm-up that reads the answer as a first time sample.
- **Decision:** (b) + (ii), on the same named HttpClients the punch path uses, so the warm connection is the one a punch reuses. Defaults, all in `TrustedClock:Connection` (validated on start): `PooledConnectionLifetime` 300 s (DNS changes are picked up within 5 min), `PooledConnectionIdleTimeout` 300 s (a connection survives gaps between punches), `ConnectTimeout` 1500 ms and validated `< TimeoutMs` (a dead host fails at the TCP/TLS stage, not after the whole 3 s), `AllowAutoRedirect = false` (a time provider that redirects is suspicious; the answer is then an HTTP error, so fail closed), `EnableMultipleHttp2Connections = false` (one request at a time per provider; a second connection is another cold handshake). Keep-alive: HTTP/2 PING every 30 s, timeout 10 s, policy `Always` (also idle; 0 turns it off). The clients now allow HTTP/2 (`RequestVersionOrLower`) because the PING exists only there; ALPN falls back to 1.1. Warm-up (`ProviderWarmupService`, a `BackgroundService`, `TrustedClock:Warmup:Enabled` true, `TimeoutMs` 3000): after startup, in the background (`Task.Yield` first, so it never delays startup or readiness), one GET per provider in parallel, response drained into `Stream.Null`, one log line each with an outcome code only (`ok`, `http_500`, `timeout`, `failed`; Warning when not ok), every failure swallowed.
- **Why it can never be a time source (rule 1 / D1):** it does not depend on `ITrustedClock`, `ITimeSource`, `ServerNowProvider` or `TimeHealthCheck`, keeps no instant, never reads the body, and feeds no cache; a test asserts its constructor and fields. A punch still asks both providers fresh. One-shot rather than periodic: the free providers are a shared resource and a ping every few minutes (~700 calls a day each) buys little; the HTTP/2 PINGs keep an idle connection alive without any API request. Periodic re-warming was not built (see trade-offs).
- **Trade-offs:** The first punch still pays for TLS if it arrives before the warm-up finishes, if the provider or a middlebox closed the idle connection anyway (PING is only a hint to the server, and we do not know whether these providers speak HTTP/2), or after the 300 s lifetime. A DNS or TLS outage still gives low precision, a single-source punch or 503, as before (fail closed). Warm-up costs one extra call per provider per start. Tests turn it off (`NoNetworkByDefault`, env var) so no test host touches the network. **Not measured:** the effect on the real network (cold vs warm) was not measured in this change.
- **Refs:** plans/03-punch-api.md §2 QA-11, step 4 · `Infrastructure/Time/ProviderWarmupService.cs`, `TrustedClockServiceCollectionExtensions.CreateHandler` · `ProviderWarmupTests`

### D-038 — Handled refusals are caught by a small middleware so the framework does not log them at Error (QA-20)
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Context:** On .NET 8, `ExceptionHandlerMiddleware` logs "An unhandled exception has occurred" at Error for every exception before it calls `IExceptionHandler`. Every double click (409), reused key (422), provider outage (503) and inactive employee (403) therefore looked like a server failure; alerting on Error would fire on normal behavior. `SuppressDiagnosticsCallback` is .NET 9 only.
- **Options:** (a) a middleware just inside `UseExceptionHandler` that catches `AppException` and delegates to the existing `AppExceptionHandler` · (b) an MVC exception filter · (c) a logging filter that drops the framework's Error entry when the exception is an `AppException` · (d) accept it and tell operators to ignore that category.
- **Decision:** (a), `Common/Errors/AppExceptionMiddleware.cs`, registered in `UseAppProblemDetails`. It resets the response the way the framework does (clear, no-cache headers, no ETag) and calls `AppExceptionHandler.TryHandleAsync`, so there is still one place that builds the ProblemDetails and picks the log level (Information for 4xx, Warning for 5xx). Anything that is not an `AppException`, and any case where the response has already started, passes through to the framework unchanged.
- **Why:** (b) only sees exceptions thrown inside MVC, so an `AppException` from a middleware or a minimal endpoint would still log at Error, and MVC's own handling changes the body path; (c) matches on a category and message text of the framework, which can change in a minor release, and hides the real exception log if the filter is wrong; (d) leaves a useless alert.
- **Trade-offs:** One more middleware in the pipeline (an extra try/catch frame, no cost on the success path). The "reset the response" lines copy what the framework does and must be kept in step by hand. A real unexpected exception is still logged at Error twice (the framework's entry plus the handler's `LogError`); that was already so and was not changed here.
- **Refs:** `Common/Errors/ProblemDetailsSetup.cs` · `PunchQaLogTests.A_handled_business_refusal_does_not_log_at_Error`, `A_real_500_does_log_at_Error_so_the_alert_still_works` · `ErrorLoggingTests`

### D-039 — Error media type: declare `application/problem+json` on the error responses, no `[Produces]` on the controller (QA-18)
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Context:** `[Produces("application/json")]` on `PunchesController` and `MeController` forced every error body (400, 401, 403, 409, 415, 422, 503) to `Content-Type: application/json`. CLAUDE.md requires RFC 7807, whose media type is `application/problem+json`; a client that switches on the media type could not tell a problem from a success body.
- **Options:** (a) drop the controller-level `[Produces]` and name the media type on each `[ProducesResponseType]` (`application/json` for the success body, `application/problem+json` for `ProblemDetails`) · (b) `[Produces("application/json", "application/problem+json")]` · (c) a global result filter that rewrites the Content-Type of every error response.
- **Decision:** (a) on `PunchesController` and `MeController`. `TimeHealthController` and the dev `TimeLabController` keep `[Produces]`: their bodies are never ProblemDetails (the 503 health report is a `TimeHealthReport`).
- **Why:** (b) still negotiates the first type for every `ObjectResult`, so errors stay `application/json`; (c) is global magic that rewrites headers after the fact and hides the real cause. (a) is declarative, and Swagger documents the right type per status for free.
- **Trade-offs:** A new controller must remember to declare the media type on its error responses; `PunchQaBodyTests.Error_responses_use_the_problem_json_media_type` checks the real Content-Type for 400/401/403/409/415/422/503 and the framework 404/405, and would catch a new `[Produces]`.
- **Refs:** RFC 7807 · `Controllers/PunchesController.cs`, `Controllers/MeController.cs` · `PunchQaBodyTests.Error_responses_use_the_problem_json_media_type`

### D-040 — The 400 `errors` hold fixed client-facing sentences, built by one `InvalidModelStateResponseFactory` (QA-19)
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Context:** For bad JSON the default 400 body echoed System.Text.Json text: CLR type names (`ClockManager.Api.Application.Punches.PunchRequest`, `System.String`), parser positions (`LineNumber: 0 | BytePositionInLine: 10`), JSON paths (`$.intent`) and a noise entry `"request":["The request field is required."]`. That discloses internals and is not a stable text a client can rely on.
- **Options:** (a) a custom `InvalidModelStateResponseFactory` that rebuilds `errors` with fixed messages · (b) keep the default and filter/regex the offending messages · (c) drop `errors` and return only `title`/`code`.
- **Decision:** (a), `Common/Errors/InvalidRequestResponse.cs`, registered once in `AddAppProblemDetails` via `ApiBehaviorOptions`, so every `[ApiController]` (punch, status, later ones) gets it. It still goes through `ProblemDetailsFactory`, so status 400, `code` `request.invalid`, `traceId`, the `ValidationProblemDetails` shape and `application/problem+json` (D-039) are unchanged. Output: JSON syntax/top-level type/missing body → `body: "must be a JSON object"`; wrong type for a field → `intent: "has the wrong type or an invalid value"`; unknown member → `time: "unknown field 'time' is not allowed; the server sets the time"` (client's own name, control characters and quotes removed, max 40 chars); bad value → our own attribute text `must be "clock-in" or "clock-out"`. Keys are the camelCase wire names. `AppException` responses are untouched (different path).
- **Why:** (b) is a blacklist of framework wording that changes between releases, and one missed message is a leak; (c) takes away what a UI needs to say which field is wrong. (a) is a whitelist: text we wrote passes, anything from the JSON reader is replaced.
- **Trade-offs:** Less detail for a developer debugging a bad body (no position; the field name and the kind of problem remain). Recognising an unknown member still reads the framework's message ("could not be mapped to any .NET member"); if that wording changed the field would get the generic wrong-type sentence, never a leak, and the test would show it. Keys of the other validation errors are now camelCase (`name`, not `Name`), a small contract change for clients of the old text; no client existed yet.
- **Refs:** plans/03-punch-api.md · `Common/Errors/InvalidRequestResponse.cs` · `PunchQaBodyTests.Bad_body_400_does_not_expose_CLR_type_names_or_parser_positions`, `Unknown_field_name_echoed_to_the_client_*`, `ProblemDetailsTests.Model_validation_failure_*`

### D-041 — A duplicate member in the punch body is a 400, found by a converter that pre-scans the object (QA-17)
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Context:** `{"intent":"clock-in","intent":"clock-out"}` was accepted and the LAST member won (System.Text.Json default; .NET 8 has no option to reject duplicates). A gateway, WAF or log that reads the FIRST member sees another request than the one executed (request-smuggling style), and the intent decides whether a shift opens or closes.
- **Options:** (a) a `JsonConverter<PunchRequest>` that scans the object with a `Utf8JsonReader` copy and throws on a repeated name, then delegates to the normal strict read · (b) a middleware that buffers and pre-scans the body for every request · (c) accept and document "last wins".
- **Decision:** (a), `Application/Punches/PunchRequestJsonConverter.cs`, attached to `PunchRequest` only. Names are compared case-insensitively (`intent` and `Intent` are the same member for the binder, so `{"intent":..,"Intent":..}` is also rejected; escaped spellings like `intent` are unescaped before comparing). The exception carries the member as its path; `InvalidRequestResponse` (D-040) answers `errors: { "intent": ["must appear only once"] }` (client's own cleaned name for an unknown member, e.g. `time`). Status/`code`/media type are the same as every other bad body. The real read goes to a private wire type that keeps `JsonUnmappedMemberHandling.Disallow` (D-029), so unknown members, wrong types and `[Required]`/regex validation behave as before.
- **Why:** (b) costs a buffering middleware on every route for a rule only this body needs and has to guess which routes carry JSON; (c) leaves a parser differential on the one field that matters. (a) is local, uses the same reader the framework uses, and needs no new package.
- **Trade-offs:** One more class to keep in step with `PunchRequest` (a new body field must be added to the wire type too, otherwise it would be an "unknown field" 400, which fails safe). The pre-scan walks the object a second time (a body is a few bytes). Only the punch body is protected; a future body type needs the same converter (or a shared generic one). The `Wire` type means Swagger still shows `PunchRequest` unchanged.
- **Refs:** plans/03-punch-api.md · `Application/Punches/PunchRequestJsonConverter.cs` · `PunchQaBodyTests.Duplicate_members_are_rejected_as_ambiguous_with_a_fixed_message`, `A_rejected_duplicate_body_stores_no_idempotency_record_*`

### D-042 — The trusted instant is range-checked and floored to a database tick right after the clock call (QA-21, QA-22)
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Context:** NodaTime `Instant` keeps nanoseconds, `datetimeoffset(7)` keeps 100 ns ticks. An instant < 100 ns after the last punch passed the guard and `Shift.Close` but was stored equal to it, so `CK_Shifts_ClockOutAfterClockIn` threw: 500 (QA-21). An instant like year 1 or 9999 reached EF/SQL as a 500 (QA-22). Both are unreachable with the millisecond providers whose sources already reject years outside [2026, 2100).
- **Options:** (a) one `TrustedInstantRules.Normalize` in the punch service, right after `GetNowAsync`, whose result feeds the guard AND the stamp · (b) floor inside `PunchStamp.From` · (c) round in the EF value converter / the database.
- **Decision:** (a). The instant must be inside [2026-01-01, 2100-01-01) UTC, else 503 `time.invalid_response` (fixed message, provider outcome `out_of_range`, no instant in the body, nothing saved); then `Utc` is floored to whole ticks with `trusted with { Utc = ... }`. The window constants moved from `HttpTimeSource` to `TrustedInstantRules` and the sources reuse them (one definition, no local clock: rule 1). The offset is not recomputed: DST transitions fall on whole seconds, so flooring never crosses one, and `PunchStamp.From` still checks the offset against tzdb at the FLOORED instant.
- **Why:** The guard must compare the value that will be stored, and only the service sees both the guard and the stamp, so one call there keeps them equal. (b) runs after the guard, so the guard would still compare the unfloored value. (c) hides the change in the converter: the guard and the response body would differ from what is stored, and a converter that rounds up could even order two punches wrongly. Flooring never moves a punch later than the trusted time.
- **Trade-offs:** A sub-tick difference is now 503 `went_backwards` (the instant is not strictly later after flooring), which is correct for storage but looks odd for a provider that is only 50 ns ahead. The window is fixed in code, so it needs a code change in 2100 (accepted, same as before). An in-range but wrong time (e.g. 2099) is still accepted: see the far-future edge-case row.
- **Refs:** plans/03-punch-api.md · `Application/Time/TrustedInstantRules.cs` · `PunchQaGuardTests.Clock_out_less_than_one_database_tick_*`, `Clock_out_exactly_one_tick_after_*`, `A_sub_tick_instant_is_floored_*`, `An_absurd_instant_from_the_clock_*`, `The_plausible_window_boundaries_are_exact`

### D-043 — The last-punch snapshot is read BEFORE the state check, and a same-key twin is replayed also after `went_backwards` (QA-24, QA-15)
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Context:** `ClockInAsync`/`ClockOutAsync` read the open shift first and the last-punch snapshot (`lastBefore`) after it. If the winning twin committed in between, `lastBefore` already held the winner's instant; with an equal trusted instant (second-precision or cached provider) the guard saw "same punch as before" and answered 503 `time.went_backwards` ("Nothing was recorded") instead of 409 (different key) or the stored 201 (same key, D-032). It failed about 3 of 4 runs of the 30-request storms.
- **Options:** (a) reorder: snapshot first, then state check · (b) keep the order, and on `went_backwards` re-check the idempotency record and the state · (c) both · (d) one query that reads both, or a snapshot-isolation transaction.
- **Decision:** (c). (a) is the cause fix: with the snapshot older than or equal to the state, a winner that commits after the snapshot is either seen by the state check (409 `already_open`/`not_open`, replayed by `PunchAsync` for the same key) or leaves `lastNow != lastBefore` (`PunchedMeanwhile` → the same 409 path). `went_backwards` now means "no punch of this employee appeared since the snapshot", i.e. the time source is wrong. (b) is a small safety net: `time.went_backwards` joins the 409 codes in the `PunchAsync` catch, so a stored record under (employee, key) is replayed; with no record the 503 stands. Lookups stay scoped by `employee.Id` + key, so nothing leaks across employees.
- **Why:** (d) needs a transaction or isolation level around a read that precedes a 3 s HTTP call (D-028 forbids holding one), or a combined query that is harder to read. Reordering costs nothing and is easy to reason about. (b) alone would leave the different-key loser with a 503.
- **Trade-offs:** A race where another tab closes and reopens the shift between snapshot and state check gives 409 `not_open` (conservative, nothing saved). A real backwards clock with no concurrent punch is still 503 (`PunchQaGuardTests`). QA-23 (guard not atomic with the INSERT) is fixed by D-044.
- **Refs:** plans/03-punch-api.md · `Application/Punches/PunchService.cs` · `PunchQaConcurrencyTests.QA24_*` and their twins (run 10 times in a row, stable)

### D-044 — Punches of one employee are serialised by an update lock on the employee's row, and the guard runs again under it (QA-23)
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Context:** The monotonic guard (D-030) reads the latest punch BEFORE the write. A punch that stalled between the guard and its INSERT/UPDATE while the same employee's other tab did in + out (or out + in) was stored with an instant not later than the new last punch: overlapping shifts (A 07:00:05-07:00:10, B in 07:00:05) or touching ones. The one-open-shift index and `CK_Shifts_ClockOutAfterClockIn` still held; nothing in the DB orders shifts of one employee against each other.
- **Options:** (a) `SELECT ... FROM Employees WITH (UPDLOCK, ROWLOCK)` as the first statement of the save transaction, then re-read the latest punch and re-run the guard · (b) `sp_getapplock` with a per-employee resource · (c) a guarded `INSERT ... SELECT ... WHERE NOT EXISTS (later punch)` (and the same for the UPDATE) · (d) a DB-level ordering constraint or trigger · (e) accept and document.
- **Decision:** (a). `PunchService.SaveShiftAndRecordAsync`: begin transaction (still after the provider call, D-028/D-033) → lock the employee row → `EnsureLaterThanLastPunchAsync` again with the pre-call snapshot → write the shift → write the idempotency record → commit. If the instant is no longer later: a newer punch than the snapshot → the same 409 (`already_open` / `not_open`) that D-043 uses, which `PunchAsync` turns into the stored replay for the same key; the same punch as the snapshot → 503 `time.went_backwards`. Lock order is the same in every path: Employees row, then Shifts, then IdempotencyRecords, so there is no cycle. The FK checks of the two inserts need only a shared lock on the employee row, which our own update lock allows; plain readers (`RequireActiveAsync`) take shared locks, which update locks allow too. Nothing else locks Employees.
- **Why:** (b) works too but is an extra concept (a named lock, `LockOwner`, manual release rules) with no row to point at; (a) uses the row that already is the natural owner, is released by commit/rollback with no cleanup, and `ROWLOCK` on a single key lookup never escalates. (c) guards only the one statement, needs a different shape for the clock-out UPDATE, and still cannot re-run the guard's three-way decision (409 vs 503) or the replay; the guard stays readable C#. (d) a trigger would have to read the other shifts inside every write and still races without a lock under READ COMMITTED; a unique/CHECK constraint cannot compare rows. (e) leaves a data-integrity hole in the main feature.
- **Trade-offs:** Punches of ONE employee queue behind each other for the length of one short transaction (lock, one read, two writes, commit; no HTTP call inside); other employees are never blocked. A stuck transaction would block that employee's punches until the request is cancelled (no `LOCK_TIMEOUT` set: the request token cancels the wait). Any future writer of an employee's shifts (manager corrections, Phase 7) must take the same lock first, in the same order. The explicit transaction still needs `CreateExecutionStrategy` if retry-on-failure is ever enabled (unchanged).
- **Refs:** plans/03-punch-api.md · `Application/Punches/PunchService.cs` · `PunchQaConcurrencyTests.QA23_a_storm_never_leaves_overlapping_or_touching_shifts` (stable 10 runs; fails without the fix), `A_punch_waits_for_the_employee_lock_*` (deterministic: a second connection holds the lock)

### D-045 — Tailwind CSS v4 for the new screens
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend
- **Options:** (a) Tailwind v4 with the Vite plugin · (b) more plain CSS · (c) a component library.
- **Decision:** (a). Colors, font and radius live once in an `@theme` block (`src/app.css`); repeated styling lives in `Button`, `Field`, `Banner`, `Card`.
- **Why:** The user asked for Tailwind; a small shared set of components keeps the screens consistent.
- **Trade-offs:** Two styling systems for now. Tailwind's reset is not imported, so the old Time lab page keeps its look. *(Ended 2026-11-20 by plan 10: the Time lab uses the shared components, index.css is gone and the reset is on.)*

### D-046 — Hand-written router and auth context, no new libraries
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend
- **Decision:** `AuthProvider` asks `GET /api/auth/me` on load. The login is an HTTP-only cookie, so JavaScript never touches a token. Any 401 `auth.unauthenticated` goes through one hook in the API client and sends the user to login.
- **Trade-offs:** An expired session is noticed only on the next request or refresh.

### D-047 — Punch screen: key per attempt, retry with the same key, no clock (the elapsed-time part amended by D-058, the no-clock part by D-084)
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend / UX
- **Decision:** A new `Idempotency-Key` per attempt, kept only after errors where the result is unknown (503, 429, network, 5xx) so "Try again" can't punch twice. No live clock or timer anywhere (user's choice): a time is shown only after a punch or from a saved shift, always from the server's string, formatted in Europe/Zurich and labeled "Zurich time". The offset is added on DST days. A forgotten shift is a notice, never closed by the UI.
- **Trade-offs:** No elapsed time while clocked in. `serverNow` from the status endpoint is not used.

### D-048 — Stateful mock mode for the UI
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend
- **Decision:** `dev/mockAuth.js` (dev server only) fakes login, signup, status and punches. Scenarios come from `?mock=` in the page URL (e.g. `open`, `forgotten`, `unavailable`, `conflict`, `expired`, `offline`).
- **Why:** Every screen state can be checked without the backend, and the frontend could be built in parallel with Phase 4.

### D-049 — Login is a JWT in an HTTP-only cookie (replaces the dev header, D-027)
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Options:** (a) bearer token kept in `localStorage` · (b) server-side session cookie · (c) JWT in an HTTP-only, SameSite=Lax cookie.
- **Decision:** (c), checked by the standard JWT handler. Cookie `clockmanager_auth`, `Secure` over HTTPS. A bearer header is ignored on purpose. 8 hour life, no refresh (`Auth:TokenLifetimeHours`, 1–24).
- **Why:** Scripts can't read the token, so a script bug can't steal it. The API is same-origin through the Vite proxy, so no CORS or CSRF token is needed; Lax blocks cross-site POSTs and signup/login accept only JSON.
- **Trade-offs:** No revocation before expiry (logout only clears the cookie); a deactivated employee is still blocked because the employee is loaded on every request. Behind a TLS proxy the app needs forwarded-header settings for `Secure`.
- **Refs:** plans/04-auth.md · `Infrastructure/Identity/`

### D-050 — Signing key from config only; Development makes a random one
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** `Auth:SigningKey` (user-secrets or `Auth__SigningKey`), at least 32 characters, checked on startup. Development without a key makes a random one each run (logins end on restart); any other environment refuses to start.
- **Why:** A fixed dev key in `appsettings.Development.json` would be a secret in git (rule 3).

### D-051 — The server clock is used for the token's issue and expiry time only
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Options:** (a) the trusted clock · (b) the server clock in one marked class.
- **Decision:** (b): `AuthClock`, one `internal` class with a `clock-ok` marker. A token is not a punch record, and punches never use it.
- **Why:** (a) would make login fail whenever the time providers are down.
- **Trade-offs:** A wrong server clock could only change session length, never a record.

### D-052 — Login failures give one answer; passwords use the built-in hasher
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** Wrong password, unknown user and a user without a password all give the same 401 `auth.invalid_credentials`; an unknown user costs a dummy hash so timing doesn't reveal names. 403 `employee.inactive` appears only after a correct password. Passwords use `PasswordHasher<Employee>` (PBKDF2) in a nullable `PasswordHash` column; seeded users have none and can't log in.
- **Trade-offs:** Signup still reveals whether a name exists (409); rate limits come in step 3.

### D-053 — Open signup creates Employees only
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** The role is set by the server; a `role` field in the body is ignored. Managers exist only through the seed. Username: 3–32 letters, digits, `.`, `_`, `-`, unique ignoring case; password 8–128.

### D-054 — Rate limits with the built-in limiter, fixed windows
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Options:** (a) a package such as AspNetCoreRateLimit · (b) sliding window or token bucket · (c) built-in `Microsoft.AspNetCore.RateLimiting`, fixed windows, no queue.
- **Decision:** (c). Login and signup: 10 per minute per IP (one shared budget). Punches: 60 per minute per signed-in employee. All under `RateLimits`. A refusal is 429 `request.rate_limited` with `Retry-After`.
- **Why:** No new dependency, per-endpoint policies, easy to test. A login screen should answer at once, so no queue.
- **Trade-offs:** A fixed window allows up to twice the limit across a boundary. State is in memory per process (several servers would each count). Behind a proxy the IP is the proxy's until forwarded headers are set up. Users behind one NAT share a budget.

### D-055 — The punch limit is per employee and sits after authentication
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** The punch limit uses the token's `sub`, so one runaway client can't hammer the time providers; anonymous requests are not counted, so they get 401 and can't use up anyone's budget. Failed punches (409, 422, 503) count too.

### D-056 — The seeded manager gets a password only from configuration
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** In Development, `Seed:ManagerPassword` (user-secrets or env) gives only `manager` a login, and only while it has none; re-running the seed never changes an existing hash. Other seeded users have no password.
- **Why:** A demo login without a password in git (rule 3).

### D-057 — The punch screen is two big switch blocks
- **Date:** 2026-11-18 · **Status:** Superseded by D-085 · **Area:** frontend · **Amends:** D-045..D-048 (layout only)
- **Decision:** Clock out (left) and Clock in (right) are two large rounded blocks from the user's sketch (`docs/design/punch-screen-sketch.png`). The active one is accent-coloured with a real button; the other is grey, dashed and disabled, with the visible reason ("Not available: you are clocked in"). On phones they stack with the active block first.
- **Why:** The app has very few features, so the screen can be simple and large. State never depends on colour alone.

### D-058 — Elapsed time on the punch screen (amends D-047)
- **Date:** 2026-11-18 · **Status:** Accepted, extended by D-084 · **Area:** frontend · **Amends:** D-047 ("no elapsed time")
- **Decision:** While clocked in, the block shows "Clocked in for H:MM:SS", counted as `(serverNow − clockIn) + ageMs + (performance.now() − syncAt)`. It uses server times plus a monotonic stopwatch, never the browser's wall clock, and is labeled as server time. With no `serverNow` there is no counter, only "Since 08:03 (Zurich time)".
- **Trade-offs:** `performance.now()` may pause while the computer sleeps; the next focus refresh re-syncs it. The ticking counter is hidden from screen readers; a spoken text changes about once a minute.

### D-059 — The screen flips only on a confirmed success
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend
- **Decision:** The state changes only after the server answers success with the expected shift (a clock-out must have `clockOut`, a clock-in must not). No optimistic update. Any other outcome keeps the state and shows the message. One punch at a time; a punch is never aborted; status fetches are numbered and stale answers are ignored. After a success the result is shown at once and the refresh follows; a failed refresh keeps the result and shows a retry notice. The key is reused only for retryable failures (503, 429, network, 5xx).
- **Trade-offs:** Slightly slower feel. Any 2xx with the right body counts, because the API layer returns only the body.

### D-060 — Frontend logic tests with Vitest
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend
- **Decision:** Vitest (node environment) tests the pure punch controller with a fake API (24 tests, `npm test`, part of `verify` and CI). Replaces the earlier "no frontend tests" note.
- **Not done:** component and browser tests (would need jsdom and Testing Library).

### D-061 — History page: week navigation by date arithmetic, no clock
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend
- **Options:** (a) Previous/Next add or subtract 7 days on the `yyyy-MM-dd` strings · (b) ask the server for the previous/next week · (c) a browser-clock "today".
- **Decision:** (a), then request explicit `from`/`to`. The page opens on the week the server returns without parameters (the week of the latest shift).
- **Why:** No clock is needed anywhere, and the tests cover DST weeks and the year boundary.
- **Trade-offs:** Next can walk into empty future weeks.

### D-062 — History loading: numbered requests, keep the shown week on errors
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend
- **Decision:** Every request gets a sequence number and the older one is aborted, so a stale answer can't show the wrong week. A failed load keeps the week on screen with a retry banner. Previous/Next are disabled while loading.

### D-063 — History shows open shifts but doesn't count them
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend / UX
- **Decision:** An open shift is shown as "Still open: not counted until you clock out", with no duration, and the totals count closed shifts only (as the server sends them). Times show their offset ("02:30 UTC+2 to 02:15 UTC+1") on clock-change days.
- **Trade-offs:** A long-open shift can make a week look low; a hint next to the total explains it.

### D-064 — History endpoint: a range with a default week and a 31-day cap
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Options:** (a) a fixed week parameter · (b) a free `from`/`to` range with a default week · (c) paging by shift.
- **Decision:** (b). Without parameters the server returns the Monday to Sunday week that contains the work date of the employee's latest shift. At most 31 days. Shifts are chosen by their Zurich clock-in date, so the existing index is enough (no migration).
- **Why:** The UI can step weeks with plain date arithmetic, the query stays bounded, and the default needs no clock.
- **Trade-offs:** Totals follow the requested range only.

### D-065 — History dates are read as text, with fixed error messages
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** `from` and `to` are parsed by us as strict `yyyy-MM-dd` (years 1900–2200), given together or not at all; an empty value counts as given and invalid. The 400 uses fixed sentences and never echoes the input.
- **Why:** Binding to a date type would echo client text and give an unstable message (same reasoning as D-040).

### D-066 — Open shifts and clock-change days in history
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** An open shift is listed (`open: true`, no duration) and never counted in totals; it gets `forgotten_clock_out` only when a trusted time exists (same rule as D-036), otherwise no flag and still 200. A day gets the note `dst_change` when its length from the time zone rules is not 24 hours; no dates are hard-coded. Durations are from the UTC instants.
- **Trade-offs:** An open shift's hours are missing from the week total until it is closed.

### D-067 — The manager types a Zurich wall time; the UI works out the offset without a clock
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend
- **Options:** (a) send a plain local time and let the server guess · (b) the user types the offset · (c) a pure resolver that tries +02:00 and +01:00 and keeps those that map back to the same Zurich wall time.
- **Decision:** (c). One match is used. Two matches (the repeated October hour) means the manager must pick "first time (summer)" or "second time (winter)". No match (the skipped March hour) is an inline error. The server checks the offset again.
- **Why:** It never guesses and never reads the browser clock, and it is unit tested.

### D-068 — The correction dialog closes only on success; a stale shift reloads in place
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend
- **Decision:** No optimistic update (same rule as D-059). On `correction.stale` the dialog reloads the shift, keeps what was typed and explains why. Rule refusals appear next to the time field; a 503 shows the trusted-time banner with Try again. It uses the native `<dialog>` (focus trap, Escape, inert page) with no extra library.

### D-069 — Manager pages reuse the History week view; problems come first
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend / UX
- **Decision:** The employee's week reuses `WeekView` and `DayCard` with a Correct button per shift. On the manager home, Problems sits above Employees because those rows need action. The Manager link shows only for managers; the server enforces access.

### D-070 — Manager access is checked against the database role
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Options:** (a) the role in the token · (b) a check in the service only · (c) a policy that loads the employee, plus the service check.
- **Decision:** (c). Anonymous → 401; a non-manager or an inactive manager → 403 `auth.forbidden`, before the body is read.
- **Why:** A demoted manager's 8-hour token must stop working at once (fits D-049).
- **Trade-offs:** One employee lookup per request (shared within the request).

### D-071 — A correction: trusted time first, then one transaction under the employee lock
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** A fresh trusted-clock reading (never the display cache) is taken before the transaction (D-028). Inside it: lock the employee row (D-044, same order as punches), reload the shift, compare the row version, check the rules, update the shift and insert the audit row together. 503 without trusted time, nothing saved.
- **Why:** The employee and the manager write the same row; the lock keeps the rules atomic and the row version catches changes made since the manager read the shift. Both race orders are tested.

### D-072 — The corrected time must carry the right Zurich offset
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** `newTime` is ISO with an offset that must equal the Zurich offset at that instant. In the repeated October hour both offsets are valid (two different instants); times in the spring gap are always refused. Seconds required, no `Z`, years 2026–2099, fixed messages.

### D-073 — Shift rows hold the current values; corrections are an insert-only log
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend / db
- **Decision:** The Shifts row is updated to the corrected value (so the open-shift index, the out-after-in check, History and totals keep working), and `ShiftCorrections` records old value, new value, manager, reason and trusted time. Rows are never updated or deleted; the original is the first row of each field. A corrected punch is stamped `manager_correction`.
- **Trade-offs:** Reading the original needs the log (the shift page shows it).

### D-074 — Correction rules: touching counts as overlap, no-op is refused
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** A shift may not overlap or touch another shift of the same employee (an open one counts as running until now), same as the punch guard (D-030). A correction to the current value is 422 `correction.unchanged` (not in the plan; it would only add audit noise). A manager may correct their own shifts; it is logged like any other.

### D-075 — Problems list filtered in SQL, newest first, at most 100
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** Long and short shifts are found with SQL date arithmetic on the UTC instants (same bounds as the flags); forgotten clock-outs only when a trusted time exists (D-036).

### D-076 — The project stays on .NET 8 although its support ended on 10 Nov 2026
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** platform
- **Options:** (a) stay on .NET 8, as the assignment says · (b) move to a newer LTS.
- **Decision:** (a). The brief names ASP.NET Core 8; no vulnerable package is reported by `dotnet list package --vulnerable`.
- **Trade-offs:** No further runtime patches from Microsoft. A move to a newer LTS is a package and `TargetFramework` change, not a redesign.

### D-077 — "Try again" after a lost answer replays the same punch and key
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend
- **Options:** (a) derive the intent again from the refreshed status · (b) hide the banner when the refresh already shows the new state · (c) replay the stored attempt (same intent, same key).
- **Decision:** (c). The server answers a replay with the stored 201, so retrying is always safe. The banner says "Try again to confirm: it will not be recorded twice". A missing `crypto.randomUUID` (plain http) uses a fallback generator, or shows a clear message and sends nothing.
- **Why:** With (a) a lost clock-in answer made "Try again" send a clock-out (QA-27).
- **Trade-offs:** The banner can sit next to a screen that already shows the new state.

### D-078 — A correction is "unchanged" when the Zurich minute is the same
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** frontend
- **Decision:** The form shows minutes only, so typing the current minute shows "already the current time" and sends nothing, whatever the seconds (QA-28).
- **Trade-offs:** A manager can't correct only the seconds, which is not a real need.

### D-079 — JSON bodies must be UTF-8 (or have no charset)
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Options:** (a) catch the formatter exceptions · (b) a small middleware that rejects early · (c) leave it.
- **Decision:** (b). Any JSON request whose charset is not utf-8 (utf-16, empty `charset=`, ...) gets 415 `request.unsupported_media_type`, as the other unsupported charsets already did.
- **Why:** utf-16 and an empty charset made the JSON reader throw: a 500 and an Error log on every JSON endpoint (QA-42).
- **Trade-offs:** A real utf-16 client is refused.

### D-080 — Login and signup limits: IPv6 /64, 16 KB bodies, duplicate fields
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** The login/signup rate limit counts an IPv6 client by its /64 (one subscriber) and an IPv4-mapped address as IPv4; `X-Forwarded-For` is still ignored (D-054). Signup, login, punch and correction bodies are limited to 16 KB. Duplicate JSON fields are refused in signup and login too (amends D-041). Every response carries `X-Content-Type-Options: nosniff`.
- **Why:** A per-address budget could be multiplied by anyone with an IPv6 range; the 30 MB default was parsed before validation (QA-46, QA-48, QA-41, QA-44).
- **Trade-offs:** Users sharing one /64 share a budget. The test host can't enforce the size limit, so the test checks the endpoint settings.

### D-081 — A token for a missing user is 401 on manager pages too; a corrupt hash is a failed login
- **Date:** 2026-11-18 · **Status:** Accepted · **Area:** backend
- **Decision:** A valid token whose employee is missing gets 401 `auth.unauthenticated` on `/api/manager/*` (as on `/api/me/*`); inactive or non-manager employees stay 403. A stored password hash that is not valid base64 counts as a failed login (401, same timing), never a 500 (QA-45, QA-43).

### D-082 — Light and dark theme from one set of tokens; light is the default
- **Date:** 2026-11-20 · **Status:** Accepted · **Area:** frontend / UX · **Amends:** D-045 (token values)
- **Context:** The redesign mockups are dark and their demo follows `prefers-color-scheme`; the user asked for light on a first visit.
- **Options:** default: (a) follow the system setting · (b) always light until the user switches. Tokens: (c) `dark:` classes on every element · (d) one `@theme` (light) plus a `:root[data-theme='dark']` block that overrides the same `--color-*` variables.
- **Decision:** (b) + (d). The sun/moon button in the top bar (also on the log-in pages) sets `data-theme` on `<html>` and stores `cm-theme` in `localStorage` (try/catch; anything but `dark` means light). A 3-line inline script in `index.html` applies it before the first paint. The default Tailwind palette is switched off (`--color-*: initial`) so only tokens can be used. The toggle's label says what a press does ("Switch to dark mode"); no `aria-pressed`, because a label that changes plus a pressed state would be announced twice.
- **Why:** (a) was explicitly not wanted. (d) keeps components theme-free and the two palettes side by side in one file; (c) doubles every colour class.
- **Trade-offs:** No "follow the system" option. Two mockup values fail WCAG and were replaced: text-input borders (`#D0D5DD`, 1.4:1) use `#868E9C` light / `#5F6B80` dark (≥ 3:1), and the "faint" grey is used only for dots and arrows, never for text (the clock's seconds use the muted grey).
- **Refs:** plans/10-ui-redesign.md §3 · `src/app.css` · `src/lib/theme.js` · `theme.test.js`

### D-083 — No web font download: Inter if installed, else the system font
- **Date:** 2026-11-20 · **Status:** Accepted · **Area:** frontend
- **Options:** (a) the mockup's Google Fonts link · (b) bundle Inter as a package · (c) font stack `Inter, Segoe UI Variable, Segoe UI, system-ui`.
- **Decision:** (c).
- **Why:** (a) sends every visitor's IP to a third party and fails offline; (b) adds a dependency and ~100 KB for a small look difference. On the Windows PCs this runs on, Segoe UI is very close.
- **Trade-offs:** The app looks slightly different from the mockup where Inter is not installed.

### D-084 — A live Zurich clock on the Clock page, from server time only (amends D-047, extends D-058)
- **Date:** 2026-11-20 · **Status:** Accepted · **Area:** frontend / UX · **Amends:** D-047 ("no live clock", the user's earlier choice, changed by the redesign mockups)
- **Context:** The mockup shows a big HH:MM:SS clock, a "Good afternoon" greeting and the full date. CLAUDE.md rule 1 forbids the device clock.
- **Options:** (a) the browser clock, labelled · (b) a new status request every second · (c) the D-058 method for "now": `serverNow.utc + ageMs` at the moment the status arrived, plus `performance.now()` since then.
- **Decision:** (c), in `lib/serverClock.js` (pure, tested) and `useServerClock.js` (the only `performance.now()` line, marked `clock-ok: display only, re-synced from server`). The status is re-fetched once a minute while the tab is visible, on focus and after every punch. A punch clears `serverNow` but keeps the sync time, so the last anchor stays until the refresh (no flicker); a new sync without `serverNow` (providers down) removes the clock, the greeting becomes "Hello, name" and the card says "Server time unavailable". Elapsed time while clocked in uses the same anchor. The label reads "Zurich time · from the server", with the offset on the two DST days. The date and the greeting hour come from `Intl` in Europe/Zurich.
- **Why:** (a) breaks rule 1; (b) would hit the time providers' cache endlessly. (c) is exact to the status cache age and never shows a device time.
- **Trade-offs:** Between syncs the clock drifts with the PC's monotonic timer (well under a second per minute, corrected each minute); if the PC sleeps, the next focus re-syncs it. One extra status request per minute per open tab. The ticking digits are hidden from screen readers; they get the minute.
- **Refs:** plans/10-ui-redesign.md §4 · `serverClock.test.js` (T6, T7, T8, anchor kept after a punch, cleared on a sync without time)

### D-085 — One clock card with one button replaces the two switch blocks (supersedes D-057)
- **Date:** 2026-11-20 · **Status:** Accepted · **Area:** frontend / UX
- **Decision:** The Clock page follows the mockup: a status pill ("On shift" / "Not clocked in"), the server clock, one line saying what happened ("Shift started at 08:03 · running for 2:38:12", "Clocked out and saved"), and one full-width button ("Clock in" in the accent colour, "Clock out" inverted). Punch errors and "Try again" appear inside the card, just above the button. The right column shows "Last shift" (from the status) and "This week" (`GET /api/me/shifts` for the Monday–Sunday week of the server's Zurich date by string arithmetic, `weekOf`; without a server date the server's default week, labelled "Latest week"). The bar chart is plain divs with one spoken summary (`role="img"`).
- **Why:** The user's new design. One button that changes its words is simpler than two blocks, one of them always disabled, and the same button element keeps focus across the change.
- **Trade-offs:** The disabled block's explanation ("Not available: you are clocked in") is gone because nothing is disabled to explain. Week totals count closed shifts only, as on History (D-063).
- **Refs:** plans/10-ui-redesign.md · `views.test.js` (`weekOf`, `weekDays`, `shiftTimeParts`)

### D-086 — Team page: problem filter chips in the browser, people as cards
- **Date:** 2026-11-20 · **Status:** Accepted · **Area:** frontend / UX · **Amends:** D-069 (layout)
- **Decision:** "Needs review" shows a count and four chips (All / Forgot to clock out / Unusually long / Very short, `aria-pressed`) that filter the loaded list in the browser; "Showing N of M" is announced. A forgotten clock-out is a red pill, long and short shifts amber, everywhere (`shiftBadges` tone `danger`). Each row has a "Review" button to the shift page. The employees table became a grid of person cards (avatar, name, status, warning pills) linking to the person's week; the employee and shift pages get a "‹ Team" back link and an avatar header.
- **Why:** The server already returns at most 100 problems (D-075), so filtering in the browser is instant and needs no new endpoint.
- **Trade-offs:** The table scrolls sideways on a phone (min width 48rem) instead of stacking; the cards wrap. Per-person weekly hours from the mockup are left out (see "Not done").

### D-087 — The Time lab developer tool is removed (supersedes D-013)
- **Date:** 2026-11-20 · **Status:** Accepted · **Area:** frontend / backend
- **Context:** The user asked to remove the Time lab: the `#/time-lab` page and its Development-only `GET /api/dev/time-lab`, which asked both time providers on each click.
- **Options:** (a) remove only the page and link, keep the endpoint · (b) remove the page, the endpoint, its service and mapper, and the `[DevelopmentOnly]` helper that only it used.
- **Decision:** (b). Removed: the page and its panels, `api/timeLab.js`, `lib/serverTime.js` (only the lab used it), the footer link, the lab's mock scenarios; `TimeLabController`, `ITimeLab`/`TimeLabRunner`/`TimeLabMapper`, `AddTimeLab`, `DevelopmentOnly.cs` and `TimeLabTests`. A test now checks that the old route is 404 in Development and Production.
- **Why:** The endpoint existed only for the page; an unused endpoint that calls real providers would be dead code with an attack surface. The punch path, `/health/time` (D-012) and the trusted clock are untouched, and the clock's own tests keep covering the providers, the cross-check and DST.
- **Trade-offs:** No screen shows the providers' raw answers anymore; `/health/time` (names, outcomes, round trips) and the logs are what's left for diagnosing a provider. Restoring it means reverting this commit.
- **Refs:** `TrustedClockStep4QaTests.Removed_time_lab_route_is_404`

### D-088 — Weekly bar chart: a tooltip per day, one tab stop with arrow keys, one name per day
- **Date:** 2026-11-20 · **Status:** Accepted · **Area:** frontend / UX · **Amends:** D-085 (the chart's single `role="img"` summary)
- **Context:** The user wants each day's total on hover (History week summary, Clock page "This week"), also for 0-time days, and usable by keyboard, touch and screen readers (WCAG 1.4.13).
- **Options:** keyboard: (a) one tab stop per day (7 Tab presses per chart) · (b) one tab stop for the chart, arrow keys / Home / End move between days (roving `tabindex`). Screen readers: (c) keep the one summary image and hide the columns · (d) each column is a `role="img"` named with the tooltip text inside a `role="group"` "Hours per day", tooltip `aria-hidden` · (e) tooltip as `role="tooltip"` linked by `aria-describedby`.
- **Decision:** (b) + (d). The tab stop starts on the highlighted day (today on the Clock page), else Monday. The whole column (bar, empty space, label) is the hit area. Hover or focus shows the card; a click or tap keeps it until a tap/click outside, blur or Escape; Escape works anywhere while it shows. Text from `dayBarParts` (same `formatDuration` as the day cards): "Wednesday · 43 h 49 min · 2 shifts", "Tuesday · No shifts", "Thursday · Nothing counted yet · 1 shift" (open shift only). The outer two columns on each side anchor the card to their edge so it stays inside the chart.
- **Why:** (a) adds 7 stops to the Clock page's tab order for read-only data; (b) is the usual chart/toolbar pattern. (c) would say every value twice once the tooltip is readable; (e) reads the name and then the same description again. With (d) each value is said once, in browse mode too, and the name equals the visible text.
- **Trade-offs:** Arrow keys are not discoverable without a hint; Tab users still get today's value and the arrows work as in other charts. After a mouse click the card stays (focus) until the user clicks elsewhere or presses Escape.
- **Refs:** `src/components/BarChart.jsx`, `barChartNav.js` · `barChart.test.jsx`, `views.smoke.test.jsx`

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
| The removed Time lab route requested (any environment) | 404, no provider call (D-087) | `TrustedClockStep4QaTests.Removed_time_lab_route_is_404` |
| Second open shift for one employee (two tabs / two servers) (S1) | The DB refuses it: `UX_Shifts_OneOpenPerEmployee`, SQL error 2601 | `ShiftConstraintTests.S1_*` |
| Clock-out equal to (T11) or before (T10) clock-in (S5) | The DB refuses it: `CK_Shifts_ClockOutAfterClockIn`, SQL error 547; `Shift.Close` refuses it first | `ShiftConstraintTests.S5_*`, `PunchStampTests.Close_refuses_*` |
| Repeated October hour (T6), DST-start instant (T7), µs / 100 ns instants (S9) | Stored as a UTC instant + the offset as seen; round-trips exactly | `PunchRoundTripTests.S9_*` |
| Overnight (T3) / year-end (T8) shift: which work date? | The Zurich date of clock-in (2026-01-12; 2026-12-31), from tzdb, checked again in SQL | `PunchStampTests` · `CK_Shifts_ClockInZurichDate` |
| `/health/time` during graceful shutdown (stopping token cancelled, client still connected) (QA-10) | 503 with `canPunch: false`, `code: time.unavailable`; was 500 `server.error`. A client that disconnects still gets no body | `TrustedClockStep4QaTests.QA10_health_time_during_shutdown_is_503_not_500_server_error` |
| A provider throws instead of returning a result, seen from `/health/time` and the 503 body | Only the outcome code `error`, never exception text | `TrustedClockStep4QaTests.Health_time_shows_error_outcome_*`, `Punch_503_body_for_two_throwing_providers_has_no_exception_text` |
| Server process in another time zone or culture (E16) | No effect: NodaTime tzdb + invariant parsing. Full suite identical under Pacific/Kiritimati + th-TH, Pacific/Pago_Pago + ar-SA, Asia/Kathmandu + fa-IR | `HostEnvironmentOverrideTests` (env `CLOCKMANAGER_TEST_TZ` / `CLOCKMANAGER_TEST_CULTURE`), CI run with `TZ` set |
| 10 simultaneous Clock Ins, same employee, separate connections (S2) | `UX_Shifts_OneOpenPerEmployee`: exactly 1 row, the other 9 get SQL 2601 | `ShiftConcurrencyTests.S2_ten_parallel_clock_ins_*` (3 rounds) |
| Clock Out of shift A racing a new Clock In (tab A closes, tab B clocks in) | Never 2 open shifts: the clock-in either lands after the close or gets 2601; the clock-out always wins | `ShiftConcurrencyTests.Clock_out_racing_a_new_clock_in_never_leaves_two_open_shifts` (15 rounds) |
| Two Clock Outs on one open shift (double click / two tabs) (S10) | `RowVersion`: one wins, the other gets `DbUpdateConcurrencyException`, the first value stays | `ShiftConcurrencyTests.S10_two_parallel_clock_outs_*`, `S10_stale_clock_out_*` |
| Half-written Clock Out, either direction (S6) | `CK_Shifts_ClockOutComplete` refuses it, one test case per column; the optional cross-check pair may be null (D-009) | `ShiftIntegrityTests.S6_*` |
| UTC date stored instead of the Zurich work date (T3 overnight, T8 year end) | `CK_Shifts_*ZurichDate` refuses it | `ShiftIntegrityTests.T3_*`, `T8_*` |
| Cross-check provider stored with a NULL delta (QA-12) | `CK_Shifts_*CrossCheck` refuses it (SQL 547); the delta needs an explicit `IS NOT NULL`, since `NULL >= 0` is UNKNOWN and passes a CHECK | `ShiftIntegrityTests.QA12_*` |
| Blank provider or cross-check provider name in a stored punch (QA-13) | `CK_Shifts_*ProviderNotBlank` and `*CrossCheckedByNotBlank` refuse `''` and spaces (SQL 547); NULL cross-check (single source) stays allowed | `ShiftIntegrityTests.QA13_*` |
| Clock In while a shift is open / Clock Out with none (normal click) | 409 `shift.already_open` (body has `openShift`) / 409 `shift.not_open`, answered before any provider call (D-028) | `PunchApiTests.Clock_in_while_a_shift_is_open_*`, `Clock_out_without_an_open_shift_*` |
| 10 parallel Clock Ins over HTTP, same employee (P5) | All pass the state check, the unique index decides: exactly 1 × 201, 9 × 409 `shift.already_open` naming the winner's shift, 1 row | `PunchApiTests.P5_*` (3 rounds) |
| Two tabs Clock Out the same shift (P12) | `RowVersion`: 1 × 201, 1 × 409 `shift.not_open`; the first clock-out stays | `PunchApiTests.P12_*` (3 rounds) |
| Provider down / invalid / disagreeing during a punch (P9) | 503 with `time.unavailable` / `time.invalid_response` / `time.providers_disagree` and `providers`; no row, an open shift stays open | `PunchApiTests.P9_*` |
| Trusted time equal to or before the last punch (T10, T11, P11) | 503 `time.went_backwards`, nothing saved, Warning logged (D-030); applies to a clock-in before the previous clock-out too | `PunchRulesTests.P11_*`, `PunchApiTests.P11_*` |
| Another tab saves a punch at the same instant while ours waits for the provider (double click) | 409 of the lost race (`shift.already_open` + open shift / `shift.not_open`), not 503 `time.went_backwards`: the guard compares with a snapshot from before the call (D-030) | `PunchApiTests.Clock_in_with_the_same_time_as_*`, `Clock_out_with_the_same_time_as_*`, `Time_behind_a_punch_that_existed_before_the_call_is_still_503_*` |
| Punch body with a `time` field, unknown intent, wrong case, no body (P15) | 400 `request.invalid`, no provider call, nothing saved (D-029) | `PunchApiTests.P15_*` |
| Missing, non-canonical or all-zero `Idempotency-Key` | 400 `idempotency.key_missing`, no provider call | `PunchApiTests.P16_*` |
| Same request sent twice, e.g. the response was lost (P6) | The second answer is the first (status and body, byte for byte); no provider call, no new row (D-032) | `PunchIdempotencyTests.P6_*` |
| Replay while the provider is down | Still 201 from the stored record: a replay never asks the provider | `PunchIdempotencyTests.A_replay_works_while_the_provider_is_down_*` |
| Clock-in key replayed after the shift was closed / clock-out key replayed | The ORIGINAL answer (open shift without duration / the clock-out with its duration), not the shift's current state | `PunchIdempotencyTests.A_clock_in_key_still_replays_after_*` |
| Same key, other intent (P8) | 422 `idempotency.key_reused`, no provider call, nothing changes; the stored original still replays | `PunchIdempotencyTests.P8_*` |
| Same key used by two employees | Independent: each gets their own punch and their own replay | `PunchIdempotencyTests.The_same_key_used_by_two_employees_*` |
| Retry with the same key after a 503 or a 409 (P10) | Really retried: failures are not stored; 201 once the provider is back / the state allows it | `PunchIdempotencyTests.P10_*`, `A_409_is_not_stored_*` |
| Same key 10 times in parallel (P7), same clock-out key in parallel | 1 row, all answers identical: the losers replay the winner (D-034) | `PunchIdempotencyTests.P7_*` |
| Writing the idempotency record fails after the shift was written | One transaction: the shift is rolled back too, the answer is 500 and a retry punches normally (D-033) | `PunchIdempotencyTests.The_shift_and_its_record_are_one_transaction_*` |
| Idempotency table: duplicate (employee, key), bad intent / status / body / key / offset, deleting a referenced shift or employee | The database refuses it (SQL 2601 / 547) | `IdempotencyRecordSchemaTests` |
| `X-Dev-Employee` sent to Production (P17) / unknown or missing user / inactive employee (P18) | 401 `auth.unauthenticated` / 401 / 403 `employee.inactive`; no provider call (D-027) | `PunchApiTests.P17_*`, `Development_without_a_known_employee_is_401`, `P18_*` |
| Client disconnects while the punch waits for the provider (P19) | The provider wait is cancelled, nothing saved, no Warning/Error and no 503 logged | `PunchApiTests.P19_*` |
| Shift across DST start / end returned by the API (T4, T5, T6) | `durationSeconds` 25200 / 32400 / 2700 from UTC; each punch shows its own offset, e.g. `02:30+02:00` → `02:15+01:00` | `PunchRulesTests.P2_*`, `P3_*`, `PunchApiTests.Shift_across_DST_end_over_HTTP_*` |
| Overnight / New Year's Eve shift returned by the API (T3, T8) | `workDate` = Zurich date of clock-in (2026-01-12, 2026-12-31); 23:30Z is already the next day in Zurich | `PunchRulesTests.P4_*` |
| Long (17 h) / short (20 s) shift (P13, P14) | Flag `long_shift` / `short_shift` on the returned shift, strict bounds, from the UTC duration (D-031) | `PunchRulesTests.P13_*`, `P14_*`, `PunchApiTests.P13_P14_*` |
| Status with the provider down (P20), any `time.*` failure or an unexpected exception in the clock | 200 with the state, `serverNow: null`, no time-based flag, no error text in the body; only provider codes in the log (D-036) | `StatusApiTests.P20_*` |
| Open shift older than 16 h by the trusted now (P13, T9) / exactly 16 h / 15 h 59 | `forgotten_clock_out` at the top level / not flagged / not flagged; never auto-closed (D-036) | `ForgottenClockOutRuleTests`, `StatusApiTests.P13_*` |
| 16 h limit across the DST start (T4) | Measured in UTC: 17 h on the wall clock, exactly 16 h → not flagged | `ForgottenClockOutRuleTests.The_duration_is_taken_from_UTC_*`, `StatusApiTests.The_flag_is_measured_in_UTC_*` |
| Status "now" slightly before the open shift's clock-in (the cache is up to 10 s old and the employee just punched) | Never flagged; the UI adds `ageMs` to `serverNow.utc` (D-035) | `ForgottenClockOutRuleTests.P13_*` (06:59:58 case) |
| A punch right after a status call (D1) | The punch asks the provider afresh and records the NEW time; the cached status time is never a fallback, not even when the provider is down | `StatusApiTests.D1_*` |
| Many status requests at once / while the providers are down | One shared provider call; a failure is remembered for 1 s only (D-035) | `StatusApiTests.Parallel_status_calls_*`, `ServerNowProviderTests` |
| Several closed shifts: which is "last"? | The latest clock-out, not the latest clock-in (D-036) | `StatusApiTests.The_last_closed_shift_is_the_one_with_the_latest_clock_out_*` |
| Zurich time string when the offset has zero minutes | Always `+01:00` / `+02:00` (NodaTime's `ExtendedIso` would write `+01`) | `PunchRulesTests.Normal_day_shift_*` |
| Duplicate `intent` member in the JSON body | 400 `request.invalid` (`intent: "must appear only once"`), also for `Intent` vs `intent`, escaped names, three copies and duplicate unknown members; nothing saved, provider not asked (QA-17, handled, D-041) | `PunchQaBodyTests.Duplicate_members_are_rejected_*` |
| Error responses' media type | Every error is `application/problem+json` (400/401/403/409/415/422/503 and the framework 404/405); success stays `application/json` (D-039) | `PunchQaBodyTests.Error_responses_use_the_problem_json_media_type` (QA-18) |
| 400 for malformed JSON / wrong type / unknown member / missing body | Status and `code` stay `request.invalid`; `errors` is keyed by wire field (`intent`, `time`) or `body` and holds fixed sentences only, no CLR names, paths or positions (QA-19, handled, D-040) | `PunchQaBodyTests.Bad_body_400_does_not_expose_*`, `Unknown_field_name_echoed_*` |
| Handled refusal logging | The app logs 4xx at Information and 5xx at Warning; `AppExceptionMiddleware` takes `AppException` before the .NET 8 `ExceptionHandlerMiddleware`, so no Error per refusal; a real 500 still logs at Error | `PunchQaLogTests.A_handled_business_refusal_does_not_log_at_Error`, `ErrorLoggingTests` (QA-20, handled, D-038) |
| Trusted instant less than 100 ns after the last punch | The instant is floored to a 100 ns tick before the guard, so it compares as stored: < 1 tick later is 503 `time.went_backwards`, exactly 1 tick later is accepted and stored strictly later | `PunchQaGuardTests.Clock_out_less_than_one_database_tick_*` (QA-21, handled, D-042) |
| Year 0001 or 9999 from the clock | Refused as 503 `time.invalid_response` by the punch path itself (window [2026-01-01, 2100-01-01) UTC, fixed in code, shared with the sources); nothing saved, no exception text | `PunchQaGuardTests.An_absurd_instant_from_the_clock_*`, `The_plausible_window_boundaries_are_exact` (QA-22, handled, D-042) |
| One wrong far-future but in-range reading (e.g. 2099) | Accepted; later honest punches are 503 `went_backwards` until a manager correction. No local-clock cap (rule 1); the defence is the two-provider cross-check | `PunchQaGuardTests.A_far_future_but_in_range_instant_*` |
| Same UUID in other case or with whitespace | One key (parsed to a Guid, trimmed). Other spellings and two headers give 400 `idempotency.key_missing` | `PunchQaKeyTests.*` |
| Replay of a stored key after the employee was deactivated | The who-check comes first: 403 `employee.inactive`, not the stored answer; the stored shift is untouched | `PunchQaKeyTests.Replay_after_the_employee_was_deactivated_*` |
| Bob uses alice's key | Keys are scoped per employee: bob gets his normal result, no 422, no leak | `PunchQaKeyTests.Bob_using_alices_key_*` |
| Client-supplied time (extra JSON keys, `?time=`, `Date` / `X-Forwarded-*` headers) | Extra keys give 400; query and headers are ignored; the stored time equals the trusted clock | `PunchQaBodyTests.Bad_body_*`, `Query_string_time_and_header_dates_are_ignored_*` |
| Clock-out 1 ms after the clock-in | Accepted; 0 s duration, flagged `short_shift` | `PunchQaGuardTests.Clock_out_one_millisecond_after_*` |
| Race loser's trusted instant equals the winner's (second-precision provider, cached time) | Snapshot is read first (D-043): the loser gets 409 `already_open`/`not_open`, a same-key twin gets the stored 201 replay | `QA24_*` (10 runs stable) |
| A punch stalls between the monotonic guard and its INSERT while another tab punches in + out (QA-23) | The save transaction locks the employee row and re-runs the guard under it: the stalled punch gets 409 (or the same-key replay), never an overlapping shift (D-044) | `QA23_a_storm_never_leaves_overlapping_or_touching_shifts`, `A_punch_waits_for_the_employee_lock_*` |
| 409 `shift.already_open` when the other tab's shift is already closed again (QA-26) | Same code and status, but with no open shift the text says the shift changed in another tab, nothing was recorded, refresh and try again (it no longer says "clock out") | `PunchQaStatusTests.QA26_*` |
| Client abort while the DB write is in flight | Rolled back, no shift and no record, the same key works afterwards; EF logs an Error line | `Abort_while_the_INSERT_waits_on_the_database_rolls_back_and_the_same_key_then_works`; log noise accepted, see Known limitations (QA-25; `QA25_*` stays skipped) |
| Zurich boundaries over HTTP (repeated October hour, DST start, midnight, new year, 2027 / 2028 change dates) | Response, DB row and status agree; durations come from UTC; flags at exact thresholds | `PunchQaDstHttpTests` |
| 30 parallel clock-ins (same key / different keys), 80-request in/out storm, 20 employees in parallel | Never a 500, never two open shifts, every 201 matches a stored row, records == successes, no cross-talk or deadlock | `PunchQaConcurrencyTests` |
| Expired, tampered, wrong-key or bearer-header token | 401 `auth.unauthenticated`; the header is never read | `AuthApiTests` |
| Valid token of a deleted or deactivated employee | 401 for a deleted one, 403 `employee.inactive` for a deactivated one | `AuthApiTests` |
| Two signups with the same name at once | The unique index gives one 409 | `AuthApiTests` |
| Missing or short signing key outside Development | The host refuses to start | `AuthHostTests` |
| Not logged in and a bad body or no `Idempotency-Key` | 401 comes before 400 | `AuthApiTests` |
| Week with the spring clock change | Duration from UTC, so a shift across it is 7 h; the day is noted `dst_change` | `ShiftHistoryApiTests.Spring_dst_week_*` |
| Week with the autumn clock change | 9 h counted; a shift inside the repeated hour is 45 min with both offsets shown; the 25-hour day is noted | `ShiftHistoryApiTests.Autumn_dst_week_*` |
| Shift across New Year or Sunday night to Monday | Stays on the Zurich date and week of its clock-in; `endsNextDay` is true | `A_year_boundary_week_*`, `A_sunday_night_shift_*` |
| Open shift in the range | Listed, not counted; forgotten flag only with a trusted time | `An_open_shift_is_listed_*`, `An_old_open_shift_*` |
| `from` or `to` missing, empty, wrong format, 32 days | 400 `request.invalid` with a fixed message; 31 days is accepted | `Bad_dates_are_a_400_*` |
| Correction typed in the skipped March hour (02:00–02:59) | Refused in the form ("this time doesn't exist in Zurich"); the server refuses it too | `manager.test.js` |
| Correction typed in the repeated October hour | The manager must choose the first (UTC+2) or second (UTC+1) occurrence | `manager.test.js` |
| A correction while the employee clocks out (either order) | One wins; the other gets 409 (`shift.not_open` or `correction.stale`); never a 500 | `ManagerApiTests` race tests |
| Moving a clock-in to the previous day | The work date moves with it | `ManagerApiTests.Moving_a_clock_in_to_the_previous_day_*` |
| Closing a forgotten shift, then the employee clocks in | Works: the shift is no longer open | `ManagerApiTests.Closing_a_forgotten_shift_*` |
| Closing a shift that may still be running | 409 `correction.shift_still_running` | `ManagerApiTests` |
| A token still says Manager after the role was changed in the database | The database role wins: 403 | `ManagerApiTests.The_database_role_wins_*` |
| Signup/login with a taken name in another case, a short password, extra fields (`role`) | 409 / 400 with fixed messages; the role always comes from the server | `AuthApiTests` |
| Wrong password, unknown user, user without a password | One identical 401; an inactive user gets 403 only after a correct password | `AuthApiTests` |
| Week with a clock change, overnight shift, open shift in History | Hours from UTC; shift stays on its clock-in day; open shift listed, not counted | `ShiftHistoryApiTests`, `ShiftHistoryBuilderTests` |
| Manager types a time in the skipped March hour or the repeated October hour | Skipped: refused. Repeated: only with an explicit offset, never guessed | `CorrectionInputTests`, `manager.test.js` |
| Correction that overlaps, touches another shift, is in the future or changes nothing | 409 `correction.overlap` / 422 `correction.in_future` / 422 `correction.unchanged` | `ManagerApiTests` |
| Punch answer lost, then "Try again" | Must replay the SAME intent and key, never the opposite punch | `punchMachine.qa.test.js` (QA-27, D-077) |
| Manager saves an untouched correction form on a punch that has seconds | Must be refused as "already the current time" | `correction.qa.test.js` (QA-28) |
| Keyboard focus on the main punch button | Needs a visible focus style on the blue block | manual check (QA-29: white focus ring on the blue block) |
| Time text without an offset reaches the Zurich formatter | Must be refused, never read in the browser zone | `zurich.qa.test.js` (QA-30) |
| `crypto.randomUUID` missing (plain http on a host name) | The punch must show a message, not do nothing | `punchMachine.qa.test.js` (QA-31) |
| Login/signup field errors | Should move focus or be announced | manual check (QA-33: focus moves to the first invalid field) |
| Long unbroken names or reasons | Must wrap, not scroll the page | manual check (QA-34: text wraps) |
| More than 100 problems in the list | The page must say it shows the newest 100 | manual check (QA-35: "Showing the newest 100") |
| Body charset `utf-16` or an empty `charset=` | 415 `request.unsupported_media_type` (D-079) | `AuthQaInputTests.QA42_*` |
| A stored password hash that is not base64 | Normal 401 like any failed login (D-081) | `AuthQaInputTests.QA43_*` |
| A token for a deleted or missing employee on manager endpoints | 401 `auth.unauthenticated`, same as `/api/me/*` (D-081) | `AuthQaTokenTests.QA45_*` |
| Duplicate JSON members in signup and login | 400 with a fixed message, like the punch body (D-080) | `AuthQaInputTests.QA41_*` |
| An IPv6 client rotating addresses inside one /64 | One shared login budget per /64 (D-080) | `AuthQaRateLimitTests.QA46_*` |
| Signup and login request size | Limited to 16 KB on signup, login, punch and correction (D-080) | `AuthQaHostTests.QA48_*` |
| JSON responses without `X-Content-Type-Options: nosniff` | Every response has it (D-080) | `AuthQaInputTests.QA44_*` |
| 20 parallel signups with one name | One 201, 19 clean 409s; the losers log an EF Error line (QA-47, accepted like QA-25) | `AuthQaConcurrencyTests` |
| `X-Forwarded-For`, `Forwarded`, `X-Real-IP` | Ignored: the connection address picks the rate-limit partition | `AuthQaRateLimitTests.Forwarding_headers_*` |
| Forged token naming a victim | 401, and it never spends the victim's punch budget | `AuthQaRateLimitTests.A_forged_token_*` |
| `alg:none`, other algorithm, payload swap, wrong issuer/audience, no `exp`, future `nbf` | 401 on every endpoint kind | `AuthQaTokenTests.Forged_stale_and_odd_tokens_*` |
| Token in the `Authorization` header, the query string or another cookie name | Ignored: 401 | `AuthQaTokenTests.A_good_token_outside_the_cookie_*` |
| Employee or anonymous asking about a missing or an existing id | Identical 403 / 401: existence is not revealed | `ManagerQaAccessTests` |
| Cross-site POST (text, form, multipart, XML) with a valid cookie | 415 and nothing changes; no CORS headers anywhere | `AuthQaBrowserTests` |
| Swagger outside Development, and the removed time lab route | 404 | `AuthQaHostTests` |
| Seed in Production or Staging, or a blank `Seed:ManagerPassword` | Nothing created / nobody gets a hash | `AuthQaSeedTests` |
| Clock page while the time providers are down (`serverNow: null`) | No clock and no time-of-day greeting; "Server time unavailable"; punching still allowed, the server decides (D-084) | `serverClock.test.js` (cleared on a sync without time) |
| Clock page right after a punch (`serverNow` cleared until the refresh) | The last server anchor is kept, so the clock does not blink (D-084) | `serverClock.test.js` (kept after a punch) |
| Clock page on a DST night (T6/T7) | Clock shows the offset ("UTC+1" / "UTC+2"); 03:00 at the March jump, never 02:00 | `serverClock.test.js` T6, T7 |
| "This week" around New Year (T8) | Monday–Sunday by date strings: 28 Dec – 3 Jan | `views.test.js` `weekOf` |

## Not done (out of scope)

- **No production deployment.** Nothing serves `frontend/dist`; a real server needs a front web server with HTTPS (README "Production").
- **Out of scope for this assignment:** payroll, breaks, geofencing, SSO, offline punching, multi-tenant use and translations.
- **No end-to-end browser test (Playwright).** Decided in Phase 8: no browser tool for now, so the screens were never opened by the author; the README has the manual list.
- **No interactive component or browser tests for the frontend.** The logic is tested (Vitest, D-060) and, since plan 10, `views.smoke.test.jsx` renders the main views to HTML with real-shaped data (no DOM, no clicks). The screens are otherwise checked through the mock scenarios and the manual list in the README.
- **Mockup parts the API has no data for (redesign, plan 10).** The Team page's per-person line "43 h 49 min this week" (the employees endpoint has status, since and flags only; the line shows those instead) and the problems table's "Show all" link (all problems, at most 100, are always listed; the chips only filter). Adding weekly hours would need `weekSeconds` on `GET /api/manager/employees`.

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
| 2026-11-17 | `CK_Shifts_*CrossCheck` in the Phase 2 schema | `NULL >= 0` is UNKNOWN, so a cross-check provider with no delta passed the CHECK. Found by the qa agent (QA-12) | `AND [Delta] IS NOT NULL`, migration `FixCrossCheckNullDelta`; regression test `QA12_*` | `9d06246` |
| 2026-11-17 | Provider columns in the Phase 2 schema were only `NOT NULL` | `''` or spaces passed, so a punch could name no time source, which `PunchStamp.From` refuses. Found by the qa agent (QA-13) | `LEN(LTRIM(RTRIM(...))) > 0` CHECKs, migration `ProviderNamesNotBlank`; regression tests `QA13_*` | `cc9c0c9` |
| 2026-11-17 | Commit subjects up to ~85 chars with internal codes; bodies of 10+ lines with column and test names | The user couldn't understand the history from the log | CLAUDE.md §6.1: subject ≤ ~50 chars in plain words, body 1–3 lines + `Refs:`; details live here and in the worklog. Pushed history is not rewritten (rule 6) | `4554d89` |
| 2026-11-18 | Phase 1 exception handling used `UseExceptionHandler` + `IExceptionHandler` and assumed our handler was the only logger | On .NET 8 the framework middleware logs every exception at Error before our handler runs, so every double click or provider outage logged an Error. Found by the qa agent (QA-20) | `AppExceptionMiddleware` handles `AppException` first (D-038); the QA-20 test is un-skipped | `d41361b` |
| 2026-11-18 | qa slice A tests used culture-sensitive `Assert.DoesNotContain(string, string)` and a shift-id substring check | Failed only in the E16 run (`th-TH`): `"   at "` matched a normal 405 body, and id `5,` matched `"status":405,`. CI was red for two commits because the push was not followed by a CI check | Ordinal comparisons; parsed-JSON assertion; wait for CI after every push | `133aa3b` |
| 2026-11-18 | Step 1 put `[Produces("application/json")]` on the punch controller (and later the status controller) | It overrode the media type of every error, so ProblemDetails went out as `application/json`, not RFC 7807 `application/problem+json`. Found by the qa agent (QA-18) | Removed the attribute; media types are declared per `[ProducesResponseType]` (D-039); the QA-18 test is un-skipped and covers every error status | `5818a6e` |
| 2026-11-18 | Step 1 left the framework's default 400 text, assuming `[ApiController]` validation output was safe to send | The JSON reader's messages echoed CLR type names, parser positions and a "request field is required" noise entry. Found by the qa agent (QA-19) | `InvalidRequestResponse` builds fixed messages in one place (D-040); the QA-19 test is un-skipped and covers invalid JSON, wrong types, unknown member, missing body, empty object | `b1e9928` |
| 2026-11-18 | Step 1 read the punch body with the default JSON settings, and a qa-slice test even documented "the last duplicate `intent` wins" as acceptable | A duplicate member ran the LAST value while a gateway reading the first would see another request; intent decides in/out. Found by the qa agent (QA-17) | `PunchRequestJsonConverter` rejects duplicates with 400 (D-041); the QA-17 test is un-skipped and extended, the "last wins" test is replaced by the rejection | `82ca5e4` |
| 2026-11-18 | Step 2 compared and saved the trusted instant as the clock returned it, and left the range to the time sources | Nanoseconds vs 100 ns database ticks: a sub-tick-later instant was stored equal and the CHECK gave a 500; an out-of-range instant from another clock would give a 500 too. Found by the qa agent (QA-21, QA-22) | `TrustedInstantRules.Normalize` after the clock call (D-042); both tests un-skipped, shown to fail without the fix | `a1d3049` |
| 2026-11-18 | Step 2 read the open shift first and the last-punch snapshot after it, and the replay catch only handled 409s | The race loser could see the winner's instant in its "before" snapshot and answer 503 `time.went_backwards` instead of 409 or the same-key replay (failed about 3 of 4 storm runs). Found by the qa agent (QA-24, QA-15) | Snapshot first, then state check; the replay catch also covers `went_backwards` (D-043); four `QA24_*` tests un-skipped, shown to fail without the fix | `cfca4b6` |
| 2026-11-18 | Step 2 evaluated the monotonic guard before the write and treated it as protection for the write | The guard was not atomic with the INSERT/UPDATE: a stalled punch could be stored with an instant not later than a newer punch of the same employee (overlapping or touching shifts). Found by the qa agent (QA-23), a skipped storm test that failed in most runs | The save transaction takes an update lock on the employee row and re-runs the guard under it (D-044); the test is un-skipped, stable over 10 runs, fails without the fix; two deterministic lock tests added | `63e25a8` |
| 2026-11-18 | Step 2 put the same "A shift is already open. Clock out before clocking in again." text on every clock-in 409 | When another tab had opened AND closed a shift meanwhile, nothing was open but the text told the employee to clock out. Found by qa slice B (QA-26) | Fixed wording when there is no open shift; the code stays `shift.already_open`. Test un-skipped | `c3781df` |
| 2026-11-18 | Claude kept the long commit subjects and bodies after the user shortened the rule | The rule change (4554d89) landed mid-step; Claude read CLAUDE.md only at the start of the session. The 14 commits from a878363 to c3781df break the new rule (not rewritten, rule 6) | Rule followed from now on: subject up to about 50 characters, plain words, 1–3 line body; CLAUDE.md is re-read before each commit | `c3781df` |
| 2026-11-18 | JSON endpoints trusted the content-type charset; `PasswordService` expected a valid stored hash; the rate limiter keyed on the full IP text; signup, login, punch and correction bodies had no size limit; no `nosniff` header; the duplicate-field rule covered only the punch body | A utf-16 or empty charset gave a 500; a corrupt hash gave a 500 on login; one IPv6 /64 got a fresh budget per address; huge bodies were parsed before validation. Found by the login QA (QA-41..44, QA-46, QA-48) | Charset middleware, safe hash check, /64 partition, 16 KB limits, nosniff, shared duplicate scan | `2bc6f66` |
| 2026-11-18 | The manager policy answered 403 for a token naming a missing employee | Inconsistent with 401 on `/api/me/*`, so the UI would not send the user to login (QA-45) | The policy answers 401 | `6f28d4b` |
| 2026-11-18 | README told people to run `dotnet user-secrets`, but the project had no secrets id | Every such command failed, so the manager login and the other database options could not be set up from the docs (F1) | Added `UserSecretsId` and a test | `9f28b2d` |
| 2026-11-18 | The login rate-limit test used a 1 s window, shorter than four password hashes in a Debug build | It failed on every run on the dev machine once the suite ran alone | Window is 5 s, wait is 6.5 s | `b23618b` |
| 2026-11-18 | A correction body with a repeated field was accepted (last one won) | Login and punches already refused this; the audit row could record a value the manager never saw in the request | Same duplicate guard on the correction body | `22bf1c9` |
| 2026-11-18 | The problems list indexed the first flag of every row | One row with no flag would crash the whole manager list | Such a row shows as "unknown" | `3783e19` |
| 2026-11-18 | CLAUDE.md listed the stack twice | Looked like a bad merge in a file reviewers read | One line kept | `7517ef0` |
| 2026-11-18 | A lint warning was left in the login component although CLAUDE.md says lint must be clean | Found by the clean-clone run in Phase 9 | The first load no longer sets state in an effect | `dc5a0a6` |
| 2026-11-18 | A skill example pointed at a plan file and a test name that do not exist | Found by the Phase 9 reference check | Points at the real plan and test | `b67115d` |

## Known limitations

- **The first provider call after a restart or an idle minute can still be slow (QA-11, mostly fixed by D-037).** A cold
  DNS + TCP + TLS setup costs ~0.8–0.9 s per provider (vs 0.13–0.22 s warm); right after startup we measured ~2.5 s
  end to end. Now fixed: a background warm-up opens one connection per provider after startup, and the pool keeps it
  (300 s idle timeout, HTTP/2 keep-alive PINGs, 300 s lifetime). Remains: a punch that arrives before the warm-up has
  finished, after the connection was closed (server-side idle close, lifetime, network change), or during a DNS/TLS
  outage still pays the cold cost and is flagged `LowPrecision`, becomes single-source, or is a 503 (fail closed).
  The improvement was not measured against the real network.

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
- **Idempotency records are never cleaned up.** Every successful punch adds one row to `IdempotencyRecords` (a key and a
  small JSON body) and nothing deletes it, so the table grows by about two rows per employee per working day. At this
  scale that is harmless, and keeping the rows means a very late retry still replays instead of failing with 409.
  Not built: a retention job (e.g. delete records older than 30 days) and a size limit; it would need a hosted
  service or a scheduled SQL job and a decision on how long a key must stay valid.
- **Idempotency records are only checked by the application, not by every DB rule (db review of step 2, QA-14, QA-16).**
  `ShiftId` is informational: replay reads the stored `ResponseBody`, looked up by `(EmployeeId, Key)`, so the database
  does not check that the shift belongs to the employee or that `Intent` matches the shift state. A composite FK was
  rejected because it needs an extra unique index on the hot `Shifts` table for no read-path benefit. The `Intent` CHECK
  follows the column collation (case-insensitive); the only writer is `PunchService.SaveShiftAndRecordAsync`, which
  writes the exact wire values. That method uses a manual transaction: if `EnableRetryOnFailure` is ever turned on, it
  must be wrapped in `CreateExecutionStrategy().ExecuteAsync`.
- **A same-key twin can answer 503 once instead of replaying (QA-15): resolved by D-043.** The snapshot is read before the state check and `went_backwards` is replayed from the stored record.
- **One consistently wrong far-future reading inside [2026, 2100) is accepted (qa slice A).** If both providers agree on a
  wrong future time (e.g. 2099-12-31), the punch is saved and every later honest punch is 503 `time.went_backwards` until
  a manager corrects it. Rule 1 forbids bounding it with the local clock, so the defences are the two-provider
  cross-check and each source's year window. Recovery needs the manager correction path with an audit trail (Phase 7).
- **A client that disconnects during the database write logs one Error line (QA-25).** The data is correct (rolled back, no
  shift, no record, the same key works afterwards). EF Core logs "An error occurred using a transaction" for the cancelled
  transaction. Silencing that category would also hide genuine transaction errors, so it is accepted. The skipped test
  `PunchQaCancelTests.QA25_*` documents it.
- **No token revocation and no password reset (Phase 4).** Logout only clears the cookie, so a stolen token works until it expires (8 h); a deactivated employee is still blocked because the employee is loaded on every request. Login and signup are rate limited (D-054).
- **Rate limits are in memory, per server, and per IP for login (D-054).** Users behind one NAT share the budget, behind a proxy without forwarded headers everyone shares the proxy's IP, and there is no per-account lockout or captcha.
- **Replaying an old punch after a correction (D-032, D-073).** A retried punch with an old `Idempotency-Key` returns the answer stored at the time, not the corrected values.
- **`npm audit` reports a high advisory in `picomatch` (build tooling only).** GHSA-fc5q-39xc-vjpf (ReDoS in glob matching); the installed 4.0.7 is the latest release, so no fix exists and the suggested downgrades are worse. It is not in the production bundle and cannot be reached by users. Re-check later.
- **Screen details that need a real browser or screen reader to judge (frontend QA).** (a) The elapsed counter adds a monotonic stopwatch to the last server time, so after a laptop sleep it can lag until the next focus refresh (QA-37). (b) In Chromium a second Escape press can close the correction dialog even while a save is running (QA-38). (c) A result message that appears together with its live region may not be read out by every screen reader (QA-39). (d) No skip link, the page title is the same on every route, and minutes are floored per shift and per total, so rows can differ from the total by a minute. All are low impact; nobody has checked them in a real browser yet.
- **The built frontend is not served by the API (QA-32).** Documented in the README ("Production"). Also, the app lives at the root of one origin: the cookie is `SameSite=Lax` and the files use absolute paths, so serving under a sub-path is not supported.
- **Login details found by QA (pinned by tests).** Login matches names with the database collation, so a trailing space or full-width letters can log in (signup can't create such names, so no clash). Logout is a plain POST, so another site can sign a user out (nuisance only; the request carries no cookie because of `SameSite=Lax`). The server doesn't check `Origin`: it relies on no CORS plus `Lax`, so adding CORS later needs a review (D-049). The password minimum counts UTF-16 units, so 4 emoji pass.
- **Left open by the final Phase 8 review (all low, none fixed).** (a) A correction replaces the stored stamp (provider, cross-check, offset); the audit row keeps the old UTC instant, not the old provider details, and "insert-only" is by code, not a database permission. (b) The cookie's `Secure` flag follows the request, so behind a TLS proxy the app needs forwarded headers (README). (c) "Try again" on a correction after a lost answer has no idempotency key: it ends as a 409 "changed meanwhile" after the first save worked. (d) Nothing in the app can switch an employee back on, although messages say to ask a manager. (e) Logout ignores a failed request, so the cookie lives to its expiry. (f) The Team and shift pages reuse the History load-error wording. (g) The Problems query is not index-friendly (it reads all shifts) and the two copies of the flag rules (SQL and C#) must be changed together. (h) The range-parsing code is copied in two controllers.
- **Whole-stack run on the real database and providers (Phase 8 C).** Signup, clock in, a second session getting 409, clock out, same-key replay, History and logout all gave the expected answers. With both providers unreachable a punch gives 503 `time.unavailable` after about 2 s and records nothing; status and History keep working with no server time. Not run through the Vite proxy or by a person in a browser.
- **Phase 9 checks and a nit.** From a fresh clone and an empty database the README steps worked (SQL Express path only; LocalDB and Docker were not run, LocalDB is not installed on the dev machine). `npm audit` (all packages) and `dotnet list package --vulnerable` (API and tests) found nothing, and no secrets are in the tracked files or in the history. Git prints "LF will be replaced by CRLF" warnings on Windows because there is no `.gitattributes`; it was left alone on purpose, since renormalizing would rewrite many files in one big diff.
