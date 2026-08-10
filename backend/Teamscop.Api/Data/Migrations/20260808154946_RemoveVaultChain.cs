using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveVaultChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_chain_breaks");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
                name: "agent_chain_breaks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtSequence = table.Column<long>(type: "bigint", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    KnownChainHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReportedChainHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_chain_breaks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_chain_breaks_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_events_UserId_VaultSequence",
                table: "agent_events",
                columns: new[] { "UserId", "VaultSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_chain_breaks_UserId_AtSequence",
                table: "agent_chain_breaks",
                columns: new[] { "UserId", "AtSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_chain_breaks_UserId_OccurredAt",
                table: "agent_chain_breaks",
                columns: new[] { "UserId", "OccurredAt" });
        }
    }
}
