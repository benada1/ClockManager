using ClockManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClockManager.Tests.Persistence;

/// <summary>
/// One throwaway database per test class (<c>ClockManager_Test_&lt;guid&gt;</c>), built by the REAL migrations
/// (<c>MigrateAsync</c>, never <c>EnsureCreated</c>), so every run also tests the migrations. Dropped on dispose.
/// </summary>
public sealed class MigratedDatabaseFixture : IAsyncLifetime
{
    public string DatabaseName { get; } = TestSqlServer.DatabasePrefix + Guid.NewGuid().ToString("N");

    public string ConnectionString => TestSqlServer.ForDatabase(DatabaseName);

    private bool _created;

    public ClockManagerDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ClockManagerDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        if (TestSqlServer.Unavailable is { } reason)
        {
            // Locally the tests are skipped (SqlServerFact) and never touch the fixture. In CI this is a failure.
            if (TestSqlServer.IsCi) throw new InvalidOperationException("CI=true but " + reason);
            return;
        }

        await using var db = CreateContext();
        _created = true; // set first: a migration that fails half-way still leaves a database to drop
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_created) await TestSqlServer.DropDatabaseAsync(DatabaseName);
    }
}
