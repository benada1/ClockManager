using ClockManager.Api.Application.Employees;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Persistence;
using ClockManager.Api.Infrastructure.Persistence.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClockManager.Api.Application.Auth;

/// <summary>Sign up, log in and "who am I". The cookie itself is set by the controller.</summary>
public sealed class AuthService(
    ClockManagerDbContext db,
    PasswordService passwords,
    ITokenIssuer tokens,
    ICurrentEmployee currentEmployee,
    ILogger<AuthService> logger)
{
    private const int SqlUniqueIndexViolation = 2601;
    private const int SqlUniqueConstraintViolation = 2627;

    public async Task<AuthSession> SignupAsync(SignupRequest request, CancellationToken ct)
    {
        var username = request.Username!;
        // The unique index decides in the end; this check gives the common case a clean answer without an exception.
        if (await db.Employees.AnyAsync(e => e.Username == username, ct)) throw UsernameTaken();

        var employee = new Employee(username, request.DisplayName!, EmployeeRole.Employee); // signup never makes a Manager
        employee.SetPasswordHash(passwords.Hash(employee, request.Password!));
        db.Employees.Add(employee);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: SqlUniqueIndexViolation or SqlUniqueConstraintViolation } sql
            && sql.Message.Contains(EmployeeConfiguration.UsernameIndex, StringComparison.Ordinal))
        {
            throw UsernameTaken(); // two signups with the same name at the same moment
        }

        logger.LogInformation("Employee {EmployeeId} signed up.", employee.Id);
        return NewSession(employee);
    }

    public async Task<AuthSession> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(e => e.Username == request.Username, ct);

        if (!passwords.Verify(employee, request.Password!))
        {
            // Same answer for a wrong name, a wrong password and a user without a password.
            throw new AppException(
                ErrorCodes.AuthInvalidCredentials,
                StatusCodes.Status401Unauthorized,
                "Wrong username or password.",
                "Check the username and password and try again.");
        }

        // Only after the password was right, so an inactive account is not revealed to someone who doesn't know it.
        if (!employee!.IsActive)
        {
            throw new AppException(
                ErrorCodes.EmployeeInactive,
                StatusCodes.Status403Forbidden,
                "This employee account is deactivated.",
                "Ask a manager to reactivate the account.");
        }

        logger.LogInformation("Employee {EmployeeId} logged in.", employee.Id);
        return NewSession(employee);
    }

    public async Task<AuthUserView> GetCurrentAsync(CancellationToken ct)
    {
        var employee = await currentEmployee.RequireActiveAsync(ct);
        return AuthUserView.From(employee.Username, employee.DisplayName, employee.Role);
    }

    private AuthSession NewSession(Employee employee)
    {
        var token = tokens.Issue(employee.Id, employee.Role);
        return new AuthSession(AuthUserView.From(employee.Username, employee.DisplayName, employee.Role), token.Value, token.ExpiresAt);
    }

    private static AppException UsernameTaken() => new(
        ErrorCodes.AuthUsernameTaken,
        StatusCodes.Status409Conflict,
        "That username is already taken.",
        "Choose another username.");
}
