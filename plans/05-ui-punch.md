# 05 — Frontend: sign up, log in and punch

**Status:** Approved 2026-11-18 · Steps 1–2 done (screens built against the mock, D-045..D-048) · Step 3 (connect to the real login) next · **Lead agent:** frontend
**Refs:** master plan §4 "UI misinformation risks" · CLAUDE.md §5 (frontend) and rule 1 · API contract: plan 04 §3, plus the Phase 3 endpoints

## 1. Goal
A simple, clear screen where a person signs up or logs in, clocks in and out, and sees what happened. The browser **never** makes up a time.
The only times shown are the ones the server sends, in **Europe/Zurich**.

## 2. What the user sees
- **Sign up / Log in** pages (username, display name, password), with clear field errors.
- **Punch screen:** one big button, "Clock in" or "Clock out", depending on the state. Under it:
  - clocked **in**: "You clocked in at 08:03 (Zurich time)", plus a notice if the shift is flagged as forgotten (open too long);
  - just clocked **out**: **"You clocked out at 17:12 (Zurich time)"** and the shift length, e.g. "Worked 9 h 09 min";
  - nothing open: "You are not clocked in", and the last shift's times if there is one.
- **No live clock** and no running timer anywhere (your decision). The screen shows a time only when a punch happened.
- A header with the user's name and a **Log out** button.

## 3. Scope
**In:** `src/api/` client, auth pages, punch screen, all states below, a Zurich formatting helper, accessibility basics, a mock mode for building without the backend.
**Out:** history and week views (Phase 6), manager screens (Phase 7), the existing Time lab page stays as it is.

## 4. Behavior and states
| Situation | What the screen does |
|---|---|
| Loading status | Spinner text; the button is not shown yet |
| Button pressed | Button disabled while the request runs; one new random key per attempt (`crypto.randomUUID()`), reused only for a retry of the same attempt |
| 201 | Show the clock-in or clock-out time from the **response** (`clockIn.zurich` / `clockOut.zurich`), then refresh the status |
| 409 | Plain message ("Your shift was already open / already closed, possibly in another tab"), then reload the status |
| 503 (`time.*`) | Banner: "We can't get a trusted time right now, nothing was recorded." and a **Try again** button (same key) |
| 400 / 422 | Show the server's field messages (`intent`, `body`, `time`) |
| 401 | Go to the login page ("Your session ended, please log in") |
| 403 `employee.inactive` | "This account is switched off" |
| 429 | "Too many tries, wait a minute" |
| Offline / server down | "You are offline or the server is down", button disabled |
| Tab gets focus again | Reload the status (another tab may have punched) |

## 5. Design rules
- Times are formatted only with `Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Zurich' })` from the strings the server sends; the label says "Zurich time".
- Never `new Date()` or `Date.now()` to create or show a punch time. (Elapsed or duration text comes from the server's `durationSeconds`.)
- Accessible: labeled inputs, visible focus, the result message in an `aria-live` region, keyboard use, readable on a phone.
- Styling with **Tailwind CSS** (added in step 1, logged as a decision); clean, calm, one main action per screen. Small files, short comments. One `src/api/` layer using `fetch` with `credentials: 'same-origin'` (the cookie is HTTP-only, so no token handling in JavaScript).

## 6. Working in parallel with the backend
- The backend plan fixes the API contract (plan 04 §3), so this plan can start at the same time. The frontend builds against the **mock mode**
  (`npm run dev:mock`), which gets login, signup, me, punches and status answers for every state above.
- Phase 3's punch and status endpoints already exist. Until the backend login lands, **real** testing uses the dev header; the final step switches to real login.
- Each agent runs only its own checks (`verify -Scope frontend` or `-Scope backend`); I run the full `verify`, apply `DECISIONS.md` and README changes, and do the commits, so nothing collides.

## 7. Steps and commits
| # | Task | Commit subject (short) |
|---|---|---|
| 1 | Add Tailwind, API client, auth context, router guard, signup + login pages, mock mode | `feat(ui): sign up and log in pages` |
| 2 | Punch screen: status, button, Zurich times on punch, all states, focus refresh | `feat(ui): clock in and out screen` |
| 3 | Connect to the real backend login (after plan 04 step 2), remove dev-header use, manual two-tab and provider-down check | `feat(ui): use the real login` |
| 4 | Accessibility pass, `npm run build` + `npm run lint` clean | `chore(ui): accessibility and lint` |

## 8. Test plan
`npm run build` and `npm run lint` clean. Component tests only if the project already has the tooling (otherwise listed in DECISIONS.md as not done).
Manual checklist, written into the README: sign up → log in → clock in → second tab clock in (409) → clock out (shows Zurich time) → provider down (503 banner) →
log out → expired session (401).

## 9. Risks
| Risk | Mitigation |
|---|---|
| Frontend and backend disagree on the contract | The contract is in plan 04 §3; mock mode follows it; step 3 integrates and fixes differences |
| The cookie isn't sent through the Vite proxy | Same-origin proxy; tested in step 3 |
| Showing a time that is wrong | Only server strings are shown; no browser clock |
