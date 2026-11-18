---
name: record-decision
description: Record an architectural, product, or technical decision, a handled edge case, a deliberate omission, or a known limitation in DECISIONS.md. Use PROACTIVELY, without being asked, in the same change whenever you choose between real alternatives (library, time provider, schema shape, API contract, error semantics, UX behavior), decide how an edge case is handled, cut or postpone scope, accept a limitation, or deviate from CLAUDE.md. Skip trivial choices such as naming or formatting.
---

# record-decision

`DECISIONS.md` at the repo root is a graded deliverable. It must show **what** was decided,
**why**, **what else was considered**, and **what it costs**. Write it in the same change as the code,
never "later".

## When to record (any one is enough)

- You picked one of several reasonable options: a library, an external time API, retry/timeout
  policy, a DB type or index, the endpoint shape, status codes, auth approach, or UI behavior.
- You decided how an edge case behaves (DST, overnight shift, forgotten clock-out, double click,
  provider outage, ...).
- You chose **not** to build something, or postponed it.
- You found a limitation you are not fixing.
- You changed or reversed an earlier decision. Do not delete the old entry. Set its status to
  `Superseded by D-NNN` and add a new entry.

**Do not record:** naming, formatting, refactors with no behavior change, or anything CLAUDE.md
already requires (unless you are changing it).

## How

1. Read `DECISIONS.md`. If it does not exist, create it with the four sections below.
2. Find the right section:
   - **Decisions**: a choice between alternatives → a new `D-NNN` entry (next number, never reuse one).
   - **Edge cases**: one row per scenario.
   - **Not done (out of scope)**: what, why, and what it would take.
   - **Known limitations**: what, impact, mitigation/workaround.
3. Check for an existing entry on the same topic. Update that one rather than adding a duplicate.
4. Write briefly and specifically. Give concrete facts (numbers, names, status codes), not vague words.
   Each section should be 1–3 lines, written for a reviewer reading quickly.
5. Link to the plan file and the test that covers the behavior when they exist.
6. In your final report to the user (or parent agent), list the IDs/rows you added or changed.

## Entry format — Decisions

```markdown
### D-007 — Fail closed when the time provider is unavailable
- **Date:** 2026-11-17 · **Status:** Accepted · **Area:** backend
- **Context:** Punches must use external Europe/Zurich time; providers can time out or return errors.
- **Options:** (a) fall back to the server clock · (b) queue the punch and resolve later · (c) reject with 503.
- **Decision:** (c), plus a secondary provider tried before rejecting.
- **Why:** (a) breaks the core rule; (b) creates records with an uncertain time and complex reconciliation.
- **Trade-offs:** An employee cannot punch during a full outage; the UI shows a clear retry message.
- **Refs:** plans/01-trusted-clock.md · `TrustedClockTests.Fails_closed_when_both_fail_E5_E6`
```

Get the date for the entry from the conversation context or `git log -1 --format=%cs`. This
is documentation metadata, so it does not fall under the punch-time rule.

## Row format — Edge cases

```markdown
| Scenario | Handling | Covered by |
|---|---|---|
| Shift crosses DST end (Oct, 02:00 happens twice) | Duration from UTC instants; displays offset | `ShiftDurationTests.DstEnd` |
```
Use `open` in "Covered by" when QA found the case but it is not handled yet.

## Parallel agents

If several agents may be editing at the same time, a subagent should **not** edit DECISIONS.md
itself. Put the drafted entry in its final report under the heading `Proposed DECISIONS.md entries`,
and the main session applies them.
