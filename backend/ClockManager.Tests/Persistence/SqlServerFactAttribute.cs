namespace ClockManager.Tests.Persistence;

/// <summary>
/// A test that needs a real SQL Server (<see cref="TestSqlServer"/>). Skipped with the reason when no server is
/// reachable on a developer machine. Never skipped in CI (<c>CI=true</c>): there the fixture fails instead, so a
/// broken database setup can't turn the constraint tests silently green. Combine with <c>[Trait("Category", "Integration")]</c>.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!TestSqlServer.IsCi && TestSqlServer.Unavailable is { } reason)
        {
            Skip = reason;
        }
    }
}
