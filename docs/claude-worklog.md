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
