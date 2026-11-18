# 06 — History: my shifts by week

**Status:** Done 2026-11-18 · Backend endpoint (D-064..D-066) and History page (`33cece4`, D-061..D-063) · Still to do by hand: look at the page in a browser
**Refs:** master plan D4 (work date = Zurich date of the clock-in), §4 overnight and DST cases · CLAUDE.md rules 1 and 2

## 1. Goal
A person can look back at their own shifts, week by week, with the hours worked each day and each week. Hours are always counted from the
UTC instants, so a week with a daylight-saving change is still right.

## 2. What the user sees
- A **History** page (link in the header next to Log out): one week at a time, Monday to Sunday, Zurich dates, with **Previous / Next week** buttons.
- Each day lists its shifts: "08:03 to 17:12 (Zurich time), 9 h 09 min", the day's total, and the week total at the top.
- Labels in plain words: **"ends next day"** for an overnight shift, **"clocks changed this night"** on a daylight-saving day, **"still open"** for a shift without a
  clock-out, and the existing notices for long, short and forgotten shifts.
- Empty week: "No shifts this week." Same design language as the punch screen (Tailwind, calm, phone first).

## 3. API contract (both agents build against this)
`GET /api/me/shifts?from=2026-11-16&to=2026-11-22` (needs the login cookie; dates are Zurich dates, both included, `yyyy-MM-dd`)
- `from` and `to` are optional **together**. Without them the server returns the Monday to Sunday week that contains the work date of the employee's **latest shift**. With no shifts at all: `days: []`, `from` and `to` null, `totalSeconds` 0.
- Rules: `to` not before `from`, at most **31 days**, valid dates. Otherwise 400 `request.invalid` with fixed messages (keys `from`, `to`).
- **200:**
```json
{
  "from": "2026-11-16", "to": "2026-11-22",
  "days": [
    { "date": "2026-11-17", "totalSeconds": 32940, "notes": [],
      "shifts": [ { "shiftId": 7, "workDate": "2026-11-17",
                    "clockIn":  { "zurich": "2026-11-17T08:03:11+01:00" },
                    "clockOut": { "zurich": "2026-11-17T17:12:20+01:00" },
                    "durationSeconds": 32940, "endsNextDay": false, "open": false, "flags": [] } ] }
  ],
  "totalSeconds": 32940
}
```
- Only days that have shifts are listed, in date order. A day's `notes` can contain `"dst_change"` (the Zurich day has 23 or 25 hours).
- Each shift sits on the day of its **clock-in** (D4). `endsNextDay` is true when the clock-out's Zurich date is later than the work date.
- `durationSeconds` comes from the UTC instants; an open shift has `null` and `open: true`. **Totals count closed shifts only.**
- Each shift keeps the same `flags` as the punch response (`long_shift`, `short_shift`, plus `forgotten_clock_out` while open and over the limit).

## 4. Design
- No new table and no migration: the existing index on (employee, clock-in Zurich date) serves the range query.
- A small application service builds the days and totals; the controller stays thin. Only **my** shifts, taken from the logged-in employee.
- **No clock is needed:** the default week comes from the data, and "Previous / Next week" is plain date arithmetic on the `from`/`to` strings (no browser or server clock).
- The "clocks changed" note comes from the NodaTime time zone rules, not from guessing.

## 5. Steps and commits
| # | Task | Commit subject (short) |
|---|---|---|
| 1 | Backend: endpoint, service, tests | `feat(history): weekly shifts endpoint` |
| 2 | Frontend (in parallel, mock first): History page, week navigation, labels, logic tests | `feat(ui): weekly history page` |
| 3 | I check both against the real backend with a script and update README / DECISIONS | `docs: history decisions and checklist` |

## 6. Tests
Totals in a normal week · **spring DST week** (a shift across the change counts 7 h) · **autumn DST week** (9 h) · **year boundary** (Dec 28 to Jan 3) ·
**overnight Sunday to Monday** stays in the week of the clock-in · open shift listed but not counted · another employee's shifts never appear · bad dates and a
range over 31 days → 400 · empty result · default week without parameters. Frontend: week navigation and label logic (Vitest, logic only).

## 7. Out of scope
Month view, charts, CSV export, editing shifts (Phase 7), all employees (Phase 7).

## 8. Questions for you (I'll use my recommendation unless you say otherwise)
- **Q1.** Weeks run **Monday to Sunday**? *(recommended: yes, the Swiss/European norm)*
- **Q2.** History opens on the **week of your latest shift** (no clock needed), with Previous / Next? *(recommended: yes)*
- **Q3.** An **open shift** is listed as "still open" and **not** counted in the totals until it is clocked out? *(recommended: yes, never invent an end time)*
