# 09 — Deliverables polish

**Status:** Draft, waiting for approval
**Why:** Reviewers read the README, DECISIONS.md, the worklog, CLAUDE.md, `.claude/`, the plans and the commit history. Phases 0–8 built and tested the app; this phase makes sure
what they read is true, consistent and easy to follow from a clean clone. No new features.

## 1. Goal
A reviewer can clone the repo, follow the README, get both servers and the tests running, and then find every decision, edge case, limit and Claude correction
without hunting. Nothing in the docs is stale, and nothing private or secret is in git.

## 2. Scope (in order, one commit per task)
1. **Clean-clone test** (the one thing Phase 8 left): fresh `git clone` into a new folder, new empty database, follow the README line by line (SQL Express path; read the LocalDB and
   Docker sections for correctness), user-secrets, migrations, both servers, seeded manager login, `dotnet test`, `npm test`. Every step that fails or is unclear becomes a README fix.
2. **README pass:** one reading order (what it is → prerequisites → run → try it → tests → decisions), remove "Phase N" headings in favour of feature names, a short
   "where to look" map (DECISIONS, plans, worklog, `.claude/`), seeded logins, known manual checks (look at the screens in a browser), no stale numbers.
3. **DECISIONS.md consistency:** every D-number is unique and referenced correctly, every edge-case row names a test that exists (script-checked, not by eye), the corrections table has real
   hashes and matches the worklog, "Not done" and "Known limitations" have no duplicates (the last lines of the file repeat some bullets).
4. **Worklog and CLAUDE.md:** worklog entries are in date order and match the corrections table; CLAUDE.md matches the real stack and commands (ports, scripts, test env vars).
5. **Plans and `.claude/`:** every plan has a final status and real commit hashes; the master plan checklist matches reality; agent and skill files match how they were used
   (no references to things that don't exist).
6. **Repo hygiene:** no secrets or local files tracked (`git ls-files` scan, history scan for connection strings with passwords/keys), `.gitignore` covers `STUDY-NOTES.md`, no dead files
   or unused code left, line endings consistent (the CRLF warnings), `dotnet list package --vulnerable` and `npm audit` results recorded.
7. **Final gate:** full `verify`, exotic time-zone/culture run, CI green on the last commit, short summary for the user (what to look at by hand).

## 3. Out of scope
Playwright, screenshots, new features, deployment, serving the built frontend from the API (all already listed in DECISIONS.md as not done).

## 4. Risks
- The clean-clone run needs a spare database name and stops nothing of yours (own ports, own DB, removed afterwards). It does use the real time providers.
- Fixing the CRLF warnings could touch many files at once. Plan: only add a `.gitattributes` and renormalize in its own commit, or leave it and record it, your choice (question below).
- Doc checks that need a human (looking at the screens) stay on the README's manual list; they are not claimed as done.

## 5. Test plan
Scripted checks for the doc claims (D-numbers, test names in edge-case rows, hashes exist in `git log`), then the full gate. Doc-only commits need no new tests; any code fix gets a failing test first.

## 6. Questions for you
1. Line endings: add `.gitattributes` and renormalize in one commit (clean, but a big diff), or just record it as a known nit?
2. Should the clean-clone run use a throwaway database name (my default), or do you want it on your normal `ClockManager` database?
