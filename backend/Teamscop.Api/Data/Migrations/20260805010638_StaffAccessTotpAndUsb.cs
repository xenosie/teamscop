using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class StaffAccessTotpAndUsb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AccessTotpEnabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AccessTotpEnrolledAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccessTotpSecret",
                table: "users",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "usb_session_tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeviceInstanceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Consumed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usb_session_tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_usb_session_tickets_companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_usb_session_tickets_users_DeviceUserId",
                        column: x => x.DeviceUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_usb_session_tickets_CompanyId",
                table: "usb_session_tickets",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_usb_session_tickets_DeviceUserId",
                table: "usb_session_tickets",
                column: "DeviceUserId");

            migrationBuilder.CreateIndex(
                name: "IX_usb_session_tickets_TicketHash",
                table: "usb_session_tickets",
                column: "TicketHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usb_session_tickets");

            migrationBuilder.DropColumn(
                name: "AccessTotpEnabled",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AccessTotpEnrolledAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AccessTotpSecret",
                table: "users");
        }
    }
}
