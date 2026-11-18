using ClockManager.Api.Application.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClockManager.Api.Infrastructure.Persistence;

public static class PersistenceExtensions
{
    /// <summary>
    /// Registers the DbContext. A missing connection string stops the host on startup with a clear message
    /// (options validated on start), instead of failing on the first request.
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"Connection string 'ConnectionStrings:{DatabaseOptions.ConnectionStringName}' is missing. Development uses "
                + "appsettings.Development.json; other environments must set the environment variable "
                + $"'ConnectionStrings__{DatabaseOptions.ConnectionStringName}' or a user-secret (see README, Database setup).")
            .ValidateOnStart();

        services.AddDbContext<ClockManagerDbContext>((sp, builder) =>
            builder.UseSqlServer(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString));

        return services;
    }

    /// <summary>
    /// Development only, and only when <see cref="DatabaseOptions.MigrateAndSeedOnStartup"/> is on: applies pending
    /// migrations (never <c>EnsureCreated</c>), then seeds the demo employees. Other environments are migrated
    /// explicitly with <c>dotnet ef database update</c> or a migration bundle.
    /// </summary>
    public static async Task InitializeDevelopmentDatabaseAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        if (!app.Environment.IsDevelopment()) return;
        if (!app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateAndSeedOnStartup) return;

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClockManagerDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        await DevelopmentSeeder.SeedAsync(
            db,
            cancellationToken,
            scope.ServiceProvider.GetRequiredService<PasswordService>(),
            app.Configuration[DevelopmentSeeder.ManagerPasswordKey]);
    }
}
