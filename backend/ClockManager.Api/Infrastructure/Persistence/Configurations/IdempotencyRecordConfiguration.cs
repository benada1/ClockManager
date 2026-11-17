using ClockManager.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClockManager.Api.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>IdempotencyRecords</c> (plans/03-punch-api.md §5, D-032). Every column is NOT NULL, so no CHECK below needs an extra
/// <c>IS NOT NULL</c> (the QA-12 lesson: a CHECK only rejects FALSE, and a comparison with NULL is UNKNOWN).
/// </summary>
internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public const string EmployeeKeyIndex = "UX_IdempotencyRecords_EmployeeId_Key";
    public const string IntentCheck = "CK_IdempotencyRecords_Intent";
    public const string CreatedUtcOffsetZeroCheck = "CK_IdempotencyRecords_CreatedUtcOffsetZero";
    public const string ResponseStatusCheck = "CK_IdempotencyRecords_ResponseStatus";
    public const string ResponseBodyJsonCheck = "CK_IdempotencyRecords_ResponseBodyJson";
    public const string KeyNotEmptyCheck = "CK_IdempotencyRecords_KeyNotEmpty";

    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords", t =>
        {
            t.HasCheckConstraint(IntentCheck, "[Intent] IN (N'clock-in', N'clock-out')");
            // Only successful punches are stored (D-032).
            t.HasCheckConstraint(ResponseStatusCheck, "[ResponseStatus] >= 200 AND [ResponseStatus] < 300");
            t.HasCheckConstraint(ResponseBodyJsonCheck, "ISJSON([ResponseBody]) = 1");
            t.HasCheckConstraint(KeyNotEmptyCheck, "[Key] <> '00000000-0000-0000-0000-000000000000'");
            // Same style as CK_Shifts_*UtcOffsetZero: instants are stored at +00:00 only.
            t.HasCheckConstraint(CreatedUtcOffsetZeroCheck, "DATEPART(TZOFFSET, [CreatedUtc]) = 0");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).UseIdentityColumn();

        builder.Property(r => r.Key).IsRequired();
        builder.Property(r => r.Intent).HasMaxLength(16).IsRequired();
        builder.Property(r => r.ResponseStatus).IsRequired();
        builder.Property(r => r.ResponseBody).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(r => r.CreatedUtc).HasColumnType("datetimeoffset(7)").IsRequired();

        // The idempotency guarantee itself: two requests with the same key can't both be stored.
        builder.HasIndex(r => new { r.EmployeeId, r.Key }).IsUnique().HasDatabaseName(EmployeeKeyIndex);

        // Records point at shifts, which are records: deleting either must fail, never cascade.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(r => r.EmployeeId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_IdempotencyRecords_Employees_EmployeeId");
        builder.HasOne<Shift>()
            .WithMany()
            .HasForeignKey(r => r.ShiftId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_IdempotencyRecords_Shifts_ShiftId");
    }
}
