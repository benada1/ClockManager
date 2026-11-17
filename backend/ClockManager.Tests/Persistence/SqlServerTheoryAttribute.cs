namespace ClockManager.Tests.Persistence;

/// <summary>The <see cref="TheoryAttribute"/> twin of <see cref="SqlServerFactAttribute"/> (same skip rule).</summary>
public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (!TestSqlServer.IsCi && TestSqlServer.Unavailable is { } reason)
        {
            Skip = reason;
        }
    }
}
