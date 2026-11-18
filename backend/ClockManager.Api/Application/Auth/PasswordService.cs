using ClockManager.Api.Domain;
using Microsoft.AspNetCore.Identity;

namespace ClockManager.Api.Application.Auth;

/// <summary>PBKDF2 hashing via <see cref="IPasswordHasher{TUser}"/>. Singleton: the dummy hash is built once.</summary>
public sealed class PasswordService
{
    private readonly IPasswordHasher<Employee> _hasher;
    private readonly Employee _dummyUser = new("dummy", "Dummy", EmployeeRole.Employee);
    private readonly string _dummyHash;

    public PasswordService(IPasswordHasher<Employee> hasher)
    {
        _hasher = hasher;
        _dummyHash = hasher.HashPassword(_dummyUser, Guid.NewGuid().ToString("N"));
    }

    public string Hash(Employee employee, string password) => _hasher.HashPassword(employee, password);

    /// <summary>
    /// True only for a real employee with a hash and the right password. An unknown employee or one without a hash is checked
    /// against a dummy hash, so the time taken does not tell whether the name exists.
    /// </summary>
    public bool Verify(Employee? employee, string password)
    {
        var hash = employee?.PasswordHash;
        PasswordVerificationResult result;
        try
        {
            result = _hasher.VerifyHashedPassword(employee ?? _dummyUser, hash ?? _dummyHash, password);
        }
        catch (FormatException)
        {
            // A stored hash that is not base64 (e.g. "!" used to lock an account) is a failed login, not a 500 (QA-43).
            _hasher.VerifyHashedPassword(_dummyUser, _dummyHash, password); // same work as a normal failure
            return false;
        }
        return hash is not null && result != PasswordVerificationResult.Failed;
    }
}
