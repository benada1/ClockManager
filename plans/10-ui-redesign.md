# 10 — UI redesign and light/dark theme

**Status:** Done 2026-11-20 · approved by the user in chat (the mockups are the brief) · 6 steps, 6 commits, gate green (see §8) · still to do by hand: look at it in a browser, both themes
**Refs:** user mockups (7 screenshots + the design source `Login/Main/History/Team/Employee.dc.html`) · CLAUDE.md §1 rule 1, §5 frontend · D-045, D-047, D-057, D-058

## 1. Goal
Every screen of the frontend gets the look of the mockups: a calm top bar with a segmented nav, big rounded cards, soft pills,
large tabular numbers with small muted units. A light and a dark theme share one set of tokens. **Light is the default** on a
first visit; a sun/moon button switches it and the choice is remembered. Nothing the app does today may break.

## 2. Scope (every page and component)
- **Shell:** `AppShell` (logo, centered segmented nav Clock / History / Manager, theme toggle, avatar + name, Log out, footer
  with the Time lab link), `AuthLayout` (logo + toggle only, centered title, card, footer "All times are Zurich time.").
- **Components:** `Button`, `Card`, `Field`, `Banner`, `Pill`, `Loading`, `Dialog`, new `ThemeToggle`, `Avatar`, `icons`, `BarChart`, `EmptyState`.
- **Pages:** Login, Signup, Clock (Punch), History (+ `WeekView`, `DayCard`), Manager (Team), ManagerEmployee, ManagerShift,
  CorrectionDialog, the not-found / loading / unreachable states in `App`, and the Time lab with its panels.
- **Cleanup:** `index.css` (old plain CSS of the Time lab) is deleted once the Time lab uses the components; Tailwind's
  preflight is then imported normally; old tokens (`rounded-ui`, `rounded-block`, ...) are removed.

## 3. Theme approach
- Tokens once in `src/app.css`: `@theme` holds the light values (the mockup's `.cm` palette), `:root[data-theme='dark']`
  overrides the same `--color-*` / `--shadow-*` variables with the mockup's `.cm.dark` palette. Components only use token
  utilities (`bg-surface`, `text-muted`, ...), never raw colours. The default Tailwind palette is switched off so a stray
  `bg-gray-100` cannot sneak in.
- `src/lib/theme.js`: read/write `localStorage['cm-theme']` inside try/catch (fallback light), set `data-theme` on `<html>`,
  a tiny store + `useTheme()` hook. `index.html` runs a 3-line inline script before the bundle so the first paint already has
  the right theme (no flash).
- Contrast is checked for both palettes (AA 4.5:1 text, 3:1 input borders). Where a mockup value fails (e.g. `faint` text,
  `input-border` on text inputs) a stronger token is used and recorded.

## 4. Time rules for the new Clock page (CLAUDE.md §1)
- The big clock, the greeting and the date come from `serverNow` of `GET /api/me/status`:
  `now = serverNow.utc + ageMs + (performance.now() − syncAt)` (the D-058 formula). It ticks with the monotonic clock between
  syncs, marked `clock-ok: display only, re-synced from server`. The page re-syncs on focus, after every punch and once a minute.
- No `serverNow` (providers down) → no clock, no greeting by time of day, a calm "server time unavailable" text. The device
  clock is never shown.
- Elapsed time while clocked in uses the same anchor; after a punch the last anchor is kept until the refresh brings a new one.
- "This week" asks `GET /api/me/shifts` for the Zurich week of the server's date (Mon–Sun by string arithmetic). Without a
  server date it shows the server's default week, labelled "Latest week".

## 5. Steps (one commit each)
1. Plan, tokens, theme store + toggle, shell, base components.
2. Login and Signup.
3. Clock page (status card, live server clock, last shift, this week + bar chart).
4. History (header with week nav, summary card + bars, day cards).
5. Manager pages and the correction dialog (filter chips, table, people cards, employee and shift pages).
6. Time lab on the shared components, delete `index.css`, preflight, remove unused tokens, docs.

## 6. Risks
- Breaking behaviour while moving markup: logic modules stay untouched; only view files change. New logic goes into small pure
  functions with tests (`serverClock.js`, `weekOf`/`weekDays`, `shiftTimeParts`, theme storage).
- Mockup data the API does not have (per-person weekly hours on the Team page, "Show all"): left out, recorded.
- Preflight could restyle something unexpectedly: imported only in the last step, after the Time lab no longer needs its own CSS.

## 7. Test plan
- Vitest: theme storage (missing, garbage, throwing storage), server clock parts (T6, T7 DST instants, greeting bounds, no
  serverNow), anchor keeping after a punch, week of a date (Monday start, year boundary T8), bar data, shift time parts (next
  day suffix, DST offsets T5/T6).
- `verify -Scope frontend` after each step, full `verify` at the end.
- By hand in `npm run dev:mock`: fresh, open, closed, forgotten, notime, unavailable, offline, history, historyempty,
  manager, managerstale, both themes, narrow window.

## 8. Progress log
**Done 2026-11-20.** Steps 1-6 committed one by one, `verify -Scope frontend` green after each.
- New tests: `theme.test.js`, `serverClock.test.js`, `views.test.js`, `views.smoke.test.jsx` (server-side render of the main views).
- Decisions: D-082 (theme, light default, contrast fixes), D-083 (no web font), D-084 (live server clock), D-085 (one clock card), D-086 (Team page); D-057 superseded, D-045/D-047/D-058 notes updated; four edge-case rows; "Not done" lists the mockup parts the API has no data for.
- Checked: production build, oxlint, Vitest, clock guard, the built CSS has the dark block and the used utilities, `npm run dev:mock` serves the page and the mock API.
- **Checked by hand, not by Claude:** Claude had no browser tool in this session, so the author checked the screens in the browser. The README manual list covers it, plus: both themes, a narrow window, `?mock=notime`, `?mock=open`, `?mock=manager`.