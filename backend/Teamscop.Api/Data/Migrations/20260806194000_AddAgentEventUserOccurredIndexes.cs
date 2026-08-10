using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Teamscop.Api.Data;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260806194000_AddAgentEventUserOccurredIndexes")]
    public partial class AddAgentEventUserOccurredIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccessTotpFailedAttempts",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AccessTotpLockoutUntil",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AccessTotpLastUsedStepUninstall",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AccessTotpLastUsedStepUsb",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_agent_events_UserId_OccurredAt",
                table: "agent_events",
                columns: new[] { "UserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_events_UserId_EventType_OccurredAt",
                table: "agent_events",
                columns: new[] { "UserId", "EventType", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_agent_events_UserId_EventType_OccurredAt",
                table: "agent_events");

            migrationBuilder.DropIndex(
                name: "IX_agent_events_UserId_OccurredAt",
                table: "agent_events");

            migrationBuilder.DropColumn(
                name: "AccessTotpFailedAttempts",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AccessTotpLockoutUntil",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AccessTotpLastUsedStepUninstall",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AccessTotpLastUsedStepUsb",
                table: "users");
        }
    }
}
