# Claude Code worklog

Where Claude Code helped, and where its output had to be corrected. One entry per correction.

---

## 2026-11-17 — Master plan did not assign work to agents

- **Asked:** A step-by-step work plan and todo list for the whole app (`plans/00-master-plan.md`).
- **What went wrong:** The plan listed phases and tasks, but never said which agent
  (`backend`, `frontend`, `db`, `qa`) owns each phase. The agents from CLAUDE.md §7 were only
  mentioned when they get created (Phase 0) and in the QA pass (Phase 8). The user asked where
  the backend agent is actually used.
- **Fix:** Added a "Who builds what" table to §5: a lead agent, supporting agents and the hand-off
  contract for each phase, with the main session as coordinator. Also wrote down the flow for
  features that span areas (db → qa failing tests → backend → `/verify` → commit).
- **Lesson:** When CLAUDE.md defines agent roles, every plan should name an owner for each step.

## 2026-11-17 — Master plan picked a test library with a commercial license

- **Asked:** Execute Phase 0 of `plans/00-master-plan.md` (test project with "xUnit, FluentAssertions").
- **What went wrong:** The plan (written by Claude) named FluentAssertions without checking its
  license. Since v8 it needs a paid commercial license, and the plan didn't mention it.
- **Fix:** Caught while starting Phase 0, before the package was added. Used plain xUnit `Assert`
  instead and recorded the choice as D-002. The plan item was updated.
- **Lesson:** Check the license and maintenance status of each dependency before putting it in a plan.

## 2026-11-17 — Clock guard was regex-only

- **Asked:** Phase 0 "forbidden-clock check: a test/analyzer".
- **What went wrong:** The existing `verify.ps1` guard is a text regex. It misses
  `using static System.DateTime;` + `Now`, and it only runs when someone remembers to run `verify`.
- **Fix:** Added Roslyn `BannedApiAnalyzers` (RS0030 as an error) through `backend/Directory.Build.props`.
  Checked it by adding a probe file with `DateTime.UtcNow`: the build failed. Removed the probe and the
  build was green again. The regex guard stays for the frontend, and CI runs both (D-001).

## 2026-11-17 — Commits were too granular and not reviewed one by one

- **Asked:** Execute Phase 0 and commit the work.
- **What went wrong:** Claude split the work into many small commits (one for a single verify flag,
  a separate commit for a one-paragraph plan tweak) and committed after only a general approval.
  The user wants each commit to be a whole task that they review before it lands. Commits that are
  too small also make it look like nobody was steering.
- **Fix:** Added a commit convention to CLAUDE.md §6.1 (one commit per task, the user reviews every
  commit, corrections as separate `fix:` commits, no rewriting pushed history). Pushed history was **not**
  rewritten: the commits already on `main` stay as they are.

## 2026-11-17 — Provider name came from config instead of the provider (QA-03)

- **Asked:** Phase 1 step 1, the time.now provider (backend agent).
- **What went wrong:** `TimeNowSource` set its `Name` from `TrustedClock:Primary:Name`. If an operator
  swapped Primary and Secondary, every time.now reading would be stored as coming from "timeapi.io": a
  wrong audit trail. The backend agent flagged it as an open question, but it still went into commit `419a757`.
- **Found by:** the qa agent while writing step-2 tests (the tests were written before the step-2 code).
- **Fix:** A fixed `ProviderName` constant. The config slots only choose roles. The regression test
  failed before the fix (`Expected "time.now", Actual "timeapi.io"`) and passes after it. Separate `fix:` commit.

## 2026-11-17 — Startup-validation tests were racy (flaky test)

- **Asked:** Phase 1 steps 1–2 (backend agent): tests that the host refuses to start with bad options.
- **What went wrong:** One full-suite run failed once and then passed 31 times. The main session couldn't
  explain it and left it open for qa rather than calling it fixed. Under CPU load the qa agent got 23 failures in 24
  runs. Cause: with minimal hosting, the host disposes itself after `ValidateOnStart` throws, and
  `WebApplicationFactory` could then throw `ObjectDisposedException` instead of the validation error.
- **Fix:** A shared `HostStartup` helper also reads the host's "Hosting failed to start" log (D-018). 21 stress runs
  were clean afterwards. Production code was never wrong; the tests were.

## 2026-11-17 — Cross-check CHECK constraint let a NULL delta through (QA-12)

- **Asked:** Phase 2 step 1 (db agent): `CK_Shifts_*CrossCheck`, "cross-check provider and delta are both set or both null".
- **What went wrong:** The second branch was `[By] IS NOT NULL AND [Delta] >= 0`. With the delta NULL, `NULL >= 0` is
  UNKNOWN, and SQL Server only rejects a row when a CHECK is FALSE. So "cross-checked by timeapi.io, delta unknown" could
  be stored, although the code comment and `PunchStamp.From` both say it must be refused. The step-1 tests only tried
  "delta set, provider null", not the other direction.
- **Found by:** the qa agent in Phase 2 step 2 (raw-SQL row writer, one column changed per test).
- **Fix:** `AND [Delta] IS NOT NULL` in the CHECK, new migration `FixCrossCheckNullDelta`. The regression test
  `QA12_cross_checked_by_a_provider_with_a_NULL_delta_violates_the_cross_check` failed before (row stored) and passes after.
  Lesson recorded for later constraints: in a CHECK, every comparison on a nullable column needs an explicit `IS NOT NULL`.

## 2026-11-17 — Blank provider names were accepted by the database (QA-13)

- **Asked:** Phase 2 step 1 (db agent): the punch evidence columns.
- **What went wrong:** `{punch}_Provider` was only `NOT NULL`, so `''` or `'   '` could be stored, and the same for
  `CrossCheckedBy`. `PunchStamp.From` refuses a blank name, but CLAUDE.md rule 2 says the database must refuse it too.
  A punch that names no time source can't be audited against rule 1.
- **Found by:** the qa agent in Phase 2 step 2 (provider). The main session added the same test for `CrossCheckedBy`.
- **Fix:** `LEN(LTRIM(RTRIM(col))) > 0` CHECKs for both punches, migration `ProviderNamesNotBlank`. The 6 `QA13_*` cases
  failed before the migration and pass after it.

## 2026-11-17 — Commit messages were too long to read

- **Asked:** The user read `git log` at the end of Phase 2.
- **What went wrong:** Subjects ran up to ~85 characters and used internal codes (`QA-12`, `E16`) and SQL/class names;
  bodies were 10+ lines of detail. The history couldn't be skimmed by someone who doesn't know the code.
- **Fix:** CLAUDE.md §6.1 now limits the subject to ~50 characters in plain words and the body to 1–3 lines plus a
  `Refs:` line; the details stay in DECISIONS.md and this worklog. Pushed commits are left as they are (no history rewrite).
- **Also asked:** why migration files are named like `20261117172920_ProviderNamesNotBlank.cs`. That is EF Core's own
  naming: the timestamp prefix is how EF orders migrations, and it is stored in the database's `__EFMigrationsHistory`
  table, so renaming the files would break `dotnet ef database update`. The readable name is the part after the `_`.

## 2026-11-18 — Handled refusals were logged at Error by the framework (QA-20)

- **Asked:** Phase 1, the error handling: ProblemDetails with stable codes, `IExceptionHandler` logging 4xx at Information
  and 5xx at Warning.
- **What went wrong:** On .NET 8, `ExceptionHandlerMiddleware` logs "An unhandled exception has occurred" at Error for every
  exception before it calls `IExceptionHandler`. So a double click (409), a reused key (422) or a provider outage (503) wrote
  an Error line with a stack trace, and any alert on Error would have fired for normal behavior. Claude had only tested the
  response body and status, never the log level.
- **Found by:** the qa agent in Phase 3 (QA-20), as a skipped known-bug test.
- **Fix:** `AppExceptionMiddleware`, just inside `UseExceptionHandler`, catches `AppException` and delegates to the same
  `AppExceptionHandler` (D-038). The QA test is un-skipped; `ErrorLoggingTests` adds a no-database check that a real
  exception still logs at Error and that 404 pages and the no-cache header are unchanged.

## 2026-11-18 — qa tests failed only in the exotic-culture CI run (culture-sensitive string asserts)

- **Asked:** Phase 3 step 5, qa slice A (74 new tests). They passed in the normal run and on the dev machine.
- **What went wrong:** The second CI run (`TZ=Pacific/Kiritimati`, culture `th-TH`, E16) failed 2 of them.
  (1) xUnit's `Assert.DoesNotContain(string, string)` compares with the CURRENT CULTURE, and under Thai collation
  `"   at "` (the stack-trace check) matched inside an ordinary 405 body. (2) A test looked for alice's shift id plus a comma
  in bob's response; with id `5` that matched `"status":405,`, so it depended on the database identity value.
  The main session also pushed the test commit (`f4e61f9`) without waiting for CI, so CI was red on two commits in a row.
- **Found by:** the E16 CI run, which exists for exactly this. Reproduced locally with the same env vars.
- **Fix:** ordinal comparisons in the new string assertions; the id check now asserts that bob's refusal has no `shiftId` /
  `clockIn` property (parsed JSON, not a substring). The whole suite passes under the exotic settings.
  Process fix: wait for the CI result after every push before starting the next task.

## 2026-11-18 — error responses were application/json instead of application/problem+json (QA-18)

- **Asked:** Phase 3 step 1, the punch endpoint, with RFC 7807 ProblemDetails for every error (CLAUDE.md).
- **What went wrong:** Claude put `[Produces("application/json")]` on the controller (and copied it to the status controller).
  That attribute decides the Content-Type of every `ObjectResult`, including the ProblemDetails for 400/409/422, so the bodies
  were right but the media type was not `application/problem+json`. Claude had asserted status codes and body fields only.
- **Found by:** the qa agent in Phase 3 (QA-18), as a skipped known-bug test.
- **Fix:** removed the controller-level attribute; the media type is declared on each `[ProducesResponseType]`
  (success `application/json`, errors `application/problem+json`), so Swagger still documents both (D-039). The test is
  un-skipped and now checks 400/401/403/409/415/422/503 plus the framework 404/405 and the status endpoint's 401.

## 2026-11-18 — 400 bodies leaked CLR type names and parser positions (QA-19)

- **Asked:** Phase 3 step 1, the punch endpoint with ProblemDetails errors and a strict body (D-029).
- **What went wrong:** Claude kept the default `[ApiController]` 400 output and only checked status and `code`. For bad JSON the
  `errors` carried System.Text.Json text: `ClockManager.Api.Application.Punches.PunchRequest`, `LineNumber | BytePositionInLine`,
  `$.intent` paths and a `"request": "The request field is required."` noise entry. That is an information leak and an unstable text.
- **Found by:** the qa agent in Phase 3 (QA-19), as a skipped known-bug test.
- **Fix:** one `InvalidModelStateResponseFactory` (`InvalidRequestResponse`, D-040) replaces every JSON-reader message with a fixed
  sentence keyed by the wire field name (or `body`), echoes an unknown member name only after cleaning and capping it, and still
  returns `request.invalid` / `application/problem+json`. The test is un-skipped and was shown to fail without the fix.

## 2026-11-18 — a duplicate `intent` member was accepted and the last one won (QA-17)

- **Asked:** Phase 3 step 1, the punch endpoint with a strict body (D-029).
- **What went wrong:** Claude made the body strict about unknown members but did not think about a member that appears twice.
  System.Text.Json runs the LAST one, so `{"intent":"clock-in","intent":"clock-out"}` clocked out, while a gateway or log reading
  the first member would see a clock-in. A qa-slice test even recorded "last wins" as observed behaviour instead of asking if it was safe.
- **Found by:** the qa agent in Phase 3 (QA-17), as a skipped known-bug test.
- **Fix:** `PunchRequestJsonConverter` (D-041) pre-scans the object, compares names case-insensitively, and throws on a repeat; the
  400 factory turns it into `intent: "must appear only once"`. The real read still uses the strict wire type. The test is un-skipped
  and extended (same/different values, `Intent`, escaped name, three copies, duplicate unknown member) and was shown to fail without the fix.
  Note: `JsonException.Path` is read-only, the constructor with a path argument is the way to set it.

## 2026-11-18 — the trusted instant was not normalized before the guard and the save (QA-21, QA-22)

- **Asked:** Phase 3 step 2, the punch service with the monotonic guard (D-030).
- **What went wrong:** Claude compared and saved the instant exactly as the clock returned it. NodaTime keeps nanoseconds, the
  database keeps 100 ns ticks, so an instant less than one tick after the clock-in passed the guard but was stored equal and
  `CK_Shifts_ClockOutAfterClockIn` gave a 500. The punch path also had no range check of its own: it trusted the sources to reject
  odd years, so year 1 or 9999 from any other clock would reach EF/SQL as a 500.
- **Found by:** the qa agent in Phase 3 (QA-21, QA-22), as two skipped known-bug tests. Both are unreachable with today's providers.
- **Fix:** `TrustedInstantRules.Normalize` runs right after the clock call: it refuses an instant outside [2026, 2100) as 503
  `time.invalid_response` (the window constant now lives there and the sources reuse it) and floors the instant to a tick; the
  guard and the stamp use the floored value (D-042). Both tests are un-skipped and were shown to fail without the fix
  (500 instead of 503); boundary and round-trip tests were added.

## 2026-11-18 — the last-punch snapshot was read after the state check (QA-24, QA-15)

- **Asked:** Phase 3 step 2, the punch service with the race handling of D-030 and D-032.
- **What went wrong:** Claude read the open shift first and the employee's last punch after it. If the winning twin committed in
  between, the loser's "before" snapshot already held the winner's instant; with an equal trusted instant the guard answered 503
  `time.went_backwards` instead of 409 (different key) or the stored 201 (same key). The replay catch only handled 409s.
- **Found by:** the qa agent (QA-24, QA-15): four skipped `QA24_*` storm tests that failed about 3 of 4 runs.
- **Fix:** the snapshot is read before the state check, so it can never be newer than the state it is paired with, and
  `time.went_backwards` also triggers the stored-record replay (D-043). The tests are un-skipped, stable over 10 runs, and fail
  without the fix; the lenient twins that tolerated the 503 were tightened.
