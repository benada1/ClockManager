using ClockManager.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClockManager.Api.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>Shifts</c> with both punches as owned columns (<c>ClockIn_*</c>, <c>ClockOut_*</c>), plans/02-schema.md §4.
/// The constraints make invalid shifts unstorable, whatever the C# code does (CLAUDE.md rule 2).
/// </summary>
internal sealed class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public const string OneOpenPerEmployeeIndex = "UX_Shifts_OneOpenPerEmployee";
    public const string ClockOutAfterClockInCheck = "CK_Shifts_ClockOutAfterClockIn";
    public const string ClockOutCompleteCheck = "CK_Shifts_ClockOutComplete";

    /// <summary>
    /// Created by raw SQL in the InitialSchema migration: EF can't index a column of an owned type together with an
    /// owner column. <c>ShiftSchemaTests.Employee_work_date_index_exists</c> guards it.
    /// </summary>
    public const string EmployeeWorkDateIndex = "IX_Shifts_EmployeeId_ClockIn_ZurichDate";

    public void Configure(EntityTypeBuilder<Shift> builder)
    {
        builder.ToTable("Shifts", t =>
        {
            // Strictly greater: a zero-length shift (T11) or a clock-out before the clock-in (T10) is refused.
            t.HasCheckConstraint(ClockOutAfterClockInCheck, "[ClockOut_Utc] IS NULL OR [ClockOut_Utc] > [ClockIn_Utc]");

            // A clock-out is fully recorded or not at all. The cross-check pair may be null in a recorded clock-out (D-009).
            t.HasCheckConstraint(ClockOutCompleteCheck,
                "([ClockOut_Utc] IS NULL AND [ClockOut_ZurichOffsetMinutes] IS NULL AND [ClockOut_ZurichDate] IS NULL"
                + " AND [ClockOut_Provider] IS NULL AND [ClockOut_RoundTripMs] IS NULL AND [ClockOut_CrossCheckedBy] IS NULL"
                + " AND [ClockOut_CrossCheckDeltaMs] IS NULL AND [ClockOut_LowPrecision] IS NULL AND [ClockOut_RawPayload] IS NULL)"
                + " OR ([ClockOut_Utc] IS NOT NULL AND [ClockOut_ZurichOffsetMinutes] IS NOT NULL AND [ClockOut_ZurichDate] IS NOT NULL"
                + " AND [ClockOut_Provider] IS NOT NULL AND [ClockOut_RoundTripMs] IS NOT NULL AND [ClockOut_LowPrecision] IS NOT NULL"
                + " AND [ClockOut_RawPayload] IS NOT NULL)");

            foreach (var punch in new[] { "ClockIn", "ClockOut" })
            {
                // NULL passes a CHECK, so these hold for an open shift's ClockOut columns too.
                t.HasCheckConstraint($"CK_Shifts_{punch}Offset", $"[{punch}_ZurichOffsetMinutes] IN (60, 120)");
                t.HasCheckConstraint($"CK_Shifts_{punch}RoundTrip", $"[{punch}_RoundTripMs] >= 0");
                t.HasCheckConstraint($"CK_Shifts_{punch}UtcOffsetZero", $"DATEPART(TZOFFSET, [{punch}_Utc]) = 0");
                // A punch must name its time source (CLAUDE.md rule 1 audit trail); NOT NULL alone lets '' and '   '
                // through (QA-13). NULL passes these, so an open shift's ClockOut columns and a single-source punch are fine.
                t.HasCheckConstraint($"CK_Shifts_{punch}ProviderNotBlank", $"LEN(LTRIM(RTRIM([{punch}_Provider]))) > 0");
                t.HasCheckConstraint($"CK_Shifts_{punch}CrossCheckedByNotBlank", $"LEN(LTRIM(RTRIM([{punch}_CrossCheckedBy]))) > 0");
                // Both set or both null. "IS NOT NULL" on the delta is needed: NULL >= 0 is UNKNOWN, and a CHECK only
                // rejects FALSE, so without it "cross-checked by X, delta unknown" was stored (QA-12).
                t.HasCheckConstraint($"CK_Shifts_{punch}CrossCheck",
                    $"([{punch}_CrossCheckedBy] IS NULL AND [{punch}_CrossCheckDeltaMs] IS NULL)"
                    + $" OR ([{punch}_CrossCheckedBy] IS NOT NULL AND [{punch}_CrossCheckDeltaMs] IS NOT NULL"
                    + $" AND [{punch}_CrossCheckDeltaMs] >= 0)");
                // The stored work date must be the date of the instant at the stored offset (catches a date computed
                // from the server's local zone, or swapped columns). tzdb itself is checked in C# (PunchStamp.From).
                t.HasCheckConstraint($"CK_Shifts_{punch}ZurichDate",
                    $"[{punch}_ZurichDate] = CAST(SWITCHOFFSET([{punch}_Utc], [{punch}_ZurichOffsetMinutes]) AS date)");
            }
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).UseIdentityColumn();

        // Shifts are records: deleting an employee who has shifts must fail, never cascade.
        builder.HasOne<Employee>()
            .WithMany()
            .HasForeignKey(s => s.EmployeeId)
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_Shifts_Employees_EmployeeId");

        // At most one open shift per employee (D5): two tabs or two servers clocking in at once, the second insert fails.
        builder.HasIndex(s => s.EmployeeId)
            .IsUnique()
            .HasFilter("[ClockOut_Utc] IS NULL")
            .HasDatabaseName(OneOpenPerEmployeeIndex);

        builder.OwnsOne(s => s.ClockIn, punch => ConfigurePunch(punch, "ClockIn"));
        builder.Navigation(s => s.ClockIn).IsRequired();

        // Optional: EF reads the row as "no clock-out" when the required columns are NULL.
        builder.OwnsOne(s => s.ClockOut, punch => ConfigurePunch(punch, "ClockOut"));
        builder.Navigation(s => s.ClockOut).IsRequired(false);

        builder.Property(s => s.RowVersion).IsRowVersion();
    }

    private static void ConfigurePunch(OwnedNavigationBuilder<Shift, PunchStamp> punch, string prefix)
    {
        punch.Property(p => p.Utc).HasColumnName($"{prefix}_Utc").HasColumnType("datetimeoffset(7)");
        punch.Property(p => p.ZurichOffset).HasColumnName($"{prefix}_ZurichOffsetMinutes").HasColumnType("smallint");
        punch.Property(p => p.ZurichDate).HasColumnName($"{prefix}_ZurichDate").HasColumnType("date");
        punch.Property(p => p.Provider).HasColumnName($"{prefix}_Provider").HasMaxLength(PunchStamp.ProviderMaxLength).IsRequired();
        punch.Property(p => p.RoundTripMs).HasColumnName($"{prefix}_RoundTripMs");
        punch.Property(p => p.CrossCheckedBy).HasColumnName($"{prefix}_CrossCheckedBy").HasMaxLength(PunchStamp.ProviderMaxLength);
        punch.Property(p => p.CrossCheckDeltaMs).HasColumnName($"{prefix}_CrossCheckDeltaMs");
        punch.Property(p => p.LowPrecision).HasColumnName($"{prefix}_LowPrecision");
        punch.Property(p => p.RawPayload).HasColumnName($"{prefix}_RawPayload").HasMaxLength(PunchStamp.RawPayloadMaxLength).IsRequired();
    }
}
