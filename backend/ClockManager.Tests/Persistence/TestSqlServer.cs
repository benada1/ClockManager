using Microsoft.Data.SqlClient;

namespace ClockManager.Tests.Persistence;

/// <summary>
/// The SQL Server used by integration tests. From <c>CLOCKMANAGER_TEST_SQL</c> (a server-level connection string,
/// no database), default <c>.\SQLEXPRESS</c> with Windows auth (plans/02-schema.md §5).
/// Probed once per test run. Unreachable: CI (<c>CI=true</c>) fails the tests, a local run skips them with a reason.
/// The probe also drops leftover <c>ClockManager_Test_*</c> databases from crashed runs.
/// </summary>
internal static class TestSqlServer
{
    public const string EnvironmentVariable = "CLOCKMANAGER_TEST_SQL";
    public const string DatabasePrefix = "ClockManager_Test_";
    private const string DefaultConnectionString = @"Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true";

    private static readonly Lazy<string?> UnavailableReason = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string ServerConnectionString { get; } =
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv ? fromEnv : DefaultConnectionString;

    public static bool IsCi => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the server answered; otherwise why the integration tests can't run.</summary>
    public static string? Unavailable => UnavailableReason.Value;

    public static string ForDatabase(string database) =>
        new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = database }.ConnectionString;

    public static async Task DropDatabaseAsync(string database)
    {
        SqlConnection.ClearAllPools(); // pooled connections to the database would block the drop
        await using var connection = new SqlConnection(ForDatabase("master"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "IF DB_ID(@name) IS NOT NULL BEGIN "
            + "DECLARE @q nvarchar(300) = QUOTENAME(@name); "
            + "EXEC(N'ALTER DATABASE ' + @q + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + @q + N';'); END";
        command.Parameters.AddWithValue("@name", database);
        await command.ExecuteNonQueryAsync();
    }

    private static string? Probe()
    {
        var builder = new SqlConnectionStringBuilder(ForDatabase("master")) { ConnectTimeout = 5 };
        try
        {
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            DropStaleDatabases(connection);
            return null;
        }
        catch (SqlException ex)
        {
            return $"SQL Server not reachable ({builder.DataSource}): {ex.Message.Split('\n')[0]} "
                + $"Set {EnvironmentVariable} to a server connection string to run the integration tests (README, Database setup).";
        }
    }

    /// <summary>
    /// Drops test databases older than an hour (a crashed run never reached its Dispose). The age comes from the SQL
    /// server's own <c>create_date</c> and clock: housekeeping only, never a punch record. A database from a run that is
    /// still going is younger than that, so parallel runs don't drop each other's databases.
    /// </summary>
    private static void DropStaleDatabases(SqlConnection connection)
    {
        var stale = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name FROM sys.databases WHERE name LIKE 'ClockManager[_]Test[_]%' AND create_date < DATEADD(HOUR, -1, GETDATE())";
            using var reader = command.ExecuteReader();
            while (reader.Read()) stale.Add(reader.GetString(0));
        }

        foreach (var name in stale)
        {
            try
            {
                DropDatabaseAsync(name).GetAwaiter().GetResult();
            }
            catch (SqlException)
            {
                // Best effort: someone may hold it open. It will be retried on the next run.
            }
        }
    }
}
