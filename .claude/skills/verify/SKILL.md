---
name: verify
description: Run the project's quality gate (backend build + tests, frontend build + lint, and a scan for forbidden local-clock usage) and report the results. Use before saying any code task is done, before a commit, and after merging work from several agents. Supports running only backend or only frontend.
---

# verify

Run the gate script from the repo root:

```powershell
pwsh -NoProfile -File .claude/skills/verify/verify.ps1              # everything
pwsh -NoProfile -File .claude/skills/verify/verify.ps1 -Scope backend
pwsh -NoProfile -File .claude/skills/verify/verify.ps1 -Scope frontend
pwsh -NoProfile -File .claude/skills/verify/verify.ps1 -Scope clock     # clock guard only (used by CI)
```

## What it checks

| Step | Command | Skipped when |
|---|---|---|
| backend build | `dotnet build` (the `.sln` in `backend/` if present, else the API project) | scope = frontend |
| backend tests | `dotnet test` | no test project exists yet |
| frontend build | `npm run build` (runs `npm ci` first if `node_modules` is missing) | scope = backend |
| frontend lint | `npm run lint` (oxlint) | scope = backend |
| clock guard | scans source files for `DateTime.Now/UtcNow/Today`, `DateTimeOffset.Now/UtcNow`, `TimeProvider.System`, `new Date()`, `Date.now()` | never |

The clock guard enforces CLAUDE.md rule 1. To allow a use that is not a punch, add the comment
`clock-ok: <reason>` on the same line (e.g. an HTTP timeout or a cache TTL). Test projects are
excluded. The script prints the full output of a step only when that step fails, which keeps the context small.

## Rules

- Use scope `all` for the final check before a commit. A smaller scope is fine while iterating.
- **Report honestly.** Paste the summary table. If a step failed, show the relevant error lines and
  do not call the task done. If a step was skipped, say so and why.
- Do not "fix" a failure by weakening the gate: no deleting tests, no adding `clock-ok:` to real
  punch code, no lowering lint rules. If the gate itself is wrong, say so and propose the change.
- Exit code: 0 = all executed steps passed; 1 = at least one failed.
