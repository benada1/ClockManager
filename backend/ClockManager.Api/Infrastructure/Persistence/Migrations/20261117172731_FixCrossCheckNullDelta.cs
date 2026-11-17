using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClockManager.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixCrossCheckNullDelta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Shifts_ClockInCrossCheck",
                table: "Shifts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shifts_ClockOutCrossCheck",
                table: "Shifts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shifts_ClockInCrossCheck",
                table: "Shifts",
                sql: "([ClockIn_CrossCheckedBy] IS NULL AND [ClockIn_CrossCheckDeltaMs] IS NULL) OR ([ClockIn_CrossCheckedBy] IS NOT NULL AND [ClockIn_CrossCheckDeltaMs] IS NOT NULL AND [ClockIn_CrossCheckDeltaMs] >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shifts_ClockOutCrossCheck",
                table: "Shifts",
                sql: "([ClockOut_CrossCheckedBy] IS NULL AND [ClockOut_CrossCheckDeltaMs] IS NULL) OR ([ClockOut_CrossCheckedBy] IS NOT NULL AND [ClockOut_CrossCheckDeltaMs] IS NOT NULL AND [ClockOut_CrossCheckDeltaMs] >= 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Shifts_ClockInCrossCheck",
                table: "Shifts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Shifts_ClockOutCrossCheck",
                table: "Shifts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shifts_ClockInCrossCheck",
                table: "Shifts",
                sql: "([ClockIn_CrossCheckedBy] IS NULL AND [ClockIn_CrossCheckDeltaMs] IS NULL) OR ([ClockIn_CrossCheckedBy] IS NOT NULL AND [ClockIn_CrossCheckDeltaMs] >= 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Shifts_ClockOutCrossCheck",
                table: "Shifts",
                sql: "([ClockOut_CrossCheckedBy] IS NULL AND [ClockOut_CrossCheckDeltaMs] IS NULL) OR ([ClockOut_CrossCheckedBy] IS NOT NULL AND [ClockOut_CrossCheckDeltaMs] >= 0)");
        }
    }
}
