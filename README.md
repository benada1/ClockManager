# Clock Manager

Employee time clock (Clock In / Clock Out). ASP.NET Core + SQL Server + React.

## Run locally

Backend (http://localhost:5258, Swagger at /swagger):

    cd backend/ClockManager.Api
    dotnet run

Frontend (http://localhost:5173, proxies /api to the backend):

    cd frontend
    npm install
    npm run dev

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
