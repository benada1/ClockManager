using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClockManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProviderNamesNotBlank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_Shifts_ClockInCrossCheckedByNotBlank",
                table: "Shifts",
                sql: "LEN(LTRIM(RTRIM([ClockIn_CrossCheckedBy]))) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shifts_ClockInProviderNotBlank",
                table: "Shifts",
                sql: "LEN(LTRIM(RTRIM([ClockIn_Provider]))) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shifts_ClockOutCrossCheckedByNotBlank",
                table: "Shifts",
                sql: "LEN(LTRIM(RTRIM([ClockOut_CrossCheckedBy]))) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shifts_ClockOutProviderNotBlank",
                table: "Shifts",
                sql: "LEN(LTRIM(RTRIM([ClockOut_Provider]))) > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Shifts_ClockInCrossCheckedByNotBlank",
                table: "Shifts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shifts_ClockInProviderNotBlank",
                table: "Shifts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shifts_ClockOutCrossCheckedByNotBlank",
                table: "Shifts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shifts_ClockOutProviderNotBlank",
                table: "Shifts");
        }
    }
}
