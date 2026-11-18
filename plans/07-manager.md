# 07 — Manager view and corrections

**Status:** Draft 2026-11-18, waiting for approval · **Lead agents:** backend, frontend (in parallel against the contract below)
**Refs:** master plan D8 · D-025 (ShiftCorrection table moved here) · D-044 (employee lock) · D-049 (roles) · CLAUDE.md §2 "Manual corrections"
**User choices (2026-11-18):** scope **medium** (see everyone, close forgotten shifts, fix clock-in/out times, always with a reason) · the manager **types the corrected time**.

## 1. Goal
A manager can see who is working and what looks wrong, and fix real mistakes. **A correction never hides the original:** every change keeps the old value, who changed it,
when (trusted clock) and why, so a reviewer can always reconstruct what happened.

## 2. What the manager sees (desktop first)
- **Employees:** a table with name, status (clocked in since 08:03 / not clocked in), and warnings (forgotten clock-out, long shift today).
- **Problems:** a list of open shifts that have run too long (forgotten clock-out), plus unusually long or very short shifts. Each row links to the shift.
- **An employee's week** (same view as History, plus a **Correct** button on each shift) and a **shift detail** with the full change history.
- **Correct dialog:** which time (clock in or clock out), the new date and time (Zurich), a required reason, then a clear result. It shows the current value
  and the original value next to the new one.
- Employees also see a small "Corrected by a manager" note on a corrected shift in their own History.

## 3. Rules (server decides, the UI only shows)
1. Only role **Manager** (checked against the database role on every request, not only the token). Employees get 403.
2. **Reason is required** (5–500 characters, not blank).
3. The new time must be **in Zurich time with its offset** (`2026-11-17T17:12:00+01:00`); the offset must match the time zone rules at that instant (no ambiguity in the repeated October hour).
4. **Not in the future**, measured with the **trusted clock**. No trusted time → 503, nothing changes (fail closed).
5. **Clock-out later than clock-in**, and the shift must **not overlap** another shift of the same employee.
6. **Closing an open shift** is allowed only when it is a **forgotten** one (open longer than `MaxOpenShiftHours`); a shift that may still be running gives 409 `correction.shift_still_running`.
7. A correction is refused if the shift changed meanwhile (**row version** mismatch → 409), and runs under the same **employee lock** as punches (D-044), re-checking the rules inside the transaction.
8. A manager may correct their own shifts; it is audited like any other (recorded in DECISIONS).
9. The moment of each correction is stamped by the **trusted clock**, never the server clock.

## 4. Data
- New table **`ShiftCorrections`**: `Id`, `ShiftId` (FK, no cascade), `ManagerId` (FK, no cascade), `Field` (`ClockIn` | `ClockOut`), `OldUtc` (null when an open shift is closed),
  `NewUtc`, `Reason`, `CreatedUtc` (trusted instant) plus the trusted-time source. CHECKs: reason not blank, field in the list, `OldUtc` and `NewUtc` at UTC offset 0.
- **The Shifts row holds the current (effective) values**, so the one-open-shift index, the "out after in" check, History and totals keep working unchanged.
  The **original** values are never lost: the first correction row of a field stores what was there before.
- A manager-typed time is saved as a punch with provider `manager_correction` (no round trip, raw text says who and why); the Zurich offset and work date come from the time zone rules.
- One migration (`AddShiftCorrections`); no other schema change.

## 5. API contract (both agents build against this)
All need the login cookie; ProblemDetails errors as everywhere. `403 auth.forbidden` for non-managers.
| Endpoint | Returns |
|---|---|
| `GET /api/manager/employees` | `[{ id, username, displayName, role, isActive, status: "clocked_in"\|"clocked_out", since: { zurich }\|null, flags: [] }]` |
| `GET /api/manager/anomalies` | newest first, max 100: `[{ shiftId, employeeId, displayName, workDate, kind: "forgotten_clock_out"\|"long_shift"\|"short_shift", clockIn:{zurich}, clockOut:{zurich}\|null, durationSeconds\|null }]` |
| `GET /api/manager/employees/{id}/shifts?from&to` | same body as `GET /api/me/shifts`, for that employee, each shift with `rowVersion` and `corrected: bool` |
| `GET /api/manager/shifts/{id}` | `{ shift: {…, rowVersion}, employee: {id, displayName}, corrections: [{ id, field, old:{zurich}\|null, new:{zurich}, reason, by:{id, displayName}, at:{zurich} }] }` |
| `POST /api/manager/shifts/{id}/corrections` | body `{ field: "clockIn"\|"clockOut", newTime: "2026-11-17T17:12:00+01:00", reason, rowVersion }` → **201** with the same body as the shift detail |
- Errors: 400 `request.invalid` (fixed messages: `newTime`, `reason`, `field`), 404 `request.not_found`, 409 `correction.shift_still_running`, 409 `correction.overlap`, 409 `correction.stale` (row version),
  422 `correction.invalid_order` (out not after in), 422 `correction.in_future`, 503 `time.*` (no trusted time).
- `GET /api/me/shifts` gets one extra field per shift: `corrected: bool`.

## 6. Edge cases to cover in tests
Employee clocking out while the manager corrects the same shift (lock + row version) · two managers correcting at once · correcting across the clock-change night and the
repeated October hour (offset checked) · moving a clock-in to the previous day (work date changes) · overlap with the next/previous shift · closing a forgotten shift then the employee clocks in again ·
closing a running shift (refused) · time in the future · trusted time down (503, nothing saved) · reason blank/too long · other employee's shift id · non-manager and unauthenticated calls ·
audit rows never change and the original value stays reconstructable · corrected shift totals in History use the effective times.

## 7. Steps and commits
| # | Task | Commit subject (short) |
|---|---|---|
| 1 | Backend: migration, correction service (lock, rules, audit), the endpoints, tests | `feat(manager): shift corrections with audit trail` and `feat(manager): employee and problem lists` |
| 2 | Frontend (parallel, mock first): manager pages, correct dialog, "corrected" note in History, logic tests | `feat(ui): manager pages and corrections` |
| 3 | Real-stack check by script on a published copy, README, DECISIONS | `docs: manager decisions and checklist` |
The wide QA (plan 08) then covers this phase too. Short comments, short commit messages, one commit per task.

## 8. Out of scope
Adding a shift the employee never punched (the user chose "medium"), deleting shifts, promoting employees to managers (seed only), e-mail notices, export.
