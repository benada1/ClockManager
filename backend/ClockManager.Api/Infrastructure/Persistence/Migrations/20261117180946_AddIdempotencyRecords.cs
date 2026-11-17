using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClockManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Intent = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ShiftId = table.Column<long>(type: "bigint", nullable: false),
                    ResponseStatus = table.Column<int>(type: "int", nullable: false),
                    ResponseBody = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.Id);
                    table.CheckConstraint("CK_IdempotencyRecords_CreatedUtcOffsetZero", "DATEPART(TZOFFSET, [CreatedUtc]) = 0");
                    table.CheckConstraint("CK_IdempotencyRecords_Intent", "[Intent] IN (N'clock-in', N'clock-out')");
                    table.CheckConstraint("CK_IdempotencyRecords_KeyNotEmpty", "[Key] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_IdempotencyRecords_ResponseBodyJson", "ISJSON([ResponseBody]) = 1");
                    table.CheckConstraint("CK_IdempotencyRecords_ResponseStatus", "[ResponseStatus] >= 200 AND [ResponseStatus] < 300");
                    table.ForeignKey(
                        name: "FK_IdempotencyRecords_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_IdempotencyRecords_Shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "Shifts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_ShiftId",
                table: "IdempotencyRecords",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "UX_IdempotencyRecords_EmployeeId_Key",
                table: "IdempotencyRecords",
                columns: new[] { "EmployeeId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdempotencyRecords");
        }
    }
}
