using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ClockManager.Tests.Infrastructure;

/// <summary>
/// For API hosts in tests that don't use the database (time, errors, options). The host requires a connection string
/// on startup (fail fast), so these hosts get a placeholder that is never opened. Development's migrate-and-seed is
/// switched off, so a test running the host as "Development" never touches a real database.
/// </summary>
internal static class NoDatabaseHostSettings
{
    public static IWebHostBuilder UseNoDatabase(this IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClockManager"] = "Server=no-database-in-this-test;Database=none",
            ["Database:MigrateAndSeedOnStartup"] = "false",
        }));
}
