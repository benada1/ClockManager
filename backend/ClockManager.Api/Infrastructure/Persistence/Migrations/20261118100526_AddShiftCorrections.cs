using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClockManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShiftCorrections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ShiftId = table.Column<long>(type: "bigint", nullable: false),
                    ManagerId = table.Column<int>(type: "int", nullable: false),
                    Field = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OldUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                    NewUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    CreatedProvider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShiftCorrections", x => x.Id);
                    table.CheckConstraint("CK_ShiftCorrections_CreatedProviderNotBlank", "LEN(LTRIM(RTRIM([CreatedProvider]))) > 0");
                    table.CheckConstraint("CK_ShiftCorrections_CreatedUtcOffsetZero", "DATEPART(TZOFFSET, [CreatedUtc]) = 0");
                    table.CheckConstraint("CK_ShiftCorrections_Field", "[Field] IN (N'ClockIn', N'ClockOut')");
                    table.CheckConstraint("CK_ShiftCorrections_NewUtcOffsetZero", "DATEPART(TZOFFSET, [NewUtc]) = 0");
                    table.CheckConstraint("CK_ShiftCorrections_OldUtcNullOnlyForClockOut", "[OldUtc] IS NOT NULL OR [Field] = N'ClockOut'");
                    table.CheckConstraint("CK_ShiftCorrections_OldUtcOffsetZero", "[OldUtc] IS NULL OR DATEPART(TZOFFSET, [OldUtc]) = 0");
                    table.CheckConstraint("CK_ShiftCorrections_ReasonNotBlank", "LEN(LTRIM(RTRIM([Reason]))) > 0");
                    table.ForeignKey(
                        name: "FK_ShiftCorrections_Employees_ManagerId",
                        column: x => x.ManagerId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ShiftCorrections_Shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "Shifts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShiftCorrections_ManagerId",
                table: "ShiftCorrections",
                column: "ManagerId");

            migrationBuilder.CreateIndex(
                name: "IX_ShiftCorrections_ShiftId",
                table: "ShiftCorrections",
                column: "ShiftId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShiftCorrections");
        }
    }
}
