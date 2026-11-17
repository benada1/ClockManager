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

## Edge cases

| Scenario | Handling | Covered by |
|---|---|---|

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
| 2026-11-17 | Committed one file per tiny change, without showing each commit first | Too granular, and the user didn't review each commit before it landed | Commit convention in CLAUDE.md §6.1: one commit per task, user reviews every commit | this commit |

## Known limitations

- **Generic error codes come from the HTTP status.** Errors produced only by a status (`request.not_found`, `request.method_not_allowed`, ...) get a code mapped from the status. Specific codes come only from `AppException`.
