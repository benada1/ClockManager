namespace ClockManager.Api.Infrastructure.Persistence;

/// <summary>
/// Database settings. The connection string comes from <c>ConnectionStrings:ClockManager</c>: the Development default
/// (SQL Express, Windows auth, no password) is in <c>appsettings.Development.json</c>; every other environment must
/// provide it through an environment variable (<c>ConnectionStrings__ClockManager</c>) or user-secrets.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public const string ConnectionStringName = "ClockManager";

    public string? ConnectionString { get; set; }

    /// <summary>
    /// Apply pending migrations and seed demo employees when the app starts. Honoured in Development only:
    /// other environments never change their schema on startup, even if this is set.
    /// </summary>
    public bool MigrateAndSeedOnStartup { get; set; }
}
