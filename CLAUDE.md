# CLAUDE.md — ClockManager

Employee time-clock system: employees **Clock In / Clock Out** of shifts.
Stack: **ASP.NET Core 8 Web API** · **Microsoft SQL Server** (EF Core) · **React 19 + Vite** (JSX).

This is a graded home assignment. Reviewers read the code, the commit history, `README.md`,
`DECISIONS.md`, this file, `.claude/`, and the work plans. **Depth over breadth**: a few features
done thoroughly, with reasons, are better than many shallow ones. Anything not done goes into `DECISIONS.md`.

---

## 1. Non-negotiable rules

1. **Punch time comes only from an external time API, using `Europe/Zurich`.**
   - Never use `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `TimeProvider.System`
     or the browser clock (`new Date()`, `Date.now()`) to create or validate a Clock In/Out record.
   - The client **never sends a timestamp** for a punch. It sends only the intent (`clock-in` / `clock-out`).
     The server gets the time itself.
   - If no trusted time is available (provider down, timeout, bad response), the punch **fails closed**
     (HTTP 503 with a clear ProblemDetails message). There is **no silent fallback** to the local clock.
   - All time access goes through one abstraction (`ITrustedClock` or similar). It is the only place
     that talks to the time provider(s), and tests can replace it with a fake.
   - Local clocks may be used **only** for non-record purposes (logging, HTTP timeouts, cache TTLs), and
     the code must make that obvious.
2. **The server is the source of truth.** All business rules (one open shift, ordering, limits) are
   checked on the server and protected in the DB with constraints. The UI only shows the result.
3. **No secrets in git.** Connection strings with passwords and API keys go in `dotnet user-secrets`
   or environment variables. `appsettings.Development.json` may hold LocalDB/trusted-connection defaults only.
4. **Every non-obvious decision goes in `DECISIONS.md`**: the decision, the alternatives, the reason,
   and the trade-off. Do this in the same change that makes the decision, not afterwards.

## 2. Domain model and rules (current understanding — refine in DECISIONS.md)

- **Employee**: has punches/shifts. Has a role (`Employee`, `Manager`/`Admin`).
- **Shift / TimeEntry**: `ClockInUtc` (required), `ClockOutUtc` (nullable = open shift), the Zurich
  offset at each punch, the time source/provider for each punch, and audit fields.
- **Storage**: save instants as UTC (`datetimeoffset` or `datetime2` + offset). Convert to
  `Europe/Zurich` only for display and for day/week grouping. **Durations are always computed from UTC
  instants**, never from wall-clock times.
- **At most one open shift per employee**, enforced by a **filtered unique index**
  (`WHERE ClockOutUtc IS NULL`), not only in application code.
- Clock Out requires an open shift. Clock In requires no open shift.
  Clock Out time must be later than Clock In time.

### Edge cases to handle (and to document in DECISIONS.md)
- **DST changes in Zurich** (last Sunday of March / October): a shift across the change has a duration
  of 7h or 9h on the wall clock, but the correct duration comes from UTC. The repeated 02:00–03:00 hour
  in October must not create ambiguous records.
- **Overnight shifts** across midnight: which day does the shift belong to? (Default: the Zurich date of Clock In.)
- **Forgotten Clock Out**: shifts open longer than a max length (configurable, e.g. 16h) are flagged.
  Never auto-close them silently with a made-up time.
- **Double click / repeated requests / two tabs**: punches are idempotent (idempotency key and/or DB
  constraint + a clean 409 response). Race conditions are tested.
- **Time provider failure**: timeouts, 5xx, rate limiting, wrong/garbled response, wrong time zone in the
  response, slow responses. Use a short timeout and possibly a second provider. Fail closed.
- **Clock skew between providers** and **time going backwards** (a Clock Out earlier than its Clock In): reject it.
- Manual corrections by a manager (if built): keep the original value, who changed it, when, and why. Never edit punches in place without an audit trail.

## 3. Repository layout

```
backend/ClockManager.Api/      ASP.NET Core Web API (controllers, services, EF Core, time provider)
backend/ClockManager.Tests/    xUnit tests (unit + integration)            [to be created]
frontend/                      React 19 + Vite, JSX, oxlint; /api proxied to the backend
plans/                         Work plans, one markdown file per feature/phase (deliverable)
.claude/agents/                Subagents: backend, frontend, db, qa          [to be created]
.claude/settings.json          Shared Claude Code settings/permissions
DECISIONS.md                   Decisions, edge cases, things not done, known limits (deliverable)
README.md                      Local setup, including database setup (deliverable)
```

Suggested backend folders (inside `ClockManager.Api`): `Controllers/`, `Domain/`, `Application/`
(services, DTOs, validation), `Infrastructure/` (EF `DbContext`, migrations, time providers),
`Common/` (errors, ProblemDetails). Keep it to one API project unless a split clearly pays for itself.

## 4. Commands

Backend (port 5258, Swagger at `/swagger`):
```powershell
cd backend/ClockManager.Api; dotnet run
dotnet build backend/ClockManager.Api
dotnet test backend/ClockManager.Tests
dotnet ef migrations add <Name> --project backend/ClockManager.Api
dotnet ef database update --project backend/ClockManager.Api
```
Frontend (port 5173):
```powershell
cd frontend; npm install; npm run dev
npm run build   # must pass before you call frontend work done
npm run lint    # oxlint, must be clean
```
The dev machine is Windows (PowerShell). The default DB is SQL Server LocalDB `(localdb)\MSSQLLocalDB`.
Docker SQL Server is an optional alternative, and the README must cover both.

## 5. Coding conventions

**Backend (C#)**
- Nullable enabled; treat warnings seriously. `async` all the way down; pass `CancellationToken`.
- Thin controllers → application services → EF Core. No business logic in controllers.
- Errors are returned as **RFC 7807 ProblemDetails** with stable error codes
  (e.g. `shift.already_open`, `shift.not_open`, `time.unavailable`).
- Use `IHttpClientFactory` + typed clients for the time API, with timeouts. No `new HttpClient()`.
- Options pattern for config (`TimeProviderOptions`, `ShiftRulesOptions`), validated on startup.
- EF Core: explicit config with `IEntityTypeConfiguration<T>`, migrations committed, no `EnsureCreated` in real code paths.
- Concurrency: rely on DB constraints + catch the unique-violation → 409. Use transactions where several writes must succeed or fail together.

**Frontend (React)**
- Functional components + hooks. One small API layer (`src/api/`) using `fetch` against `/api`.
- **Never compute or send punch times from the browser.** Show the server's times, formatted in
  `Europe/Zurich` with `Intl.DateTimeFormat(..., { timeZone: 'Europe/Zurich' })`.
- Disable the punch button while a request is in flight; show server error messages; handle 409/503 clearly.
- A live "current time" display, if any, must come from the server/trusted source and be labeled as such.
- Accessible markup (labels, focus states, button semantics). Responsive layout.

**Tests**
- Backend: xUnit. Unit tests for the domain rules with a **fake trusted clock** (DST dates, midnight,
  long shifts). Integration tests against a real SQL Server/LocalDB (or Testcontainers) for constraints and concurrency.
- Every bug fix gets a test that fails without the fix.

## 6. Workflow with Claude Code

- **Plan before code** for any non-trivial feature: write `plans/NN-<feature>.md` (goal, scope,
  steps, risks/edge cases, test plan). Get approval, then build. Update the plan's status when done.
- **Small, focused commits** with clear messages (`feat(api): ...`, `fix(ui): ...`, `test: ...`,
  `docs: ...`). The commit history is graded: no huge "everything" commits.
- Before saying a task is done: backend `dotnet build` + `dotnet test`; frontend `npm run build` + `npm run lint`.
  Report failures honestly.
- **Record Claude Code corrections**: when the user corrects Claude or Claude's output had to be
  fixed, add a short entry to `docs/claude-worklog.md` (what was asked, what went wrong, how it was fixed).
  This shows where the tool helped and where it needed fixing, and is part of the deliverable.
- Keep `README.md` and `DECISIONS.md` in sync with the code in the same change.

## 7. Agents (in `.claude/agents/`)

| Agent      | Owns                                                                 |
|------------|----------------------------------------------------------------------|
| `backend`  | API endpoints, services, trusted-clock integration, validation, errors |
| `frontend` | React UI, API client, states (loading/error/offline), accessibility   |
| `db`       | Schema, EF Core config, migrations, indexes/constraints, seed data, queries |
| `qa`       | Finds bugs and risky conditions (DST, races, provider failure, bad input); writes failing tests and repro steps; does **not** fix production code |

Shared rules for all agents: follow this file, stay in your own area, and hand off across areas by
describing the needed change (endpoint contract, schema change) instead of making it yourself.
