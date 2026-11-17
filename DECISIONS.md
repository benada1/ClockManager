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

### D-026 — CI runs the integration tests against a SQL Server 2022 container with a per-run password
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** ci
- **Options:** (a) keep skipping `Category=Integration` in CI · (b) a `services:` container with the sa password in a GitHub secret · (c) a `services:` container with a fixed password in the YAML · (d) `docker run` in a step with a password generated by `openssl rand` in the run.
- **Decision:** (d). The password is masked (`::add-mask::`) and passed to the tests via `CLOCKMANAGER_TEST_SQL`; a wait loop polls `sqlcmd` until the server accepts logins. Both test runs (normal and the E16 exotic-time-zone run) now include the integration tests, and `CI=true` turns an unreachable server into a failure, not a skip.
- **Why:** The constraint and race tests are the proof of CLAUDE.md rule 2; they must run on every push. (d) stores no secret anywhere (CLAUDE.md rule 3), and a `services:` container can't take a value computed during the run. (c) would put a password in git, even a throwaway one.
- **Trade-offs:** About 20–40 s extra per run for the image pull and boot (partly hidden behind restore/build). CI uses SQL Server 2022 on Linux, the dev machine SQL Server 2025 Express on Windows; we only use long-standing features, and CI is the referee (plan 02 §8).
- **Refs:** `.github/workflows/ci.yml` · `TestSqlServer.cs` · D-024

### D-027 — Phase 3 "who is punching": a Development-only `X-Dev-Employee` header behind `ICurrentEmployee`
- **Date:** 2026-11-17 · **Status:** Accepted (temporary, replaced in Phase 4) · **Area:** backend
- **Context:** Punches need an employee, but login is Phase 4. The user chose "punch first, login later" (plan 03 §3).
- **Options:** (a) build cookie login now · (b) `employeeId` in the punch body · (c) a dev-only header read by an `ICurrentEmployee` implementation registered only in Development.
- **Decision:** (c). `X-Dev-Employee: <username>` loads the employee from the DB. Missing, blank, repeated or unknown → 401 `auth.unauthenticated`; `IsActive = false` → 403 `employee.inactive`. Outside Development the registered implementation always returns "nobody", so every punch is a 401 and the header is never read.
- **Why:** `PunchService` depends only on `ICurrentEmployee`, so Phase 4 swaps the implementation without touching it. (b) would make "who" part of the client's payload, the kind of trust rule 2 forbids, and would leak into the contract.
- **Trade-offs:** In Development anyone can act as anyone. Acceptable for a local dev stand-in; P17 proves Production ignores the header.
- **Refs:** plans/03-punch-api.md §3 · `Infrastructure/Identity/DevHeaderCurrentEmployee.cs` · `PunchApiTests.P17_Production_ignores_X_Dev_Employee_and_answers_401`, `P18_*`

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
| Duplicate `intent` member in the JSON body | Last member wins (System.Text.Json default) and the stored idempotency Intent matches it; ambiguous for a gateway that reads the first | `PunchQaBodyTests.Duplicate_intent_members_*` (QA-17, open) |
| Error responses' media type | `application/json`, not `application/problem+json` (the `[Produces]` filter); the body shape is right | `PunchQaBodyTests.Error_responses_use_the_problem_json_media_type` (QA-18, open) |
| 400 for malformed JSON | Status and `code` are stable, but `errors` echoes System.Text.Json messages and CLR type names | `PunchQaBodyTests.Bad_body_400_does_not_expose_*` (QA-19, open) |
| Handled refusal logging | The app logs 4xx at Information and 5xx at Warning; `AppExceptionMiddleware` takes `AppException` before the .NET 8 `ExceptionHandlerMiddleware`, so no Error per refusal; a real 500 still logs at Error | `PunchQaLogTests.A_handled_business_refusal_does_not_log_at_Error`, `ErrorLoggingTests` (QA-20, handled, D-038) |
| Trusted instant less than 100 ns after the last punch | Passes the ns guard, is stored equal, and the DB CHECK fails: 500. Unreachable with millisecond providers | `PunchQaGuardTests.Clock_out_less_than_one_database_tick_*` (QA-21, open) |
| Year 0001 or 9999 from the clock | 500 if a clock layer ever let it through; the sources reject years outside [2026, 2100) | `PunchQaGuardTests.An_absurd_instant_from_the_clock_is_never_a_500` (QA-22, open) |
| One wrong far-future but in-range reading (e.g. 2099) | Accepted; later honest punches are 503 `went_backwards` until a manager correction. No local-clock cap (rule 1); the defence is the two-provider cross-check | `PunchQaGuardTests.A_far_future_but_in_range_instant_*` |
| Same UUID in other case or with whitespace | One key (parsed to a Guid, trimmed). Other spellings and two headers give 400 `idempotency.key_missing` | `PunchQaKeyTests.*` |
| Replay of a stored key after the employee was deactivated | The who-check comes first: 403 `employee.inactive`, not the stored answer; the stored shift is untouched | `PunchQaKeyTests.Replay_after_the_employee_was_deactivated_*` |
| Bob uses alice's key | Keys are scoped per employee: bob gets his normal result, no 422, no leak | `PunchQaKeyTests.Bob_using_alices_key_*` |
| Client-supplied time (extra JSON keys, `?time=`, `Date` / `X-Forwarded-*` headers) | Extra keys give 400; query and headers are ignored; the stored time equals the trusted clock | `PunchQaBodyTests.Bad_body_*`, `Query_string_time_and_header_dates_are_ignored_*` |
| Clock-out 1 ms after the clock-in | Accepted; 0 s duration, flagged `short_shift` | `PunchQaGuardTests.Clock_out_one_millisecond_after_*` |

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
| 2026-11-17 | `CK_Shifts_*CrossCheck` in the Phase 2 schema | `NULL >= 0` is UNKNOWN, so a cross-check provider with no delta passed the CHECK. Found by the qa agent (QA-12) | `AND [Delta] IS NOT NULL`, migration `FixCrossCheckNullDelta`; regression test `QA12_*` | `9d06246` |
| 2026-11-17 | Provider columns in the Phase 2 schema were only `NOT NULL` | `''` or spaces passed, so a punch could name no time source, which `PunchStamp.From` refuses. Found by the qa agent (QA-13) | `LEN(LTRIM(RTRIM(...))) > 0` CHECKs, migration `ProviderNamesNotBlank`; regression tests `QA13_*` | `cc9c0c9` |
| 2026-11-17 | Commit subjects up to ~85 chars with internal codes; bodies of 10+ lines with column and test names | The user couldn't understand the history from the log | CLAUDE.md §6.1: subject ≤ ~50 chars in plain words, body 1–3 lines + `Refs:`; details live here and in the worklog. Pushed history is not rewritten (rule 6) | this commit |
| 2026-11-18 | Phase 1 exception handling used `UseExceptionHandler` + `IExceptionHandler` and assumed our handler was the only logger | On .NET 8 the framework middleware logs every exception at Error before our handler runs, so every double click or provider outage logged an Error. Found by the qa agent (QA-20) | `AppExceptionMiddleware` handles `AppException` first (D-038); the QA-20 test is un-skipped | `this commit` |
| 2026-11-18 | qa slice A tests used culture-sensitive `Assert.DoesNotContain(string, string)` and a shift-id substring check | Failed only in the E16 run (`th-TH`): `"   at "` matched a normal 405 body, and id `5,` matched `"status":405,`. CI was red for two commits because the push was not followed by a CI check | Ordinal comparisons; parsed-JSON assertion; wait for CI after every push | this commit |

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
- **A same-key twin can answer 503 once instead of replaying (QA-15).** In a very narrow window (twin A commits between
  twin B's state check and B's last-punch read, and B's trusted time is not later than A's), B gets 503
  `time.went_backwards`. Nothing is wrong at rest: the client retries with the same key and gets A's stored answer.
- **One consistently wrong far-future reading inside [2026, 2100) is accepted (qa slice A).** If both providers agree on a
  wrong future time (e.g. 2099-12-31), the punch is saved and every later honest punch is 503 `time.went_backwards` until
  a manager corrects it. Rule 1 forbids bounding it with the local clock, so the defences are the two-provider
  cross-check and each source's year window. Recovery needs the manager correction path with an audit trail (Phase 7).
