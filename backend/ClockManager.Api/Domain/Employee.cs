namespace ClockManager.Api.Domain;

/// <summary>A person who punches in and out.</summary>
public sealed class Employee
{
    public const int UsernameMaxLength = 64;
    public const int DisplayNameMaxLength = 128;
    public const int PasswordHashMaxLength = 256;

    public Employee(string username, string displayName, EmployeeRole role)
    {
        Username = Require(username, UsernameMaxLength, nameof(username));
        DisplayName = Require(displayName, DisplayNameMaxLength, nameof(displayName));
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role.");
        Role = role;
        IsActive = true;
    }

    /// <summary>For EF Core materialization only.</summary>
    private Employee()
    {
    }

    public int Id { get; private set; }

    /// <summary>Unique (<c>UX_Employees_Username</c>). Case-insensitive under the database's default collation.</summary>
    public string Username { get; private set; } = null!;

    public string DisplayName { get; private set; } = null!;

    public EmployeeRole Role { get; private set; }

    /// <summary>A deactivated employee keeps their history but can't punch (enforced in Phase 3).</summary>
    public bool IsActive { get; private set; }

    /// <summary>PBKDF2 hash from <c>PasswordHasher</c>; null = cannot log in (seeded users). Never sent to a client.</summary>
    public string? PasswordHash { get; private set; }

    public void SetPasswordHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length > PasswordHashMaxLength)
        {
            throw new ArgumentException($"A hash of 1 to {PasswordHashMaxLength} characters is required.", nameof(hash));
        }
        PasswordHash = hash;
    }

    /// <summary>SQL Server <c>rowversion</c>, set by the database on every write.</summary>
    public byte[] RowVersion { get; private set; } = null!;

    private static string Require(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Required.", name);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength) throw new ArgumentException($"At most {maxLength} characters.", name);
        return trimmed;
    }
}
