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
