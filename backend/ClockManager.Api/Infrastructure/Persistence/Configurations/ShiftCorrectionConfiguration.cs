using ClockManager.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClockManager.Api.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>ShiftCorrections</c> (plans/07-manager.md §4): the audit trail of manager corrections, insert-only.
/// OldUtc is the only nullable column, so its CHECKs say what NULL means explicitly (QA-12: NULL passes a CHECK).
/// </summary>
internal sealed class ShiftCorrectionConfiguration : IEntityTypeConfiguration<ShiftCorrection>
{
    public const string FieldCheck = "CK_ShiftCorrections_Field";
    public const string OldOnlyNullForClockOutCheck = "CK_ShiftCorrections_OldUtcNullOnlyForClockOut";
    public const string OldUtcOffsetZeroCheck = "CK_ShiftCorrections_OldUtcOffsetZero";
    public const string NewUtcOffsetZeroCheck = "CK_ShiftCorrections_NewUtcOffsetZero";
    public const string CreatedUtcOffsetZeroCheck = "CK_ShiftCorrections_CreatedUtcOffsetZero";
    public const string ReasonNotBlankCheck = "CK_ShiftCorrections_ReasonNotBlank";
    public const string CreatedProviderNotBlankCheck = "CK_ShiftCorrections_CreatedProviderNotBlank";
    public const string ShiftIdIndex = "IX_ShiftCorrections_ShiftId";

    public void Configure(EntityTypeBuilder<ShiftCorrection> builder)
    {
        builder.ToTable("ShiftCorrections", t =>
        {
            t.HasCheckConstraint(FieldCheck, "[Field] IN (N'ClockIn', N'ClockOut')");
            // NULL old value = an open shift was closed; a clock-in always had a value before.
            t.HasCheckConstraint(OldOnlyNullForClockOutCheck, "[OldUtc] IS NOT NULL OR [Field] = N'ClockOut'");
            t.HasCheckConstraint(OldUtcOffsetZeroCheck, "[OldUtc] IS NULL OR DATEPART(TZOFFSET, [OldUtc]) = 0");
            t.HasCheckConstraint(NewUtcOffsetZeroCheck, "DATEPART(TZOFFSET, [NewUtc]) = 0");
            t.HasCheckConstraint(CreatedUtcOffsetZeroCheck, "DATEPART(TZOFFSET, [CreatedUtc]) = 0");
            t.HasCheckConstraint(ReasonNotBlankCheck, "LEN(LTRIM(RTRIM([Reason]))) > 0");
            t.HasCheckConstraint(CreatedProviderNotBlankCheck, "LEN(LTRIM(RTRIM([CreatedProvider]))) > 0");
        });

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).UseIdentityColumn();

        builder.Property(c => c.Field).HasConversion<string>().HasMaxLength(16).IsUnicode().IsRequired();
        builder.Property(c => c.OldUtc).HasColumnType("datetimeoffset(7)");
        builder.Property(c => c.NewUtc).HasColumnType("datetimeoffset(7)").IsRequired();
        builder.Property(c => c.Reason).HasMaxLength(ShiftCorrection.ReasonMaxLength).IsRequired();
        builder.Property(c => c.CreatedUtc).HasColumnType("datetimeoffset(7)").IsRequired();
        builder.Property(c => c.CreatedProvider).HasMaxLength(PunchStamp.ProviderMaxLength).IsRequired();

        // Audit rows point at records: deleting a shift or a manager must fail, never cascade.
        builder.HasOne<Shift>()
            .WithMany()
            .HasForeignKey(c => c.ShiftId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_ShiftCorrections_Shifts_ShiftId");
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(c => c.ManagerId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_ShiftCorrections_Employees_ManagerId");

        builder.HasIndex(c => c.ShiftId).HasDatabaseName(ShiftIdIndex);
    }
}
