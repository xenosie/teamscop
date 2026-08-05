using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackingVaultAndConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChainHash",
                table: "agent_events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VaultSequence",
                table: "agent_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_sequence_states",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastVaultSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastChainHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GapCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_sequence_states", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_agent_sequence_states_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_tracking_configs",
                columns: table => new
                {
                    StaffUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScreenshotQuality = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScreenshotPeriodSeconds = table.Column<int>(type: "integer", nullable: false),
                    TimeTrackEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    BrowserHistoryEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ScreenshotEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConfigVersion = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_tracking_configs", x => x.StaffUserId);
                    table.ForeignKey(
                        name: "FK_staff_tracking_configs_companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_staff_tracking_configs_users_StaffUserId",
                        column: x => x.StaffUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_events_UserId_VaultSequence",
                table: "agent_events",
                columns: new[] { "UserId", "VaultSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_tracking_configs_CompanyId",
                table: "staff_tracking_configs",
                column: "CompanyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_sequence_states");

            migrationBuilder.DropTable(
                name: "staff_tracking_configs");

            migrationBuilder.DropIndex(
                name: "IX_agent_events_UserId_VaultSequence",
                table: "agent_events");

            migrationBuilder.DropColumn(
                name: "ChainHash",
                table: "agent_events");

            migrationBuilder.DropColumn(
                name: "VaultSequence",
                table: "agent_events");
        }
    }
}
