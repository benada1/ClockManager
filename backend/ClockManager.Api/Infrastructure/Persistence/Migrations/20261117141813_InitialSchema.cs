using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClockManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Employees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Username = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                    table.CheckConstraint("CK_Employees_Role", "[Role] IN (N'Employee', N'Manager')");
                });

            migrationBuilder.CreateTable(
                name: "Shifts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    ClockIn_Utc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    ClockIn_ZurichOffsetMinutes = table.Column<short>(type: "smallint", nullable: false),
                    ClockIn_ZurichDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ClockIn_Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClockIn_RoundTripMs = table.Column<int>(type: "int", nullable: false),
                    ClockIn_CrossCheckedBy = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ClockIn_CrossCheckDeltaMs = table.Column<int>(type: "int", nullable: true),
                    ClockIn_LowPrecision = table.Column<bool>(type: "bit", nullable: false),
                    ClockIn_RawPayload = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    ClockOut_Utc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    ClockOut_ZurichOffsetMinutes = table.Column<short>(type: "smallint", nullable: true),
                    ClockOut_ZurichDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ClockOut_Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ClockOut_RoundTripMs = table.Column<int>(type: "int", nullable: true),
                    ClockOut_CrossCheckedBy = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ClockOut_CrossCheckDeltaMs = table.Column<int>(type: "int", nullable: true),
                    ClockOut_LowPrecision = table.Column<bool>(type: "bit", nullable: true),
                    ClockOut_RawPayload = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shifts", x => x.Id);
                    table.CheckConstraint("CK_Shifts_ClockInCrossCheck", "([ClockIn_CrossCheckedBy] IS NULL AND [ClockIn_CrossCheckDeltaMs] IS NULL) OR ([ClockIn_CrossCheckedBy] IS NOT NULL AND [ClockIn_CrossCheckDeltaMs] >= 0)");
                    table.CheckConstraint("CK_Shifts_ClockInOffset", "[ClockIn_ZurichOffsetMinutes] IN (60, 120)");
                    table.CheckConstraint("CK_Shifts_ClockInRoundTrip", "[ClockIn_RoundTripMs] >= 0");
                    table.CheckConstraint("CK_Shifts_ClockInUtcOffsetZero", "DATEPART(TZOFFSET, [ClockIn_Utc]) = 0");
                    table.CheckConstraint("CK_Shifts_ClockInZurichDate", "[ClockIn_ZurichDate] = CAST(SWITCHOFFSET([ClockIn_Utc], [ClockIn_ZurichOffsetMinutes]) AS date)");
                    table.CheckConstraint("CK_Shifts_ClockOutAfterClockIn", "[ClockOut_Utc] IS NULL OR [ClockOut_Utc] > [ClockIn_Utc]");
                    table.CheckConstraint("CK_Shifts_ClockOutComplete", "([ClockOut_Utc] IS NULL AND [ClockOut_ZurichOffsetMinutes] IS NULL AND [ClockOut_ZurichDate] IS NULL AND [ClockOut_Provider] IS NULL AND [ClockOut_RoundTripMs] IS NULL AND [ClockOut_CrossCheckedBy] IS NULL AND [ClockOut_CrossCheckDeltaMs] IS NULL AND [ClockOut_LowPrecision] IS NULL AND [ClockOut_RawPayload] IS NULL) OR ([ClockOut_Utc] IS NOT NULL AND [ClockOut_ZurichOffsetMinutes] IS NOT NULL AND [ClockOut_ZurichDate] IS NOT NULL AND [ClockOut_Provider] IS NOT NULL AND [ClockOut_RoundTripMs] IS NOT NULL AND [ClockOut_LowPrecision] IS NOT NULL AND [ClockOut_RawPayload] IS NOT NULL)");
                    table.CheckConstraint("CK_Shifts_ClockOutCrossCheck", "([ClockOut_CrossCheckedBy] IS NULL AND [ClockOut_CrossCheckDeltaMs] IS NULL) OR ([ClockOut_CrossCheckedBy] IS NOT NULL AND [ClockOut_CrossCheckDeltaMs] >= 0)");
                    table.CheckConstraint("CK_Shifts_ClockOutOffset", "[ClockOut_ZurichOffsetMinutes] IN (60, 120)");
                    table.CheckConstraint("CK_Shifts_ClockOutRoundTrip", "[ClockOut_RoundTripMs] >= 0");
                    table.CheckConstraint("CK_Shifts_ClockOutUtcOffsetZero", "DATEPART(TZOFFSET, [ClockOut_Utc]) = 0");
                    table.CheckConstraint("CK_Shifts_ClockOutZurichDate", "[ClockOut_ZurichDate] = CAST(SWITCHOFFSET([ClockOut_Utc], [ClockOut_ZurichOffsetMinutes]) AS date)");
                    table.ForeignKey(
                        name: "FK_Shifts_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "UX_Employees_Username",
                table: "Employees",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Shifts_OneOpenPerEmployee",
                table: "Shifts",
                column: "EmployeeId",
                unique: true,
                filter: "[ClockOut_Utc] IS NULL");

            // Hand-written: EF can't model an index over an owner column (EmployeeId) plus an owned-type column
            // (ClockIn_ZurichDate). Serves "an employee's shifts by work day/week" (Phase 6) and the FK lookup.
            // Guarded by ShiftSchemaTests.Employee_work_date_index_exists (the model differ doesn't see it).
            migrationBuilder.Sql(
                "CREATE INDEX [IX_Shifts_EmployeeId_ClockIn_ZurichDate] ON [Shifts] ([EmployeeId], [ClockIn_ZurichDate]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX [IX_Shifts_EmployeeId_ClockIn_ZurichDate] ON [Shifts];");

            migrationBuilder.DropTable(
                name: "Shifts");

            migrationBuilder.DropTable(
                name: "Employees");
        }
    }
}
