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
