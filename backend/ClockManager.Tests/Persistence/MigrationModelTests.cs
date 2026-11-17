using ClockManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ClockManager.Tests.Persistence;

/// <summary>
/// S12: the C# model and the last migration's snapshot agree, so nobody can change an entity and forget the migration.
/// EF Core 8 has no <c>Database.HasPendingModelChanges()</c> (that's EF 9), so this compares the two models with EF's
/// own <see cref="IMigrationsModelDiffer"/>, the same step <c>dotnet ef migrations add</c> runs. No database needed.
/// </summary>
public sealed class MigrationModelTests
{
    [Fact]
    public void S12_model_has_no_changes_missing_from_the_migrations()
    {
        using var db = new ClockManagerDbContext(new DbContextOptionsBuilder<ClockManagerDbContext>()
            .UseSqlServer("Server=model-only;Database=model-only") // never opened
            .Options);

        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);

        var snapshotModel = snapshot.Model is IMutableModel mutable ? mutable.FinalizeModel() : snapshot.Model;
        snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel);

        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(
            snapshotModel.GetRelationalModel(),
            db.GetService<IDesignTimeModel>().Model.GetRelationalModel());

        Assert.True(differences.Count == 0,
            "The model has changes that no migration contains. Run 'dotnet ef migrations add <Name> --project backend/ClockManager.Api'. "
            + "Pending: " + string.Join(", ", differences.Select(d => d.GetType().Name)));
    }
}
