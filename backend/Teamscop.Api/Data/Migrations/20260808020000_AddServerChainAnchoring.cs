using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Teamscop.Api.Data;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    /// <summary>
    /// B1 — moves vault continuity from a write-side counter to a server-held anchor.
    ///
    /// <c>agent_sequence_states</c> goes: <c>LastChainHash</c> was written and never read,
    /// <c>GapCount</c> could only ever increment, and <c>LastVaultSequence</c> was used to *reject*
    /// late batches, which §13.1 forbids. Anchoring now reads <c>agent_events</c> itself — already
    /// indexed by (UserId, VaultSequence) — and records only the disagreements.
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260808020000_AddServerChainAnchoring")]
    public partial class AddServerChainAnchoring : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "agent_sequence_states");

            migrationBuilder.CreateTable(
                name: "agent_chain_breaks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtSequence = table.Column<long>(type: "bigint", nullable: false),
                    KnownChainHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReportedChainHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                name: "IX_agent_chain_breaks_UserId_AtSequence",
                table: "agent_chain_breaks",
                columns: new[] { "UserId", "AtSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_chain_breaks_UserId_OccurredAt",
                table: "agent_chain_breaks",
                columns: new[] { "UserId", "OccurredAt" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "agent_chain_breaks");

            migrationBuilder.CreateTable(
                name: "agent_sequence_states",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GapCount = table.Column<long>(type: "bigint", nullable: false),
                    LastChainHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastVaultSequence = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
        }
    }
}
