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
