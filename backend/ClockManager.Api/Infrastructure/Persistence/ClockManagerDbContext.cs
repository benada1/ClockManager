using ClockManager.Api.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace ClockManager.Api.Infrastructure.Persistence;

/// <summary>
/// The EF Core context. Schema rules live in the <c>Configurations/</c> classes; the database is created and changed
/// only by migrations (never <c>EnsureCreated</c>).
/// </summary>
public sealed class ClockManagerDbContext(DbContextOptions<ClockManagerDbContext> options) : DbContext(options)
{
    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<Shift> Shifts => Set<Shift>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Instant>().HaveConversion<NodaTimeConverters.InstantConverter>();
        configurationBuilder.Properties<Offset>().HaveConversion<NodaTimeConverters.OffsetMinutesConverter>();
        configurationBuilder.Properties<LocalDate>().HaveConversion<NodaTimeConverters.LocalDateConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ClockManagerDbContext).Assembly);
}
