using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class LifecycleTotp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastHeartbeatAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UninstallTotpEnabled",
                table: "companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UninstallTotpEnrolledAt",
                table: "companies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UninstallTotpSecret",
                table: "companies",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "uninstall_tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TicketHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Consumed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_uninstall_tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_uninstall_tickets_companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_uninstall_tickets_CompanyId",
                table: "uninstall_tickets",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_uninstall_tickets_TicketHash",
                table: "uninstall_tickets",
                column: "TicketHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "uninstall_tickets");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "UninstallTotpEnabled",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "UninstallTotpEnrolledAt",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "UninstallTotpSecret",
                table: "companies");
        }
    }
}
