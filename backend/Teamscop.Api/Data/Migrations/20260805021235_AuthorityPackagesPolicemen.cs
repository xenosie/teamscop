using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Teamscop.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuthorityPackagesPolicemen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AuthorityVersion",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "IsPoliceman",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PolicemanUpdatedAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "policeman_authority_grants",
                columns: table => new
                {
                    StaffUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policeman_authority_grants", x => new { x.StaffUserId, x.PackageId });
                    table.ForeignKey(
                        name: "FK_policeman_authority_grants_users_StaffUserId",
                        column: x => x.StaffUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_CompanyId_IsPoliceman",
                table: "users",
                columns: new[] { "CompanyId", "IsPoliceman" });

            migrationBuilder.CreateIndex(
                name: "IX_policeman_authority_grants_PackageId",
                table: "policeman_authority_grants",
                column: "PackageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "policeman_authority_grants");

            migrationBuilder.DropIndex(
                name: "IX_users_CompanyId_IsPoliceman",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AuthorityVersion",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IsPoliceman",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PolicemanUpdatedAt",
                table: "users");
        }
    }
}
