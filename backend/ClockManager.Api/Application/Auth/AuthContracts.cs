using System.ComponentModel.DataAnnotations;
using ClockManager.Api.Domain;

namespace ClockManager.Api.Application.Auth;

/// <summary>Body of <c>POST /api/auth/signup</c>. The messages are client-facing and never echo the input.</summary>
public sealed class SignupRequest
{
    public const string UsernamePattern = @"^[A-Za-z0-9._-]{3,32}\z"; // \z, not $: "bob\n" must fail

    [Required(AllowEmptyStrings = false, ErrorMessage = "is required: 3 to 32 letters, digits, '.', '_' or '-'")]
    [RegularExpression(UsernamePattern, ErrorMessage = "must be 3 to 32 letters, digits, '.', '_' or '-'")]
    public string? Username { get; init; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "is required (1 to 128 characters)")]
    [StringLength(Employee.DisplayNameMaxLength, ErrorMessage = "must be 1 to 128 characters")]
    public string? DisplayName { get; init; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "is required (8 to 128 characters)")]
    [StringLength(128, MinimumLength = 8, ErrorMessage = "must be 8 to 128 characters")]
    public string? Password { get; init; }
}

/// <summary>Body of <c>POST /api/auth/login</c>. Only the shape is checked here; a wrong value is a 401.</summary>
public sealed class LoginRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "is required")]
    [StringLength(128, ErrorMessage = "is too long")]
    public string? Username { get; init; }

    [Required(AllowEmptyStrings = false, ErrorMessage = "is required")]
    [StringLength(128, ErrorMessage = "is too long")]
    public string? Password { get; init; }
}

/// <summary>What signup, login and me answer. No id, no hash.</summary>
public sealed record AuthUserView(string Username, string DisplayName, string Role)
{
    public static AuthUserView From(string username, string displayName, EmployeeRole role) =>
        new(username, displayName, role.ToString());
}

/// <summary>A new session: the user plus the token the controller puts in the cookie.</summary>
public sealed record AuthSession(AuthUserView User, string Token, DateTimeOffset ExpiresAt);

public sealed record IssuedToken(string Value, DateTimeOffset ExpiresAt);

public interface ITokenIssuer
{
    IssuedToken Issue(int employeeId, EmployeeRole role);
}
