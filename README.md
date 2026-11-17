# Clock Manager

Employee time clock (Clock In / Clock Out). ASP.NET Core + SQL Server + React.

## Run locally

Set up the database first (next section). In Development the backend applies migrations and seeds the
demo employees on startup.

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

## Punching (Phase 3)

`POST /api/punches` clocks the caller in or out. The body is **only** the intent; the server takes the time from
the trusted clock. Any other field (e.g. `"time"`) is rejected with 400 (`DECISIONS.md` D-029).

Until login arrives in Phase 4, the caller is named by the **Development-only** header `X-Dev-Employee`
(seeded users: `manager`, `alice`, `bob`, `carol`). In any other environment the header is ignored and every
punch is a 401 (D-027). Each punch also needs an `Idempotency-Key` header with a new UUID. Swagger at
http://localhost:5258/swagger shows both headers.

**Retrying a punch:** if the response is lost (or you double-click), send the **same** `Idempotency-Key` again. The
server answers with the original result (same status and body, no second punch, no new time lookup), even if the shift
has been closed since (D-032). Only successful punches are remembered, so a retry after a 503 or a 409 is a real new
attempt. The key belongs to one employee and one intent: reusing it for the other intent (a clock-out with a
clock-in's key) is a 422 `idempotency.key_reused`. Use a new UUID for every new punch.

    $headers = @{ 'X-Dev-Employee' = 'alice'; 'Idempotency-Key' = [guid]::NewGuid().ToString() }
    Invoke-RestMethod -Method Post -Uri http://localhost:5258/api/punches -Headers $headers `
        -ContentType 'application/json' -Body '{"intent":"clock-in"}'

    # or with curl.exe (PowerShell 7.3+ passes the JSON quotes through as written)
    curl.exe -i -X POST http://localhost:5258/api/punches -H "X-Dev-Employee: alice" `
        -H "Idempotency-Key: $([guid]::NewGuid())" -H "Content-Type: application/json" -d '{"intent":"clock-out"}'

A 201 returns the shift: `workDate` (Zurich date of the clock-in), `clockIn` / `clockOut` with `utc`, `zurich`
(with its offset), `provider`, `crossCheckedBy`, `lowPrecision`, `durationSeconds` (from the UTC instants) and
`flags` (`long_shift`, `short_shift`). Errors are ProblemDetails with a stable `code`: 409 `shift.already_open` /
`shift.not_open`, 422 `idempotency.key_reused`, 503 `time.*` (no trusted time, or `time.went_backwards`), 400 `request.invalid` /
`idempotency.key_missing`, 401 `auth.unauthenticated`, 403 `employee.inactive`.

**Status:** `GET /api/me/status` (same `X-Dev-Employee` header, no `Idempotency-Key`) returns the caller's state for
the UI: `employee`, `openShift` and `lastClosedShift` (same shape as a punch response, or `null`), `serverNow` and the
top-level `flags`.

    Invoke-RestMethod -Uri http://localhost:5258/api/me/status -Headers @{ 'X-Dev-Employee' = 'alice' } | ConvertTo-Json -Depth 6

`serverNow` (`utc`, `zurich`, `source: "trusted-clock"`, `ageMs`) is the trusted time **for display only**: it is cached
for up to 10 s (`ServerNow:CacheSeconds`, 1-30), so `utc` is the instant as it was fetched and `ageMs` says how old it is.
The UI estimates "now" as `utc + ageMs + its own elapsed time`; it is never used for a punch, which always asks the
providers afresh (D-035). The flag `forgotten_clock_out` appears when the open shift is older than `ShiftRules:MaxOpenShiftHours`
(16 h, strictly). If no trusted time is available, the endpoint still answers 200 with `serverNow: null` and no time-based
flag (D-036). Errors: 401 `auth.unauthenticated`, 403 `employee.inactive`.

## Tests and quality gate

    dotnet test backend/ClockManager.sln

    # everything CI checks: backend build + tests, frontend build + lint, clock guard
    pwsh -NoProfile -File .claude/skills/verify/verify.ps1

Stop a running `dotnet run` before building: it locks the files in `bin/`.

Using the local clock (`DateTime.Now`, `DateTimeOffset.UtcNow`, `TimeProvider.System`, ...) in API code
is a **build error** (RS0030, see `DECISIONS.md` D-001). Punch times come only from the trusted time provider.

## Trusted time

Every punch asks two free external providers for the current Europe/Zurich time, in parallel, and
cross-checks them (`DECISIONS.md` D-006, D-008): **time.now** (primary) and **timeapi.io** (secondary).
No API keys are needed. If neither gives a trustworthy answer, or they disagree by more than 2 s, the
punch fails with **503** (`time.unavailable`, `time.invalid_response` or `time.providers_disagree`).
There is no fallback to the server clock. Settings: section `TrustedClock` in `appsettings.json`.

- **`GET http://localhost:5258/health/time`** (all environments): 200 when a punch would get a time
  right now, 503 otherwise, with each provider's status code and round trip. Cached for 10 s (D-012).
- **Time lab** (Development only, D-013): http://localhost:5173/#/time-lab with an "Ask both providers"
  button, backed by `GET /api/dev/time-lab`. It shows each provider's raw answer, round trip, parsed
  UTC / Zurich time, the cross-check and what a punch would get. It is a 404 outside Development.
  To see every UI state without the backend, run `npm run dev:mock` in `frontend/` and open e.g.
  http://localhost:5173/?mock=disagree#/time-lab (scenarios: accepted, single, lowprecision, dst,
  disagree, down, invalid, notdev, servererror, unreachable; D-017).
- **Live provider tests** (skipped by default, so CI never touches the network):

      $env:CLOCKMANAGER_LIVE_TESTS = '1'
      dotnet test backend/ClockManager.sln --filter Category=Live
      Remove-Item Env:CLOCKMANAGER_LIVE_TESTS
