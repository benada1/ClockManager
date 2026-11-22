# Clock Manager

Employee time clock (Clock In / Clock Out). ASP.NET Core + SQL Server + React.

**Where to look**

- `DECISIONS.md`: every decision (D-001 to D-081) with the alternatives and trade-offs, the edge cases and the test for each, what was not done, known limits, and a table of where Claude's output had to be corrected.
- `plans/`: one plan per phase (goal, scope, risks, test plan, final status). `plans/00-master-plan.md` is the overview.
- `docs/claude-worklog.md`: a short story of where Claude Code helped and where it needed fixing.
- `CLAUDE.md` and `.claude/` (agents and skills): the rules the work followed. The rule that matters most: **punch times come only from the external Europe/Zurich time API, never from a local clock**.
- `backend/ClockManager.Api` (API), `backend/ClockManager.Tests` (xUnit), `frontend/` (React).
- `.claude/agents/` (backend, frontend, db, qa) and `.claude/skills/` (`record-decision`: how decisions are written; `verify`: the quality gate script; `zurich-time-cases`: verified DST instants and the test matrix).
- Plans in order: `plans/01-trusted-clock.md`, `02-schema`, `03-punch-api`, `04-auth`, `05-ui-punch`, `06-history`, `07-manager`, `08-qa`, `09-polish`, `10-ui-redesign`.

## Prerequisites

- **.NET 8 SDK** and **Node.js 22 or newer** (CI uses 22), **PowerShell 7** (for the `verify` script) and **git**.
- A **SQL Server**: Express (default), LocalDB or Docker, see Database setup.
- One-time: `dotnet tool restore` (installs `dotnet-ef` as a repo-local tool) and `npm install` in `frontend/`.
- First run order: set up the database, start the backend, start the frontend, open http://localhost:5173 and sign up.
- The commands are PowerShell (the dev machine is Windows); CI runs the same tests on Linux. Only the SQL Express path was run by hand; LocalDB and Docker follow the same steps with another connection string.
- Punching needs two live external time providers. If both are down when you look, the punch correctly fails with a 503 and nothing is saved: check `GET /api/health/time`, then try again later.

## Run locally

Set up the database first (next section). In Development the backend applies migrations and seeds the
demo employees on startup (setting `Database:MigrateAndSeedOnStartup`, on in `appsettings.Development.json`).

Backend (http://localhost:5258, Swagger at /swagger):

    cd backend/ClockManager.Api
    dotnet run

Frontend (http://localhost:5173, proxies /api to the backend):

    cd frontend
    npm install
    npm run dev

## Database setup

The API uses SQL Server through EF Core 8. The connection string is `ConnectionStrings:ClockManager`.
Pick **one** of the three options below. Only the SQL Express default is committed, because it has no
password. Any other server goes in user-secrets or an environment variable, **never** in a committed file.

**1. SQL Server Express (default).** `appsettings.Development.json` already contains:

    Server=.\SQLEXPRESS;Database=ClockManager;Integrated Security=true;TrustServerCertificate=true

Check that the instance exists and is running (the service is `MSSQL$SQLEXPRESS`):

    Get-Service 'MSSQL$*'
    sqlcmd -S .\SQLEXPRESS -E -C -Q "SELECT @@SERVERNAME, @@VERSION"

If your instance has a different name, override the connection string with user-secrets (see option 3).

**2. LocalDB.** Install it with Visual Studio or the SQL Server Express installer ("LocalDB" feature), then:

    sqllocaldb create MSSQLLocalDB      # skip if `sqllocaldb info` already lists it
    sqllocaldb start MSSQLLocalDB
    cd backend/ClockManager.Api
    dotnet user-secrets set "ConnectionStrings:ClockManager" "Server=(localdb)\MSSQLLocalDB;Database=ClockManager;Integrated Security=true;TrustServerCertificate=true"

**3. Docker** (SQL Server 2022 in a Linux container). Pick your own strong `sa` password and keep it
out of git:

    $env:MSSQL_SA_PASSWORD = 'choose-a-Str0ng-password!'   # this shell only
    docker run -d --name clockmanager-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$env:MSSQL_SA_PASSWORD" -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
    cd backend/ClockManager.Api
    dotnet user-secrets set "ConnectionStrings:ClockManager" "Server=localhost,1433;Database=ClockManager;User Id=sa;Password=$env:MSSQL_SA_PASSWORD;TrustServerCertificate=true"

**Create the schema.** Run this from the repo root. `dotnet-ef` is pinned in `.config/dotnet-tools.json`, so
everyone uses the same version:

    dotnet tool restore
    dotnet ef database update --project backend/ClockManager.Api

Starting the API in Development (`dotnet run`) does the same, then seeds 1 manager (`manager`) and
3 employees (`alice`, `bob`, `carol`). Seeding is idempotent and adds no shifts. **Other environments never
migrate themselves.** Set `ConnectionStrings__ClockManager` as an environment variable and run
`dotnet ef database update` (or a migration bundle) as a deployment step. If the connection string is
missing, the API refuses to start and says so.

**Integration tests** run against a real SQL Server. Each test class gets its own throwaway database
(`ClockManager_Test_<guid>`), built by the real migrations and dropped afterwards. They use
`CLOCKMANAGER_TEST_SQL`, a server-level connection string with no database name, or
`Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true` by default. The login must be able to
create databases.

    $env:CLOCKMANAGER_TEST_SQL = 'Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true'
    dotnet test backend/ClockManager.sln --filter Category=Integration

If no server is reachable, the integration tests are **skipped** locally (the reason is shown), but with
`CI=true` they **fail**. GitHub Actions starts a SQL Server 2022 container with a password generated in each
run, so the integration tests run on every push (D-026).

## Try the app by hand

**Note:** besides the tests and mock scenarios, the author has gone through all the screens by hand in the browser. Repeat this list after UI changes.

Start the backend and the frontend (see Run locally), open http://localhost:5173 and check by hand:
1. **Sign up** with a new username, then you land on the Clock page (signup logs you in). The big clock and the greeting come from the server's trusted time; with the providers down it says "Server time unavailable" instead of showing the PC's time.
2. **Clock in:** the card shows "On shift" and "Shift started at HH:mm · running for H:MM:SS".
3. **Second tab:** open the app in another tab and press Clock in there: you get a clear "already clocked in" message.
4. **Clock out:** the card shows "Clocked out and saved", the clock-out time and how long you worked; "Last shift" and "This week" update.
5. **Time provider down:** turn off the network and punch: you see "can't get a trusted time", nothing is saved, and Try again works later.
6. **Log out**, then try the punch URL again: you are sent to the login page.
7. **History:** open History in the header: today's shift is listed with its duration, and the week total matches.
8. **Manager** (after setting the manager password, see Login): "Needs review" (with filter chips) and "People" load, and Correct on a shift asks for a reason and then shows "Corrected by a manager" in that employee's History.

9. **Theme:** the sun/moon button in the top bar switches light and dark; a first visit is always light, and the choice survives a reload (D-082).

`npm run dev:mock` (in `frontend/`) shows every screen state without the backend, e.g. `?mock=forgotten`, `?mock=unavailable`, `?mock=conflict`, `?mock=expired`, `?mock=notime` (no server time: no clock).

## Login and signup

Sign up and log in with `POST /api/auth/signup` and `POST /api/auth/login` (body `{ username, displayName, password }` or
`{ username, password }`). The answer sets a **JWT in an HTTP-only cookie** (`clockmanager_auth`, 8 hours, no refresh),
so scripts in the browser can't read it. `POST /api/auth/logout` clears it and `GET /api/auth/me` says who you are.
Signup always creates an **Employee**; the manager account exists only through the seed (D-049..D-053).

- **Signing key:** set it once, it is never stored in git (Development makes a random one per run, so logins end when
  the server restarts, which is also why a restart logs you out; any other environment refuses to start without it):

      cd backend/ClockManager.Api
      dotnet user-secrets set "Auth:SigningKey" "<at least 32 random characters>"

- **Demo manager login (optional, Development only):** `dotnet user-secrets set "Seed:ManagerPassword" "<password>"`, **restart the backend** (the seed runs at startup), then
  log in as `manager`. The other seeded users (alice, bob, carol) have no password: create your own users with signup.
- **Rate limits** (`RateLimits` section, D-054): 10 logins/signups per minute per IP and 60 punches per minute per user;
  more gives 429 `request.rate_limited` with a `Retry-After` header.

## Punching (API)

`POST /api/punches` clocks the caller in or out. The body is **only** the intent; the server takes the time from
the trusted clock. Any other field (e.g. `"time"`) is rejected with 400 (`DECISIONS.md` D-029).

You must be logged in (see Login above); without the cookie every punch is a 401. Each punch also needs an
`Idempotency-Key` header with a new UUID. Swagger at http://localhost:5258/swagger shows the header.

**Retrying a punch:** if the response is lost (or you double-click), send the **same** `Idempotency-Key` again. The
server answers with the original result (same status and body, no second punch, no new time lookup), even if the shift
has been closed since (D-032). Only successful punches are remembered, so a retry after a 503 or a 409 is a real new
attempt. The key belongs to one employee and one intent: reusing it for the other intent (a clock-out with a
clock-in's key) is a 422 `idempotency.key_reused`. Use a new UUID for every new punch.

    # log in once (or sign up); the session cookie is kept in $s
    $body = '{"username":"alice2","displayName":"Alice","password":"a-long-password"}'
    Invoke-RestMethod -Method Post -Uri http://localhost:5258/api/auth/signup -SessionVariable s `
        -ContentType 'application/json' -Body $body

    $headers = @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }
    Invoke-RestMethod -Method Post -Uri http://localhost:5258/api/punches -WebSession $s -Headers $headers `
        -ContentType 'application/json' -Body '{"intent":"clock-in"}'

A 201 returns the shift: `workDate` (Zurich date of the clock-in), `clockIn` / `clockOut` with `utc`, `zurich`
(with its offset), `provider`, `crossCheckedBy`, `lowPrecision`, `durationSeconds` (from the UTC instants) and
`flags` (`long_shift`, `short_shift`). Errors are ProblemDetails with a stable `code`: 409 `shift.already_open` /
`shift.not_open`, 422 `idempotency.key_reused`, 503 `time.*` (no trusted time, or `time.went_backwards`), 400 `request.invalid` /
`idempotency.key_missing`, 401 `auth.unauthenticated`, 403 `employee.inactive`.

**Status:** `GET /api/me/status` (needs the login cookie, no `Idempotency-Key`) returns the caller's state for
the UI: `employee`, `openShift` and `lastClosedShift` (same shape as a punch response, or `null`), `serverNow` and the
top-level `flags`.

    Invoke-RestMethod -Uri http://localhost:5258/api/me/status -WebSession $s | ConvertTo-Json -Depth 6

`serverNow` (`utc`, `zurich`, `source: "trusted-clock"`, `ageMs`) is the trusted time **for display only**: it is cached
for up to 10 s (`ServerNow:CacheSeconds`, 1-30), so `utc` is the instant as it was fetched and `ageMs` says how old it is.
The UI estimates "now" as `utc + ageMs + its own elapsed time`; it is never used for a punch, which always asks the
providers afresh (D-035). The flag `forgotten_clock_out` appears when the open shift is older than `ShiftRules:MaxOpenShiftHours`
(16 h, strictly). If no trusted time is available, the endpoint still answers 200 with `serverNow: null` and no time-based
flag (D-036). Errors: 401 `auth.unauthenticated`, 403 `employee.inactive`.

## History

`GET /api/me/shifts?from=2026-11-16&to=2026-11-22` (login cookie, Zurich dates, at most 31 days) returns your shifts grouped by day
with day and week totals counted from the UTC instants. Without `from` and `to` it returns the Monday to Sunday week of your latest
shift. A shift stays on the day it started (`endsNextDay` marks overnight ones), an open shift is listed but not counted, and a day
with a clock change is noted (D-064..D-066). In the app, open **History** in the header.

## Manager view

Log in as a **Manager** (only through the seed: `dotnet user-secrets set "Seed:ManagerPassword" "<password>"`, then user `manager`) and
open **Manager** in the header. You see the shifts that need review (forgotten clock-outs, unusually long or
short shifts, with filter chips by kind) and the people on the team (who is clocked in, warnings). Open an employee's week or a shift and press **Correct** to fix a clock-in or clock-out time: a reason is required, the time must be
in the past and must not overlap another shift, and only a forgotten shift can be closed. The original value, the new value, who changed it, when
(trusted clock) and why are kept for ever, and the employee sees "Corrected by a manager" in their History (D-067..D-075).

## Tests and quality gate

    dotnet test backend/ClockManager.sln                                   # backend; the integration tests need SQL Server
    dotnet test backend/ClockManager.sln --filter "Category!=Integration"  # backend without a database
    cd frontend; npm test                                                  # frontend logic tests (Vitest)

    # the CI re-run in another time zone and culture (proves nothing depends on the server's settings)
    $env:TZ='Pacific/Kiritimati'; $env:CLOCKMANAGER_TEST_TZ='Pacific/Kiritimati'; $env:CLOCKMANAGER_TEST_CULTURE='th-TH'
    dotnet test backend/ClockManager.sln

    # everything CI checks: backend build + tests, frontend build + lint + tests, clock guard
    pwsh -NoProfile -File .claude/skills/verify/verify.ps1

Stop a running `dotnet run` before building: it locks the files in `bin/`.

Using the local clock (`DateTime.Now`, `DateTimeOffset.UtcNow`, `TimeProvider.System`, ...) in API code
is a **build error** (RS0030, see `DECISIONS.md` D-001). Punch times come only from the trusted time provider.

## Production

This repository covers development and CI. `npm run build` creates `frontend/dist`, but nothing in the backend serves it, so a real
deployment needs a web server in front that serves those files and forwards `/api` to the API over **HTTPS** (the login cookie is
`Secure` only over HTTPS; behind a proxy that ends TLS the API needs forwarded headers configured). Not built: see DECISIONS "Not done".

## Trusted time

Every punch asks two free external providers for the current Europe/Zurich time, in parallel, and
cross-checks them (`DECISIONS.md` D-006, D-008): **time.now** (primary) and **timeapi.io** (secondary).
No API keys are needed. If neither gives a trustworthy answer, or they disagree by more than 2 s, the
punch fails with **503** (`time.unavailable`, `time.invalid_response` or `time.providers_disagree`).
There is no fallback to the server clock. Settings: section `TrustedClock` in `appsettings.json`.

Connection warm-up (D-037): after startup the API makes one background request per provider, only to open
the connection, so the first punch is not slow; the answer is thrown away and never used as a time.
Keys: `TrustedClock:Warmup:Enabled` / `TimeoutMs`, and the pool settings under `TrustedClock:Connection`
(`PooledConnectionLifetimeSeconds`, `PooledConnectionIdleTimeoutSeconds`, `ConnectTimeoutMs`,
`KeepAlivePingDelaySeconds`, `KeepAlivePingTimeoutSeconds`). Set `TrustedClock:Warmup:Enabled=false` to run
offline. The tests turn it off, so no test touches the network.

- **`GET http://localhost:5258/health/time`** (all environments; also at `/api/health/time`, which the Vite dev proxy can reach): 200 when a punch would get a time
  right now, 503 otherwise, with each provider's status code and round trip. Cached for 10 s (D-012).
- **Live provider tests** (skipped by default, so CI never touches the network):

      $env:CLOCKMANAGER_LIVE_TESTS = '1'
      dotnet test backend/ClockManager.sln --filter Category=Live
      Remove-Item Env:CLOCKMANAGER_LIVE_TESTS
