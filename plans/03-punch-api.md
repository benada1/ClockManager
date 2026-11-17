# 03 — Punch API

**Status:** Questions answered 2026-11-17 (Q1 key required, Q2 went_backwards = 503, Q3 cached trusted server-now; recommended option for all three) · waiting for the go to build
**Lead agent:** backend · **Supporting:** db (idempotency table, transaction review), qa (race, replay, failure tests)
**Master plan refs:** D1, D5, D6, D7, D9 · §4 "Concurrency", "Business-rule edges" · CLAUDE.md rules 1 and 2

---

## 1. Goal

An employee can **Clock In** and **Clock Out** through the API. Each punch:
- gets its time **only** from `ITrustedClock` (Phase 1). The client sends an intent, never a time;
- is saved in the Phase 2 schema, whose constraints are the final guard;
- answers a retry (same `Idempotency-Key`) with the **original** result instead of punching twice;
- fails with a clear ProblemDetails code instead of guessing.

There is no UI in this phase (Phase 5). Swagger and the tests are the clients.

## 2. Scope

### In scope
- `POST /api/punches`: `{ "intent": "clock-in" | "clock-out" }` + `Idempotency-Key` header.
- `GET /api/me/status`: open shift, the last closed shift, flags, and a labeled "server now".
- `ICurrentEmployee`: who is punching. Filled from a **Development-only** header in this phase; Phase 4 swaps in cookie login.
- `PunchService`: the use case (state → trusted time → rules → save → map DB errors).
- `IdempotencyRecord` table + migration (moved here from Phase 2, D-025).
- Computed flags: forgotten clock-out (open > `MaxOpenShiftHours`), long shift, short shift (< `ShortShiftSeconds`).
- QA-11 (carried over from Phase 1): connection warm-up at startup + `SocketsHttpHandler` pool settings.
- Unit tests (fake clock) and integration tests (real SQL Server).

### Out of scope
- Login, passwords, roles on endpoints → Phase 4 (this phase only prepares `ICurrentEmployee`).
- UI → Phase 5. History by day/week → Phase 6. Manager corrections → Phase 7.
- Cleanup of old idempotency records (recorded as a known limitation).

## 3. Who is punching (decided 2026-11-17: punch first, login in Phase 4)

```csharp
public interface ICurrentEmployee
{
    // null = nobody is signed in → 401 auth.unauthenticated
    Task<CurrentEmployee?> GetAsync(CancellationToken ct);
}
```
- **Phase 3:** `DevHeaderCurrentEmployee` reads the header `X-Dev-Employee: <username>` (e.g. `alice`) and loads the
  employee from the DB. It is registered **only in Development**. In every other environment there is no header
  implementation at all, so every punch answers 401. A test proves that Production ignores the header.
- **Phase 4:** a cookie-based implementation replaces it, and the dev header is deleted. `PunchService` doesn't change.
- Unknown username → 401. Deactivated employee (`IsActive = false`) → 403 `employee.inactive` (new code).

## 4. API contract

### `POST /api/punches`
```http
POST /api/punches
Idempotency-Key: 3f1c0d7e-8a4b-4c55-9a51-0d2b7c1e9f10
X-Dev-Employee: alice
Content-Type: application/json

{ "intent": "clock-in" }
```
- The body is read **strictly**: an unknown field (e.g. `"time": "..."`) is a 400. That way a client can't even
  try to send a timestamp (CLAUDE.md rule 1).
- **201 Created** → the shift:
```json
{
  "shiftId": 42,
  "workDate": "2026-11-17",
  "clockIn":  { "utc": "2026-11-17T07:00:00.123Z", "zurich": "2026-11-17T08:00:00.123+01:00",
                "provider": "time.now", "crossCheckedBy": "timeapi.io", "lowPrecision": false },
  "clockOut": null,
  "durationSeconds": null,
  "flags": []
}
```
- A clock-out returns the same shape with `clockOut`, `durationSeconds` (from the **UTC instants**) and flags.

| Situation | Status | Code |
|---|---|---|
| Clock in while a shift is open (also: the loser of a race) | 409 | `shift.already_open` (+ the open shift in the body) |
| Clock out with no open shift | 409 | `shift.not_open` |
| No trusted time (down / invalid / providers disagree) | 503 | `time.unavailable` / `time.invalid_response` / `time.providers_disagree` |
| Trusted time ≤ the employee's last punch | 503 | `time.went_backwards` (Q2) |
| Same key, different intent | 422 | `idempotency.key_reused` |
| Missing or non-UUID `Idempotency-Key` | 400 | `idempotency.key_missing` (new code, Q1: the header is required) |
| Unknown intent, unknown field, bad JSON | 400 | `request.invalid` |
| Nobody signed in / inactive employee | 401 / 403 | `auth.unauthenticated` / `employee.inactive` |

### `GET /api/me/status`
```json
{
  "employee": { "username": "alice", "displayName": "Alice Example" },
  "openShift": { ...same shape as above... } ,
  "lastClosedShift": { ... } ,
  "serverNow": { "utc": "...", "zurich": "...", "source": "trusted-clock", "ageMs": 3200 },
  "flags": ["forgotten_clock_out"]
}
```
`serverNow` is for **display and flags only**, never for a record (Q3: trusted clock, cached ≤ 10 s, labeled with source and age). If no trusted time is available,
`serverNow` is null, the state is still returned, and time-based flags are left out instead of guessed.

## 5. The punch flow (`PunchService`)

```
1. who?            ICurrentEmployee → 401 / 403
2. replay?         IdempotencyRecord (employee, key) exists?
                     same intent → return the stored response (no provider call, no new row)
                     other intent → 422
3. state check     clock-in: open shift exists → 409 · clock-out: no open shift → 409
                   (cheap early answer; NOT the guarantee, the DB is)
4. trusted time    ITrustedClock.GetNowAsync → TrustedTimeException → 503   (no DB transaction open yet)
5. monotonic guard time must be > the employee's last punch (latest clock-in or clock-out) → else 503 went_backwards
6. save            one transaction: INSERT/UPDATE the shift + INSERT the IdempotencyRecord
7. map DB errors   2601 on UX_Shifts_OneOpenPerEmployee → 409 shift.already_open
                   2601 on the idempotency key → another request with the same key won → replay its response
                   DbUpdateConcurrencyException on clock-out → 409 shift.not_open (someone else closed it)
```
**Why the provider call comes before the transaction:** the call can take up to 3 s. A transaction (and its locks)
held open over an HTTP call would block other punches. The database constraints still catch every race at step 6.

**Why the state check happens before the provider call:** a click that is obviously wrong ("clock out" with no
open shift) gets its 409 fast, without using a provider request. It's an optimization, not the protection.

### Idempotency details (D6)
- Table `IdempotencyRecords`: `Id`, `EmployeeId` (FK), `Key` (`uniqueidentifier`), `Intent`, `ShiftId` (FK),
  `ResponseStatus`, `ResponseBody` (`nvarchar(max)` JSON), `CreatedUtc` (**the punch's trusted instant**, never a
  local clock). Unique index `(EmployeeId, Key)`.
- **Only successful punches are stored.** A 503 or 409 is not saved, so a retry with the same key after an outage
  really tries again. That's what the "Retry" button in Phase 5 needs.
- The key is scoped per employee: two employees can't collide or read each other's replies.

## 6. Edge cases → tests

| # | Scenario | Expected | Test type |
|---|---|---|---|
| P1 | Clock in, then clock out (normal day) | 201, 201; duration from UTC | integration |
| P2 | Shift across DST start (T: 22:00 → 06:00, March) | `durationSeconds` = 7 h | unit (fake clock) |
| P3 | Shift across DST end (October), punch in the repeated hour | 9 h; both punches keep their own offset | unit |
| P4 | Overnight shift (23:00 → 07:00) and New Year's Eve | work date = Zurich date of clock-in | unit |
| P5 | 10 parallel clock-ins, different keys, same employee | exactly 1 × 201, 9 × 409 `shift.already_open`, 1 row | integration |
| P6 | Same request sent twice (lost response) | second answer = first (status + body), 1 row, provider asked once | integration |
| P7 | Same key sent 10 times in parallel | 1 row; all answers identical | integration |
| P8 | Same key, other intent | 422 `idempotency.key_reused` | integration |
| P9 | Provider down / invalid / disagree | 503 with the right code, **no row, no idempotency record** | integration |
| P10 | Retry with the same key after a 503, provider back | 201 (failure wasn't stored) | integration |
| P11 | Provider time earlier than the last punch, or equal to it | 503 `time.went_backwards`, nothing saved | unit + integration |
| P12 | Two tabs: both clock out the same shift | one 201, one 409 `shift.not_open` | integration |
| P13 | Clock out after 17 h | 201, flag `long_shift`; status before it shows `forgotten_clock_out` | unit |
| P14 | Clock out 20 s after clock in | 201, flag `short_shift` | unit |
| P15 | Body with an extra `time` field / unknown intent / no body | 400, nothing saved, provider not asked | integration |
| P16 | Missing / malformed `Idempotency-Key` | 400 `idempotency.key_missing` | integration |
| P17 | `X-Dev-Employee` in Production | 401; header ignored | integration |
| P18 | Inactive employee | 403 `employee.inactive` | integration |
| P19 | Client disconnects during the provider call | nothing saved; no 5xx logged as a provider failure | integration |
| P20 | Status with the provider down | 200, state present, `serverNow: null`, no time-based flags | integration |

The qa agent adds more in step 5. Every handled case gets a row in the DECISIONS.md edge-case table.

## 7. Steps and commits (autonomous, CLAUDE.md §6.1)

| # | Task | Who | Commit |
|---|---|---|---|
| 1 | `ICurrentEmployee` + dev header (Development only), `PunchService` clock-in/out without idempotency, `POST /api/punches`, strict body, error mapping, unit + integration tests P1–P5, P9, P11–P15, P17–P19 | backend | `feat(punch): clock in and out with trusted time, DB-backed rules and stable error codes` |
| 2 | `IdempotencyRecord` entity + migration (db reviews), replay + key reuse, tests P6–P8, P10, P16 | backend + db | `feat(punch): idempotent punches with Idempotency-Key replay` |
| 3 | `GET /api/me/status`, flags, labeled server-now, tests P20 + flag tests | backend | `feat(punch): employee status endpoint with forgotten clock-out flag` |
| 4 | QA-11: startup warm-up (result never used for a record) + `SocketsHttpHandler` pool settings, tests | backend | `perf(time): warm provider connections at startup (QA-11)` |
| 5 | qa adversarial pass: races, replay, provider failure during a punch, input | qa | `test(punch): ...` |
| 6 | Fixes for real qa findings, each its own `fix:` commit | backend | only if found |

DECISIONS to record during the build: dev header as the Phase 3 stand-in for login; provider call before the
transaction; only successful punches stored for replay; flags computed on read (not stored); `went_backwards` status;
status endpoint's cached server-now; strict body; QA-11 warm-up design.

## 8. Risks

| Risk | Mitigation |
|---|---|
| The dev header leaks into Production | Registered only in Development; P17 proves Production ignores it; deleted in Phase 4. |
| Idempotency and the one-open-shift index interact in a race (same key, parallel) | Shift + idempotency record in **one** transaction; on a key violation, reload and replay; P7 tests it. |
| Free providers rate-limit us because of the status endpoint | Status uses a short cache (Q3); punches always ask fresh (D1). |
| A slow provider call makes the punch feel slow | QA-11 warm-up; the 3 s timeout stays; low-precision flag when RTT > 1.5 s. |

## 9. Test plan summary
- **Unit (fake clock):** P2–P4, P11, P13, P14. No DB: the rules live in `Shift`, the guard and the flag calculator.
- **Integration (`WebApplicationFactory` + a throwaway SQL Server DB per class + `FakeTrustedClock`):** everything else.
  The fake clock can be told to fail, return a given instant, or be slow, so P9–P11 and P19 are exact.
- CI already runs both against SQL Server (D-026).

## 10. Questions for the user (answered 2026-11-17: recommended option for all three)

- **Q1. Should the `Idempotency-Key` header be required (recommended)?** Required: a punch without it is a 400, so
  every client gets retry safety and the replay logic has one path. Optional: easier to try in Swagger/curl, but a
  retry without a key could hit "already clocked in", which is the confusion D6 exists to prevent. (Swagger will
  show the header field, so trying it there still works.)
- **Q2. "Time went backwards": 503 (recommended) or 409?** 503 says "we have no trustworthy time right now",
  same as the other `time.*` errors, so the UI shows the time-problem banner with Retry. 409 would say "your request
  conflicts with the data", but the employee did nothing wrong.
- **Q3. Where does the status endpoint's "server now" come from?** (a) **The trusted clock with a short cache (≤ 10 s), labeled
  with its source and age (recommended).** It's correct for display and flags, and it doesn't call the providers on every page load.
  (b) A fresh provider call on every status request: always exact, but slow and could get us rate-limited. (c) No
  server now at all: then the UI can't show elapsed time without the browser clock, which CLAUDE.md forbids.
