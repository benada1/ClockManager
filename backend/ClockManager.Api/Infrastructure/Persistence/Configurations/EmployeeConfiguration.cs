using ClockManager.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClockManager.Api.Infrastructure.Persistence.Configurations;

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public const string UsernameIndex = "UX_Employees_Username";

    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("Employees", t =>
            t.HasCheckConstraint("CK_Employees_Role", "[Role] IN (N'Employee', N'Manager')"));

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityColumn();

        builder.Property(e => e.Username).HasMaxLength(Employee.UsernameMaxLength).IsRequired();
        builder.HasIndex(e => e.Username).IsUnique().HasDatabaseName(UsernameIndex);

        builder.Property(e => e.DisplayName).HasMaxLength(Employee.DisplayNameMaxLength).IsRequired();

        // Text, not an int: readable in SQL and in reports, and the CHECK above lists the allowed values.
        builder.Property(e => e.Role).HasConversion<string>().HasMaxLength(16).IsUnicode().IsRequired();

        // DEFAULT 1 is for rows inserted by hand. ValueGeneratedNever makes EF always send the value: otherwise EF would
        // treat false (the CLR default) as "not set" and the DB default would silently turn a deactivated employee active.
        builder.Property(e => e.IsActive).IsRequired().HasDefaultValue(true).ValueGeneratedNever();

        // Nullable: seeded employees have no password and cannot log in.
        builder.Property(e => e.PasswordHash).HasMaxLength(Employee.PasswordHashMaxLength);

        builder.Property(e => e.RowVersion).IsRowVersion();
    }
}
